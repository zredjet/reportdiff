"""位置ずれ探索の計測・比較用コピーをoutへ生成する。製品ソースは変更しない。"""
from pathlib import Path
import hashlib
import json
import shutil
import sys

root = Path(__file__).resolve().parents[2]
if len(sys.argv) > 2:
    raise SystemExit('使い方: experiments.py [未使用のout配下のコピー先]')
base = Path(sys.argv[1]).resolve() if len(sys.argv) == 2 else root / 'out/perf2k-search/variants'
if base.exists() or not base.is_relative_to(root / 'out'):
    raise SystemExit('存在しないリポジトリ内out配下を指定してください。')

for variant in ['baseline', 'trace', 'linear', 'inner2', 'inner4', 'inner4trace']:
    dest = base / variant
    for project in ['Core', 'Pdf', 'Report', 'Cli']:
        target = dest / f'src/ReportDiff.{project}'
        target.mkdir(parents=True)
        for source in (root / f'src/ReportDiff.{project}').iterdir():
            if source.is_file():
                shutil.copy2(source, target / source.name)

    def change(file, before, after):
        path = dest / f'src/ReportDiff.Core/{file}.cs'
        text = path.read_text()
        if text.count(before) != 1:
            raise RuntimeError(f'試作パッチ対象が一意でありません: {file}: {before}')
        path.write_text(text.replace(before, after))

    if variant in ['trace', 'inner4trace'] or variant.startswith('inner'):
        (dest / 'src/ReportDiff.Core/PipelineMetrics.cs').write_text('''using System.Collections.Concurrent;
using System.Diagnostics;
namespace ReportDiff.Core;
public static class PipelineMetrics
{
    private static readonly ConcurrentQueue<object> events = new();
    private static long origin = Stopwatch.GetTimestamp();
    public static void Reset() { events.Clear(); origin = Stopwatch.GetTimestamp(); }
    public static object[] Snapshot() => events.ToArray();
    public static void Add(string kind, long start, object data) => events.Enqueue(new {
        kind, start_ms = Stopwatch.GetElapsedTime(origin, start).TotalMilliseconds,
        elapsed_ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds, data });
}
''')
    if variant == 'trace':
        change('GroupShiftSearch', 'using System.Runtime.CompilerServices;',
               'using System.Runtime.CompilerServices;\nusing System.Diagnostics;')
        change('GroupShiftSearch', '[] shifts, byte[] raw)', '[] shifts, byte[] raw, int group = 0)')
        change('GroupShiftSearch', '        var threshold =', '''        var started = Stopwatch.GetTimestamp();
        var events = new List<object>();
        var totalPixels = 0;
        foreach (var run in runs) totalPixels += run.Right - run.Left;
        var threshold =''')
        change('GroupShiftSearch', '            var candidateCount = 0;', '''            var shiftStarted = Stopwatch.GetTimestamp();
            var scanned = 0; var touchedRuns = 0; var edgePixels = 0;
            var lastX = -1; var lastY = -1;
            var candidateCount = 0;''')
        change('GroupShiftSearch', '''                for (var x = run.Left; x < run.Right && candidateCount < bestCount; x++)
                    if (IsCandidate(a, b, row + x, sourceRow + Math.Clamp(x - dx, 0, width - 1), threshold, tolerance))
                        candidateCount++;''', '''                var x = run.Left;
                for (; x < run.Right && candidateCount < bestCount; x++)
                    if (IsCandidate(a, b, row + x, sourceRow + Math.Clamp(x - dx, 0, width - 1), threshold, tolerance))
                        candidateCount++;
                var visited = x - run.Left;
                scanned += visited; touchedRuns++;
                if (run.Left - dx < 0 || run.Right - 1 - dx >= width) edgePixels += visited;
                lastX = x - 1; lastY = run.Y;''')
        change('GroupShiftSearch', '            // 同点は', '''            events.Add(new { index, dx, dy, cutoff = bestCount, count = candidateCount,
                scanned, touched_runs = touchedRuns, edge_run_pixels = edgePixels, last_x = lastX, last_y = lastY,
                elapsed_ms = Stopwatch.GetElapsedTime(shiftStarted).TotalMilliseconds });
            // 同点は''')
        change('GroupShiftSearch', '        return new(bestCount, best);', '''        PipelineMetrics.Add("search.group", started, new { group, pixels = totalPixels, runs = runs.Length,
            initial = initialCount, remaining = bestCount, best_shift = best, candidates = events,
            output_scanned = bestCount == 0 ? 0 : totalPixels });
        return new(bestCount, best);''')
        change('TolerantDifference', 'options, index.Runs(group), index.InitialCount(group), shifts, raw);',
               'options, index.Runs(group), index.InitialCount(group), shifts, raw, group);')
    if variant == 'linear':
        before = '''                for (var x = run.Left; x < run.Right && candidateCount < bestCount; x++)
                    if (IsCandidate(a, b, row + x, sourceRow + Math.Clamp(x - dx, 0, width - 1), threshold, tolerance))
                        candidateCount++;'''
        after = '''                if (run.Left - dx >= 0 && run.Right - 1 - dx < width)
                {
                    var sourceOffset = sourceRow - row - dx;
                    var end = row + run.Right;
                    for (var pixel = row + run.Left; pixel < end && candidateCount < bestCount; pixel++)
                        if (IsCandidate(a, b, pixel, pixel + sourceOffset, threshold, tolerance)) candidateCount++;
                }
                else
                {
'''+before+'''
                }'''
        change('GroupShiftSearch', before, after)
    if variant.startswith('inner'):
        change('GroupShiftSearch', '    private static bool IsCandidate(', '    internal static bool IsCandidate(')
        source = (root / 'tools/ReportDiff.SearchBenchmark/InnerGroupShiftSearch.cs.txt').read_text()
        if variant == 'inner2':
            source = source.replace('const int Degree = 4;', 'const int Degree = 2;')
        if variant == 'inner4trace':
            source = source.replace('const bool Trace = false;', 'const bool Trace = true;')
        # const falseの分岐はコンパイルで除かれる。到達不能コード警告は試作内だけで抑制。
        (dest / 'src/ReportDiff.Core/InnerGroupShiftSearch.cs').write_text('#pragma warning disable CS0162\n' + source)
        change('TolerantDifference', '''        schedule.Run(group =>
        {''', '''        // 大グループの候補内並列と、残りの独立グループ並列を段階順に実行する。
        var large = Enumerable.Range(1, count - 1).Where(g => index.InitialCount(g) > 0
            && index.PixelCount(g) >= 1_000_000 && Environment.ProcessorCount > 1).ToHashSet();
        foreach (var group in large)
            results[group] = InnerGroupShiftSearch.Evaluate(ownedA, ownedB, width, height, options,
                index.Runs(group).ToArray(), index.InitialCount(group), shifts, raw, group);
        schedule.Run(group =>
        {
            if (large.Contains(group)) return;''')
    probe = dest / 'Probe'
    probe.mkdir()
    shutil.copy2(root / 'tools/ReportDiff.PipelineBenchmark/Program.cs', probe / 'Program.cs')
    (probe / 'Probe.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>ReportDiff.Tests</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="../src/ReportDiff.Cli/ReportDiff.Cli.csproj" /></ItemGroup></Project>''')
    synthetic = dest / 'Synthetic'
    synthetic.mkdir()
    shutil.copy2(root / 'tools/ReportDiff.SearchBenchmark/Synthetic.cs', synthetic / 'Program.cs')
    (synthetic / 'Synthetic.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>ReportDiff.Tests</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="../src/ReportDiff.Core/ReportDiff.Core.csproj" /></ItemGroup></Project>''')
    manifest = {str(p.relative_to(dest)): hashlib.sha256(p.read_bytes()).hexdigest()
                for p in sorted(dest.rglob('*')) if p.is_file()}
    (dest / 'source-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(dest)
