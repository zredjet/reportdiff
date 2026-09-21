"""PDF解析の調査コピー。製品ソースを変更せず、パッチ対象が異なる場合は停止する。"""
from pathlib import Path
import hashlib
import json
import shutil
import sys

root = Path(__file__).resolve().parents[2]
base = Path(sys.argv[1]).resolve()
if base.exists() or not base.is_relative_to(root/'out'):
    raise SystemExit('コピー先は未使用のリポジトリ内out配下を指定してください。')
for variant in ['baseline', 'pair2', 'trace', 'pair2trace']:
    dest = base/variant
    for name in ['Core', 'Pdf', 'Report', 'Cli']:
        source = root/f'src/ReportDiff.{name}'
        target = dest/f'src/ReportDiff.{name}'
        target.mkdir(parents=True)
        for p in source.iterdir():
            if p.is_file(): shutil.copy2(p, target/p.name)
    def change(path, before, after):
        p = dest/path
        text = p.read_text()
        if text.count(before) != 1:
            raise RuntimeError(f'パッチ対象が一意でありません: {path}: {before}')
        p.write_text(text.replace(before, after))
    cli = Path('src/ReportDiff.Cli/ComparisonRunner.cs')
    pdf = Path('src/ReportDiff.Pdf/PdfTextReader.cs')
    probe = dest/'Probe'
    probe.mkdir()
    shutil.copy2(root/'tools/ReportDiff.PipelineBenchmark/Program.cs', probe/'Program.cs')
    (probe/'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>ReportDiff.Tests</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="../src/ReportDiff.Cli/ReportDiff.Cli.csproj" /></ItemGroup></Project>\n')
    if variant.startswith('pair2'):
        shutil.copy2(root/'tools/ReportDiff.TextBenchmark/PairTrial.cs.txt', dest/'src/ReportDiff.Cli/PdfPairTrial.cs')
        change(pdf, 'private static readonly NearestNeighbourWordExtractor WordExtractor',
                    'private readonly NearestNeighbourWordExtractor WordExtractor')
        change(cli, '''                var textA = a.Annotate(page.PageNumber, normalized.OriginalSizeA, comparison.Clusters, parameters.Exclude);
                var textB = b.Annotate(page.PageNumber, normalized.OriginalSizeB, comparison.Clusters, parameters.Exclude, appliedShift);''', '''                PageTextAnnotations? textA = null, textB = null;
                PdfPairTrial.Run(a.Format == InputFormat.Pdf && b.Format == InputFormat.Pdf && comparison.Clusters.Count > 0,
                    () => textA = a.Annotate(page.PageNumber, normalized.OriginalSizeA, comparison.Clusters, parameters.Exclude),
                    () => textB = b.Annotate(page.PageNumber, normalized.OriginalSizeB, comparison.Clusters, parameters.Exclude, appliedShift));''')
        change(cli, '''            if (page.HasA) writer.AddFontWarnings(page.PageNumber, "A", a.InspectFonts(page.PageNumber));
            if (page.HasB) writer.AddFontWarnings(page.PageNumber, "B", b.InspectFonts(page.PageNumber));''', '''            IReadOnlyList<PdfFontWarning> fontsA = [], fontsB = [];
            PdfPairTrial.Run(page.HasA && page.HasB && a.Format == InputFormat.Pdf && b.Format == InputFormat.Pdf,
                () => { if (page.HasA) fontsA = a.InspectFonts(page.PageNumber); },
                () => { if (page.HasB) fontsB = b.InspectFonts(page.PageNumber); });
            if (page.HasA) writer.AddFontWarnings(page.PageNumber, "A", fontsA);
            if (page.HasB) writer.AddFontWarnings(page.PageNumber, "B", fontsB);''')
        change(Path('Probe/Program.cs'), '''        var textA=a.Annotate(page,normalized.OriginalSizeA,comparison.Clusters,parameters.Exclude);Mark("text-a",page);
        var textB=b.Annotate(page,normalized.OriginalSizeB,comparison.Clusters,parameters.Exclude,shift);Mark("text-b",page);''', '''        PageTextAnnotations? textA = null, textB = null;
        PdfPairTrial.Run(a.Format == InputFormat.Pdf && b.Format == InputFormat.Pdf && comparison.Clusters.Count > 0,
            () => textA = a.Annotate(page, normalized.OriginalSizeA, comparison.Clusters, parameters.Exclude),
            () => textB = b.Annotate(page, normalized.OriginalSizeB, comparison.Clusters, parameters.Exclude, shift));
        Mark("text-pair", page);''')
        change(Path('Probe/Program.cs'), '''    if(page<=a.PageCount)writer.AddFontWarnings(page,"A",a.InspectFonts(page));
    if(page<=b.PageCount)writer.AddFontWarnings(page,"B",b.InspectFonts(page));''', '''    IReadOnlyList<PdfFontWarning> fontsA = [], fontsB = [];
    PdfPairTrial.Run(page <= a.PageCount && page <= b.PageCount && a.Format == InputFormat.Pdf && b.Format == InputFormat.Pdf,
        () => { if (page <= a.PageCount) fontsA = a.InspectFonts(page); },
        () => { if (page <= b.PageCount) fontsB = b.InspectFonts(page); });
    if (page <= a.PageCount) writer.AddFontWarnings(page, "A", fontsA);
    if (page <= b.PageCount) writer.AddFontWarnings(page, "B", fontsB);''')
    if 'trace' in variant:
        (dest/'src/ReportDiff.Core/PipelineMetrics.cs').write_text('''using System.Collections.Concurrent;
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
        elapsed_ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
        thread = Environment.CurrentManagedThreadId, data });
}
''')
        change(pdf, 'using OpenCvSharp;', 'using System.Diagnostics;\nusing OpenCvSharp;')
        change(pdf, '    private FileStream? stream;', '''    private static int nextReader;
    private readonly int readerId = Interlocked.Increment(ref nextReader);
    private FileStream? stream;''')
        change(pdf, '                stream = File.OpenRead(path);', '                var started = Stopwatch.GetTimestamp();\n                stream = File.OpenRead(path);')
        change(pdf, '                document = PdfDocument.Open(stream);', '                document = PdfDocument.Open(stream);\n                PipelineMetrics.Add("text.open", started, new { readerId, pageNumber });')
        change(pdf, '            cachedPage = document.GetPage(pageNumber);', '''            var started = Stopwatch.GetTimestamp();
            cachedPage = document.GetPage(pageNumber);
            PipelineMetrics.Add("text.page", started, new { readerId, pageNumber, letters = cachedPage.Letters.Count });''')
        change(pdf, '            var words = page.GetWords(WordExtractor)', '            var wordsStarted = Stopwatch.GetTimestamp();\n            var words = page.GetWords(WordExtractor)')
        change(pdf, '            if (words.Length > options.MaxWordsPerPage)', '            PipelineMetrics.Add("text.words", wordsStarted, new { readerId, pageNumber, count = words.Length });\n            if (words.Length > options.MaxWordsPerPage)')
        change(pdf, '            var sx = originalSize.Width / page.Width;', '            var mapStarted = Stopwatch.GetTimestamp();\n            var sx = originalSize.Width / page.Width;')
        change(pdf, '            return TextAnnotations.Create(mapped, clusters, exclusions, dpi, options);', '''            PipelineMetrics.Add("text.map", mapStarted, new { readerId, pageNumber, words = mapped.Count });
            var matchStarted = Stopwatch.GetTimestamp();
            var result = TextAnnotations.Create(mapped, clusters, exclusions, dpi, options);
            PipelineMetrics.Add("text.match", matchStarted, new { readerId, pageNumber, clusters = clusters.Count });
            return result;''')
        change(pdf, '            return cachedFontWarnings ??= new PdfFontInspector(document!).Inspect(page);', '''            var started = Stopwatch.GetTimestamp();
            var result = cachedFontWarnings ??= new PdfFontInspector(document!).Inspect(page);
            PipelineMetrics.Add("text.fonts", started, new { readerId, pageNumber, warnings = result.Count });
            return result;''')
    if variant in ['baseline', 'pair2']:
        checks = dest/'Checks'; checks.mkdir()
        shutil.copy2(root/'tools/ReportDiff.TextBenchmark/TrialChecks.cs', checks/'Program.cs')
        for name in ['PdfFixture.cs', 'PdfTextFixture.cs', 'PdfFontFixture.cs']:
            shutil.copy2(root/'tests/ReportDiff.Tests'/name, checks/name)
        constant = '<DefineConstants>PAIR2</DefineConstants>' if variant == 'pair2' else ''
        (checks/'Checks.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>ReportDiff.Tests</AssemblyName>{constant}</PropertyGroup><ItemGroup><ProjectReference Include="../src/ReportDiff.Cli/ReportDiff.Cli.csproj" /></ItemGroup></Project>\n')
    manifest = {str(p.relative_to(dest)): hashlib.sha256(p.read_bytes()).hexdigest()
                for p in sorted(dest.rglob('*')) if p.is_file()}
    (dest/'source-manifest.json').write_text(json.dumps(manifest, indent=2)+'\n')
    print(dest)
