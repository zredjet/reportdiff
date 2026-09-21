using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;

[assembly: SupportedOSPlatform("windows")]
[assembly: SupportedOSPlatform("macOS")]

if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
if (args.Length is < 3 or > 5) throw new ArgumentException("入力A 入力B 空の出力先 [dpi=300] [最大ページ数] を指定してください。");
var settings = new AppSettings { Dpi = args.Length > 3 ? int.Parse(args[3]) : 300, ImageDpi = args.Length > 3 ? int.Parse(args[3]) : 300 };
var detail = typeof(ComparisonParameters).Assembly.GetType("ReportDiff.Core.PipelineMetrics");
var reset = detail?.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static);
var snapshot = detail?.GetMethod("Snapshot", BindingFlags.Public | BindingFlags.Static);
var stages = new List<object>();var pages = new List<object>();
var timer = Stopwatch.StartNew();var previous = 0.0;var previousAllocated = GC.GetTotalAllocatedBytes(false);
Mark("start",0);
using var a = new ComparisonInput(args[0], settings);using var b = new ComparisonInput(args[1], settings);
var writer = new ReportWriter(args[2], new(a.Describe(),b.Describe()), settings.ToReportConfiguration(), true);
Mark("open",0);
var last = Math.Max(a.PageCount,b.PageCount);
if(args.Length>4)last=Math.Min(last,int.Parse(args[4]));
if(last<1)throw new ArgumentOutOfRangeException(nameof(last));
for(var page=1;page<=last;page++)
{
    reset?.Invoke(null,null);
    if(page>Math.Min(a.PageCount,b.PageCount))
    {
        using var image=(page<=a.PageCount?a:b).ReadPage(page);Mark("render",page);
        writer.AddUnpairedPage(page,image.Pixels);Mark("images",page);
    }
    else
    {
        using var imageA=a.ReadPage(page);Mark("render-a",page);
        using var imageB=b.ReadPage(page);Mark("render-b",page);
        using var normalized=PageNormalizer.Normalize(imageA.Pixels,imageB.Pixels);Mark("normalize",page);
        var parameters=settings.ForPage(page,a.Dpi);
        var alignment=GlobalAligner.Estimate(normalized.A,normalized.B,parameters,settings.Align,normalized.SizeMismatch);
        var shift=alignment.Status=="applied"?alignment.EstimatedShiftPx:null;
        using var correctedB=shift is null?null:GlobalAligner.TranslateB(normalized.B,shift);
        Mark("alignment",page);
        var timings=new ComparisonTimings();
        using var comparison=PageComparer.Compare(normalized.A,correctedB??normalized.B,parameters,true,timings);
        Mark("compare",page);
        var textA=a.Annotate(page,normalized.OriginalSizeA,comparison.Clusters,parameters.Exclude);Mark("text-a",page);
        var textB=b.Annotate(page,normalized.OriginalSizeB,comparison.Clusters,parameters.Exclude,shift);Mark("text-b",page);
        writer.AddComparedPage(page,normalized,comparison,a.Dpi,textA,textB,alignment,correctedB);Mark("images",page);
        pages.Add(new { page,width=normalized.A.Width,height=normalized.A.Height,status=comparison.Status,clusters=comparison.Clusters.Count,timings });
    }
    if(page<=a.PageCount)writer.AddFontWarnings(page,"A",a.InspectFonts(page));
    if(page<=b.PageCount)writer.AddFontWarnings(page,"B",b.InspectFonts(page));
    Mark("fonts",page);
    if(snapshot is not null)stages.Add(new { page,stage="detail",events=snapshot.Invoke(null,null) });
    // 計測データの取り出しを次ページの描画時間に含めない。
    previous=timer.Elapsed.TotalMilliseconds;previousAllocated=GC.GetTotalAllocatedBytes(false);
}
var report=writer.Complete();Mark("json",0);
HtmlReportWriter.Write(args[2],report);Mark("html",0);
Console.WriteLine(JsonSerializer.Serialize(new { instrumented=detail is not null,elapsed_ms=timer.Elapsed.TotalMilliseconds,stages,pages,report.Summary },new JsonSerializerOptions {WriteIndented=true}));
void Mark(string stage,int page)
{
    var elapsed=timer.Elapsed.TotalMilliseconds;var allocated=GC.GetTotalAllocatedBytes(false);
    stages.Add(new {stage,page,elapsed_ms=elapsed,stage_ms=elapsed-previous,allocated_bytes=allocated-previousAllocated,
        gc_heap_bytes=GC.GetTotalMemory(false),gen0=GC.CollectionCount(0),gen1=GC.CollectionCount(1),gen2=GC.CollectionCount(2)});
    previous=elapsed;previousAllocated=allocated;
}
