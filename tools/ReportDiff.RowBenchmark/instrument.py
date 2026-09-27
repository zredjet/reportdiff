#!/usr/bin/env python3
"""製品ソースのコピーに段階別タイマーだけを追加する。製品本体は変更しない。"""
from pathlib import Path
import hashlib
import json
import shutil
import sys

root = Path(__file__).resolve().parents[2]
dest = Path(sys.argv[1]).resolve()
if dest.exists() or not dest.is_relative_to(root / 'out'):
    raise SystemExit('リポジトリのout配下に、存在しないコピー先を指定してください。')
hashes = {}
for name in ['Core', 'Pdf', 'Report', 'Cli']:
    source = root / f'src/ReportDiff.{name}'
    shutil.copytree(source, dest / f'src/ReportDiff.{name}', ignore=shutil.ignore_patterns('bin', 'obj'))
    for p in source.glob('*.cs'):
        hashes[str(p.relative_to(root))] = hashlib.sha256(p.read_bytes()).hexdigest()


def replace(project, name, before, after):
    p = dest / f'src/ReportDiff.{project}/{name}.cs'
    text = p.read_text()
    if text.count(before) != 1:
        raise RuntimeError(f'計測対象が一意ではありません: {name}: {before}')
    p.write_text(text.replace(before, after))


def expression(project, name, label, text):
    replace(project, name, text, f'RowMetrics.Measure("{label}", () => {text})')


(dest / 'src/ReportDiff.Core/RowMetrics.cs').write_text('''using System.Diagnostics;
using System.Text.Json;
namespace ReportDiff.Core;
// 計測コピー専用。イベントの親子時間を合算しない。
public static class RowMetrics
{
    private static readonly List<object> events = [];
    public static T Measure<T>(string stage, Func<T> action)
    {
        var start = Stopwatch.GetTimestamp();
        try { return action(); }
        finally { events.Add(new { stage, ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds }); }
    }
    public static void Measure(string stage, Action action) => Measure(stage, () => { action(); return 0; });
    public static void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
}
''')
expression('Core', 'RowComparison', 'baseline_compare', 'PageComparer.Compare(a, b, parameters)')
expression('Core', 'RowComparison', 'row_text', 'readText()')
expression('Core', 'RowComparison', 'row_select', 'RowSelector.Select(a, b, text.A, text.B, parameters, options, minLineOverlap)')
expression('Core', 'RowComparison', 'content_render_a', 'surface.ContentMap.Render(a, PageSpace.A)')
expression('Core', 'RowComparison', 'content_render_b', 'surface.ContentMap.Render(b, PageSpace.B)')
candidate = '''parameters.Regions.Count == 0 && parameters.Exclude.Count == 0
                ? PageComparer.Compare(ca, cb, parameters, true, retainProjection: true)
                : RegionalComparer.Compare(ca, cb, parameters with { Exclude = [] }, RegionMap.ForRows(surface, parameters), retainProjection: true)'''
expression('Core', 'RowComparison', 'candidate_compare', candidate)
expression('Core', 'RowComparison', 'display_project', 'RowProjection.Create(candidate, surface, a, b, parameters, selection.Candidate.EquivalentPositions, globalMap)')
for label, text in [
    ('open_a', 'new ComparisonInput(command.InputA, settings)'), ('open_b', 'new ComparisonInput(command.InputB, settings)'),
    ('render_a', 'a.ReadPage(page.PageNumber)'), ('render_b', 'b.ReadPage(page.PageNumber)'),
    ('normalize', 'PageNormalizer.Normalize(imageA.Pixels, imageB.Pixels)'),
    ('global_align', 'GlobalAligner.Estimate(normalized.A, normalized.B, parameters, settings.Align, normalized.SizeMismatch)'),
    ('display_render_a', 'rows.Surface?.DisplayMap.Render(normalized.A, PageSpace.A)'),
    ('display_render_b', 'rows.Surface?.DisplayMap.Render(correctedB ?? normalized.B, PageSpace.B)'),
    ('save_page', '''writer.AddComparedPage(page.PageNumber, normalized, comparison, a.Dpi, textA, textB, alignment, correctedB,
                    new(rows, displayA, displayB, structureA, structureB))'''),
    ('fonts_a', 'writer.AddFontWarnings(page.PageNumber, "A", a.InspectFonts(page.PageNumber))'),
    ('fonts_b', 'writer.AddFontWarnings(page.PageNumber, "B", b.InspectFonts(page.PageNumber))'),
    ('save_json', 'writer.Complete()'), ('save_html', 'HtmlReportWriter.Write(output, report)')]:
    expression('Cli', 'ComparisonRunner', label, text)
replace('Cli', 'ComparisonRunner', '                var textA =', '                var annotationStarted = System.Diagnostics.Stopwatch.GetTimestamp();\n                var textA =')
replace('Core', 'RowMetrics', '    public static void Write', '''    public static void Elapsed(string stage, long start) => events.Add(new { stage, ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds });
    public static void Write''')
replace('Cli', 'ComparisonRunner', '                RowMetrics.Measure("save_page",', '                RowMetrics.Elapsed("annotations", annotationStarted);\n                RowMetrics.Measure("save_page",')
replace('Cli', 'Program', 'return CliApplication.Run(args, Console.Out, Console.Error);', '''var result = CliApplication.Run(args, Console.Out, Console.Error);
if (Environment.GetEnvironmentVariable("REPORTDIFF_ROW_METRICS") is { } path) ReportDiff.Core.RowMetrics.Write(path);
return result;''')
(dest / 'source-hashes.json').write_text(json.dumps(hashes, indent=2) + '\n')
print(dest)
