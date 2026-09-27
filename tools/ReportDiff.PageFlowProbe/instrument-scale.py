#!/usr/bin/env python3
"""out配下の製品コピーだけに段階計測を追加する。タイマーの親子関係も保存する。"""
from pathlib import Path
import hashlib
import json
import shutil
import sys

root=Path(__file__).resolve().parents[2]
dest=Path(sys.argv[1]).resolve()
if dest.exists() or not dest.is_relative_to(root/'out'):
    raise ValueError('out配下の未使用のコピー先を指定してください。')
hashes={}
for name in ['Core','Pdf','Report','Cli']:
    source=root/f'src/ReportDiff.{name}'
    shutil.copytree(source,dest/f'src/ReportDiff.{name}',ignore=shutil.ignore_patterns('bin','obj'))
    for p in source.glob('*.cs'):hashes[str(p.relative_to(root))]=hashlib.sha256(p.read_bytes()).hexdigest()
def replace(project,name,before,after):
    p=dest/f'src/ReportDiff.{project}/{name}.cs'; s=p.read_text()
    if s.count(before)!=1:raise ValueError(f'計測箇所が一意ではありません: {name}: {before}')
    p.write_text(s.replace(before,after))
def expr(project,name,label,text):replace(project,name,text,f'FlowMetrics.Measure("{label}", () => {text})')
(dest/'src/ReportDiff.Core/FlowMetrics.cs').write_text('''using System.Diagnostics;
using System.Text.Json;
namespace ReportDiff.Core;
// 計測コピー専用。inclusive時間を親子で加算しない。
public static class FlowMetrics
{
    private sealed record Entry(int Id, int? Parent, string Stage, double Ms);
    private static readonly List<Entry> events = [];
    private static int sequence;
    [ThreadStatic] private static int? parent;
    public static T Measure<T>(string stage, Func<T> action)
    {
        var start = Stopwatch.GetTimestamp(); var id = Interlocked.Increment(ref sequence);
        var previous = parent; parent = id;
        try { return action(); }
        finally {
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds; parent = previous;
            lock (events) events.Add(new(id, previous, stage, elapsed));
        }
    }
    public static void Measure(string stage, Action action) => Measure(stage, () => { action(); return 0; });
    public static void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(events.OrderBy(e => e.Id),
        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
}
''')
replace('Pdf','PdfReader','    public LoadedImage ReadPage(int pageNumber, int dpi = 300)\n    {',
        '''    public LoadedImage ReadPage(int pageNumber, int dpi = 300) =>
        ReportDiff.Core.FlowMetrics.Measure("pdf_render", () => ReadPageMeasured(pageNumber, dpi));
    private LoadedImage ReadPageMeasured(int pageNumber, int dpi)
    {''')
expr('Cli','ComparisonInput','pdf_text','RowTextAnnotations.ToAligned(text.ReadRowWords(page, map.SizeOf(side), Dpi), map, side)')
expr('Cli','ComparisonRunner','flow_prepare','PageFlowRun.Prepare(a, b, plan, settings, command.Pages is not null, progress)')
expr('Cli','PageFlowRun','flow_plan','PageFlowPlan.PrepareForPages(document, ReadOriginal, n => settings.ForPage(n, a.Dpi), settings.Rows, selectionLimited)')
expr('Cli','PageFlowRun','flow_adoption','''PageFlowAdoption.Evaluate(run.Plan, page.PageNumber, ia, correctedB ?? ib, settings.ForPage(page.PageNumber, a.Dpi), settings.Rows,
                a.ReadRowWords(page.PageNumber, map, PageSpace.A),
                b.ReadRowWords(page.PageNumber, map, PageSpace.B), settings.Text.MinLineOverlap)''')
expr('Cli','PageFlowRun','flow_collect','collector.Add(new(side, page.PageNumber), image.Pixels, text, settings.Text.MinLineOverlap)')
expr('Cli','PageFlowRun','flow_collect_aligned','collector.AddAligned(new(side, page.PageNumber), image.Pixels, shift, text, settings.Text.MinLineOverlap)')
expr('Cli','PageFlowRun','flow_global_align','GlobalAligner.Estimate(ia.Pixels, ib.Pixels, settings.ForPage(page.PageNumber, a.Dpi), settings.Align)')
expr('Core','PageFlowPlan','flow_inference','PageFlowInference.Find(document, options, dpi)')
expr('Core','PageFlowPlan','flow_band_verification','''PageFlowBandVerifier.Verify(original, descriptors[original.Source.Page].Original, descriptors[original.Target.Page].Original,
                    source, target, parameters[original.Source.Page.Page], parameters[original.Target.Page.Page])''')
expr('Core','PageFlowPlan','flow_surface','PageFlowSurface.Create(la, lb, a, b, verified, parameters[number], numeric)')
expr('Core','PageFlowPlan','flow_numeric_rows','PageFlowNumericRows.Find(document, inference, selectionLimited, n => parameters[n], options)')
expr('Core','PageFlowPlan','flow_ambiguity','''PageFlowAmbiguity.Find(document, inference, options, dpi, selectionLimited,
            PageFlowLimits.MaximumDescriptorBytes - baseline.Usage.DescriptorBytes)''')
expr('Core','PageFlowPlan','flow_comparison','''PageFlowComparison.Create(page, Pages.Single(p => p.Number == page).Built!.Surface!, a, b, parameters[page],
            document.Pages.Single(p => p.Key == new PageFlowPageKey(PageSpace.B, page)).GlobalMap)''')
expr('Core','PageFlowAdoption','adoption_baseline','PageComparer.Compare(a, b, parameters)')
for label,text in [
 ('normalize','PageNormalizer.Normalize(imageA.Pixels, imageB.Pixels)'),
 ('global_align','GlobalAligner.Estimate(normalized.A, normalized.B, parameters, settings.Align, normalized.SizeMismatch)'),
 ('final_flow_compare','flow.Plan!.Compare(page.PageNumber, normalized.A, correctedB ?? normalized.B)'),
 ('row_compare','''RowComparer.Compare(normalized.A, correctedB ?? normalized.B, parameters, settings.Rows,
                    () => (wordsA = a.ReadRowWords(page.PageNumber, map, PageSpace.A), wordsB = b.ReadRowWords(page.PageNumber, map, PageSpace.B)),
                    pdfPair: a.Format == InputFormat.Pdf && b.Format == InputFormat.Pdf, originalSizesEqual: !normalized.SizeMismatch,
                    minLineOverlap: settings.Text.MinLineOverlap, globalMap: map)'''),
 ('save_page','''writer.AddComparedPage(page.PageNumber, normalized, comparison, a.Dpi, textA, textB, alignment, correctedB,
                    new(rows, displayA, displayB, structureA, structureB, carry, carry is null ? null : surface, flow?.Adoptions.GetValueOrDefault(page.PageNumber)))'''),
 ('save_json','writer.Complete(flow?.Complete())'),
 ('save_html','HtmlReportWriter.Write(output, report)')]:expr('Cli','ComparisonRunner',label,text)
for side in ['A','B']:
 expr('Cli','ComparisonRunner','save_flow_bands',f'flow?.VerifyAndSave(writer, page.PageNumber, PageSpace.{side}, image{side}.Pixels)')
replace('Cli','Program','return CliApplication.Run(args, Console.Out, Console.Error);',
        '''var result = ReportDiff.Core.FlowMetrics.Measure("cli", () => CliApplication.Run(args, Console.Out, Console.Error));
if (Environment.GetEnvironmentVariable("REPORTDIFF_FLOW_METRICS") is { } path) ReportDiff.Core.FlowMetrics.Write(path);
return result;''')
(dest/'source-hashes.json').write_text(json.dumps(hashes,indent=2)+'\n')
print(dest)

expr('Core','PageFlowAggregation','shared_aggregation','PageFlowSharedCauses.Evaluate(input, independent, descriptorBytesRemaining)')
