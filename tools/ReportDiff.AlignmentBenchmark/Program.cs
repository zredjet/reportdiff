using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Tests;

if (args is ["--fixture", var directory])
{
    Directory.CreateDirectory(directory);
    var a = Path.Combine(directory, "旧 日本語.pdf");
    var b = Path.Combine(directory, "新 日本語.pdf");
    var same = Path.Combine(directory, "同内容.pdf");
    var yaml = Path.Combine(directory, "設定.yaml");
    File.WriteAllBytes(a, CreatePage(false, false)); File.WriteAllBytes(b, CreatePage(true, true));
    File.WriteAllBytes(same, CreatePage(false, true));
    File.WriteAllText(yaml, "# 合成 PDF の全体補正\ndpi: 144\nalign:\n  enabled: true # 有効化\n");
    var changedExit = CliApplication.Run(["compare", a, b, "--out", Path.Combine(directory, "changed"), "--config", yaml], Console.Out, Console.Error);
    var sameExit = CliApplication.Run(["compare", a, same, "--out", Path.Combine(directory, "same"), "--config", yaml], Console.Out, Console.Error);
    if (changedExit != 1 || sameExit != 0) throw new InvalidOperationException("合成 PDF の比較結果が期待と異なります。");
    return;
}
var iterations = args.Length == 1 && int.TryParse(args[0], out var parsed) ? parsed : 3;
if (iterations is < 1 or > 20) throw new ArgumentException("計測回数は 1〜20 にしてください。");
using var original = AlignmentFixture.Render(300);
using var changed = AlignmentFixture.Render(300, true);
using var shifted = AlignmentFixture.Shift(original, 29, 19);
using var shiftedChange = AlignmentFixture.Shift(changed, 29, 19);
using var blank = new Mat(original.Size(), MatType.CV_8UC3, Scalar.All(255));
var parameters = new ComparisonParameters();
var results = new List<object>();
foreach (var (name, a, b, enabled, expected) in new[]
{
    ("無効・内容変更のみ", original, changed, false, "different"),
    ("有効・全体移動のみ", original, shifted, true, "same"),
    ("有効・全体移動と内容変更", original, shiftedChange, true, "different"),
    ("有効・白紙", blank, blank, true, "same")
})
{
    var samples = new List<object>(); var elapsed = new List<double>();
    for (var i = -1; i < iterations; i++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var alignment = GlobalAligner.Estimate(a, b, parameters, new() { Enabled = enabled });
        using var corrected = alignment.Status == "applied" ? GlobalAligner.TranslateB(b, alignment.EstimatedShiftPx!) : null;
        var alignMs = watch.Elapsed.TotalMilliseconds;
        using var compared = PageComparer.Compare(a, corrected ?? b, parameters);
        watch.Stop();
        if (compared.Status != expected || compared.Clusters.Count != (expected == "same" ? 0 : 1))
            throw new InvalidOperationException("補正後の結果が期待と異なります。");
        if (corrected is not null && alignment.EstimatedShiftPx != new GlobalShift(-29, -19))
            throw new InvalidOperationException("補正量が期待と異なります。");
        if (i < 0) continue;
        elapsed.Add(watch.Elapsed.TotalMilliseconds);
        samples.Add(new { align_ms = alignMs, total_ms = watch.Elapsed.TotalMilliseconds,
            managed_allocated_bytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
            alignment, status = compared.Status, clusters = compared.Clusters.Count, raw_pixels = compared.RawPixels });
    }
    elapsed.Sort();
    results.Add(new { scenario = name, median_total_ms = elapsed[elapsed.Count / 2], samples });
    Console.Error.WriteLine($"{name}: 中央値 {elapsed[elapsed.Count / 2]:F1}ms");
}
using var process = Process.GetCurrentProcess();
Console.WriteLine(JsonSerializer.Serialize(new { generated_at = DateTimeOffset.Now,
    os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    runtime = Environment.Version.ToString(), iterations, width = original.Width, height = original.Height,
    dpi = 300, glyphs = 600, process_peak_working_set_bytes = process.PeakWorkingSet64 > 0 ? (long?)process.PeakWorkingSet64 : null,
    note = "align_ms は探索と補正画像の作成。total_ms は既存コアの比較も含む。PDF 描画・テキスト・レポートは含まない。マネージド割当量はネイティブ Mat を含まない。", results },
    new JsonSerializerOptions { WriteIndented = true }));

static byte[] CreatePage(bool changed, bool shifted)
{
    var runs = new List<TextRun>();
    for (var row = 0; row < 3; row++)
    for (var col = 0; col < 3; col++)
        runs.Add(new($"{row}{col}{(changed && row == 1 && col == 1 ? "128" : "123")}",
            20 + col * 75 + (shifted ? 3 : 0), 260 - row * 100 + (shifted ? 2 : 0)));
    return PdfFixture.CreateTextPage(runs);
}
