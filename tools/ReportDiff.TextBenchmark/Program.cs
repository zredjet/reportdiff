using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Tests;

[assembly: SupportedOSPlatform("windows")]
[assembly: SupportedOSPlatform("macOS")]

if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
if (args is ["--fixture", var directory])
{
    Directory.CreateDirectory(directory);
    foreach (var revised in new[] { false, true })
    {
        TextRun[] runs = [new(revised ? "128" : "123", 50, 220), new(revised ? "日本新" : "日本旧", 50, 190),
            new("<script>悪&" + (revised ? "B" : "A") + new string('A', 2100), 50, 160, 8)];
        File.WriteAllBytes(Path.Combine(directory, revised ? "新 文字.pdf" : "旧 文字.pdf"), PdfFixture.CreateTextPage(runs));
    }
    return;
}
var iterations = args.Length == 1 && int.TryParse(args[0], out var n) && n is >= 1 and <= 20 ? n : 5;
var temporary = Path.Combine(Path.GetTempPath(), "reportdiff-text-benchmark-" + Guid.NewGuid());
Directory.CreateDirectory(temporary);
try
{
    var path = Path.Combine(temporary, "合成 帳票.pdf");
    var runs = Enumerable.Range(0, 600).Select(i => new TextRun(i.ToString("D6"), 20 + i % 20 * 27, 800 - i / 20 * 25, 6)).ToArray();
    File.WriteAllBytes(path, PdfFixture.CreateTextPage(runs, media: "0 0 595.276 841.89"));
    using var pdf = PdfReader.Open(path); using var image = pdf.ReadPage(1);
    var size = image.Pixels.Size();
    var records = new List<object>();
    foreach (var count in new[] { 1, 120, 500 })
    {
        var clusters = runs.Take(count).Select((r, i) => new DifferenceCluster(i + 1,
            new Rect((int)Math.Floor(Units.PointsToPixels(r.X, 300)), (int)Math.Floor(Units.PointsToPixels(841.89 - r.Y - 5, 300)),
                (int)Math.Ceiling(Units.PointsToPixels(22, 300)), (int)Math.Ceiling(Units.PointsToPixels(6, 300))), 1)).ToArray();
        using var reused = new PdfTextReader(path);
        var validation = reused.Annotate(1, size, 300, clusters, []);
        if (validation.Warnings.Count != 0 || validation.TextByCluster.Count != count
            || validation.TextByCluster.Any(pair => pair.Value != (pair.Key - 1).ToString("D6")))
            throw new InvalidOperationException("テキスト注釈が期待値と一致しません。");
        foreach (var mode in new[] { "open_and_annotate", "reuse_document" })
        {
            var samples = new List<(double Ms, long Bytes)>();
            for (var i = 0; i < iterations; i++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var timer = Stopwatch.StartNew();
                if (mode == "open_and_annotate")
                {
                    using var reader = new PdfTextReader(path);
                    reader.Annotate(1, size, 300, clusters, []);
                }
                else reused.Annotate(1, size, 300, clusters, []);
                timer.Stop(); samples.Add((timer.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated));
            }
            var median = samples.OrderBy(s => s.Ms).ElementAt(samples.Count / 2);
            records.Add(new { clusters = count, mode, iterations, median_ms = median.Ms, managed_bytes_at_median = median.Bytes,
                samples_ms = samples.Select(s => s.Ms).ToArray() });
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { generated_at = DateTimeOffset.Now, os = RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(), runtime = Environment.Version.ToString(), dpi = 300,
        width = size.Width, height = size.Height, words = 600, results_equal = true, measurements = records }, new JsonSerializerOptions { WriteIndented = true }));
}
finally { Directory.Delete(temporary, true); }
