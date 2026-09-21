using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

[assembly: SupportedOSPlatform("windows")]
[assembly: SupportedOSPlatform("macOS")]

Console.OutputEncoding = Encoding.UTF8;
if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
if (args.Length == 4 && args[0] == "repeat-pdf")
{
    var count = int.Parse(args[3]);
    if (count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count));
    using var document = PdfDocument.Open(File.ReadAllBytes(args[1]));
    if (document.NumberOfPages != 1) throw new ArgumentException("反復元は1ページのPDFにしてください。");
    var builder = new PdfDocumentBuilder();
    for (var page = 0; page < count; page++) builder.AddPage(document, 1);
    using var destination = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write);
    destination.Write(builder.Build());
    return;
}
if (args.Length != 5 || args[0] != "probe")
    throw new ArgumentException("repeat-pdf 入力PDF 未使用出力PDF ページ数、または probe 入力A 入力B 空の出力先 dpi を指定してください。");
var settings = new AppSettings { Dpi = int.Parse(args[4]), ImageDpi = int.Parse(args[4]) };
using var process = Process.GetCurrentProcess();
var stages = new List<object>();
var pages = new List<object>();
var timer = Stopwatch.StartNew();
var previous = 0.0;
Mark("start", 0);
ReportDocument report;
using (var a = new ComparisonInput(args[1], settings))
using (var b = new ComparisonInput(args[2], settings))
{
    if (a.PageCount != b.PageCount || a.Dpi != b.Dpi)
        throw new ArgumentException("この計測は両入力のページ数・DPIが同じ場合に限定します。");
    var writer = new ReportWriter(args[3], new(a.Describe(), b.Describe()), settings.ToReportConfiguration(), true);
    Mark("open", 0);
    for (var page = 1; page <= a.PageCount; page++)
    {
        var started = timer.Elapsed.TotalMilliseconds;
        // Matの生存期間と処理順はComparisonRunnerに合わせる。ページをまたいで画像を保持しない。
        {
            using var imageA = a.ReadPage(page); Mark("render-a", page);
            using var imageB = b.ReadPage(page); Mark("render-b", page);
            using var normalized = PageNormalizer.Normalize(imageA.Pixels, imageB.Pixels); Mark("normalize", page);
            var parameters = settings.ForPage(page, a.Dpi);
            var alignment = GlobalAligner.Estimate(normalized.A, normalized.B, parameters, settings.Align, normalized.SizeMismatch);
            var shift = alignment.Status == "applied" ? alignment.EstimatedShiftPx : null;
            using var correctedB = shift is null ? null : GlobalAligner.TranslateB(normalized.B, shift);
            Mark("alignment", page);
            var timings = new ComparisonTimings();
            using var comparison = PageComparer.Compare(normalized.A, correctedB ?? normalized.B, parameters, true, timings);
            Mark("compare", page);
            var textA = a.Annotate(page, normalized.OriginalSizeA, comparison.Clusters, parameters.Exclude); Mark("text-a", page);
            var textB = b.Annotate(page, normalized.OriginalSizeB, comparison.Clusters, parameters.Exclude, shift); Mark("text-b", page);
            writer.AddComparedPage(page, normalized, comparison, a.Dpi, textA, textB, alignment, correctedB); Mark("images", page);
            pages.Add(new { page, timings });
        }
        Mark("images-disposed", page);
        writer.AddFontWarnings(page, "A", a.InspectFonts(page));
        writer.AddFontWarnings(page, "B", b.InspectFonts(page));
        Mark("page-complete", page, timer.Elapsed.TotalMilliseconds - started);
    }
    report = writer.Complete(); Mark("json", 0);
    HtmlReportWriter.Write(args[3], report); Mark("html", 0);
}
// 結果のメタデータは製品の戻り値と同様に生存。GCは要求せず、Dispose直後を観測する。
Mark("inputs-disposed", 0);
Console.WriteLine(JsonSerializer.Serialize(new { elapsed_ms = timer.Elapsed.TotalMilliseconds, stages, pages, report.Summary },
    new JsonSerializerOptions { WriteIndented = true }));

void Mark(string stage, int page, double? pageMs = null)
{
    var elapsed = timer.Elapsed.TotalMilliseconds;
    process.Refresh();
    var gc = GC.GetGCMemoryInfo();
    stages.Add(new
    {
        stage, page, elapsed_ms = elapsed, stage_ms = elapsed - previous, page_ms = pageMs,
        rss_bytes = process.WorkingSet64,
        managed_estimate_bytes = GC.GetTotalMemory(false),
        allocated_total_bytes = GC.GetTotalAllocatedBytes(false),
        gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
        last_gc_index = gc.Index, last_gc_generation = gc.Generation,
        last_gc_heap_bytes = gc.HeapSizeBytes, last_gc_committed_bytes = gc.TotalCommittedBytes,
        last_gc_fragmented_bytes = gc.FragmentedBytes, last_gc_pause_ms = gc.PauseDurations.ToArray().Sum(t => t.TotalMilliseconds)
    });
    // 段階時間はスナップショット処理を除外する。page_msにはこの観測負荷を含む。
    previous = timer.Elapsed.TotalMilliseconds;
}
