using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;

[assembly: SupportedOSPlatform("windows")]
[assembly: SupportedOSPlatform("macOS")]

if (args.Length != 1) throw new ArgumentException("計測画像を一時保存する、存在しないディレクトリを指定してください。");
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root) || File.Exists(root)) throw new ArgumentException("保存先は存在しないパスにしてください。");
Directory.CreateDirectory(root);
var records = new List<Measurement>();
var settings = new AppSettings();
var inputs = new ReportInputs(new("A.png", "png", 1, new string('a', 64)), new("B.png", "png", 1, new string('b', 64)));
var timestamp = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
foreach (var (width, height) in new[] { (32, 32), (128, 128), (512, 512), (999, 1000), (1000, 1000), (1024, 1024) })
foreach (var pattern in new[] { "blank", "form", "noise" })
{
    using var image = new Mat(height, width, MatType.CV_8UC3, Scalar.All(255));
    if (pattern == "noise")
    {
        var random = new Random(921);
        var pixels = image.AsSpan<Vec3b>();
        for (var i = 0; i < pixels.Length; i++) pixels[i] = new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
    }
    else if (pattern == "form")
    {
        for (var y = 10; y < height; y += 31) Cv2.Line(image, new(0, y), new(width - 1, y), Scalar.All(0));
        for (var x = 10; x < width; x += 83) Cv2.Line(image, new(x, 0), new(x, height - 1), new(90, 60, 20));
    }
    using var pair = PageNormalizer.Normalize(image, image);
    using var comparison = new PageComparison("same", [], 0, 0, 0, 0,
        new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0)), new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0)), []);
    string[]? reference = null;
    for (var iteration = 0; iteration < 18; iteration++)
    foreach (var mode in iteration % 2 == 0 ? new[] { "serial", "parallel", "auto" } : new[] { "auto", "parallel", "serial" })
    {
        var execution = new PageImageExecution
        {
            MaxDegreeOfParallelism = mode == "serial" ? 1 : 2,
            MinimumParallelPixels = mode == "auto" ? 1_000_000 : 0
        };
        var output = Path.Combine(root, "run");
        var writer = new ReportWriter(output, inputs, settings.ToReportConfiguration(), true, timestamp, execution);
        var allocated = GC.GetTotalAllocatedBytes(false);
        var timer = Stopwatch.StartNew();
        writer.AddComparedPage(1, pair, comparison, 300);
        timer.Stop();
        var bytes = GC.GetTotalAllocatedBytes(false) - allocated;
        // JSON/HTML生成・ハッシュ検査・削除は画像保存の計測区間から外す。
        var report = writer.Complete(); HtmlReportWriter.Write(output, report);
        var manifest = Directory.GetFiles(output, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(output, p) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))))
            .Order(StringComparer.Ordinal).ToArray();
        reference ??= manifest;
        if (!reference.SequenceEqual(manifest)) throw new InvalidOperationException("逐次／並列の出力が一致しません。");
        records.Add(new(width, height, pattern, mode, PageImageWriteSchedule.Create(width, height, execution).Degree,
            iteration < 3, timer.Elapsed.TotalMilliseconds, bytes));
        Directory.Delete(output, true);
    }
    Console.Error.WriteLine($"{width}×{height} {pattern}: 全出力一致");
}
var summaries = records.Where(r => !r.Warmup).GroupBy(r => new { r.Width, r.Height, r.Pattern, r.Mode, r.Workers })
    .Select(g => new { g.Key, median_ms = Median(g.Select(r => r.Milliseconds)), min_ms = g.Min(r => r.Milliseconds),
        max_ms = g.Max(r => r.Milliseconds), median_managed_bytes = Median(g.Select(r => (double)r.ManagedBytes)), count = g.Count() });
Console.WriteLine(JsonSerializer.Serialize(new { processor_count = Environment.ProcessorCount, output_equal = true, summaries, records },
    new JsonSerializerOptions { WriteIndented = true }));

static double Median(IEnumerable<double> values)
{
    var sorted = values.Order().ToArray();
    return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
}

internal sealed record Measurement(int Width, int Height, string Pattern, string Mode, int Workers, bool Warmup,
    double Milliseconds, long ManagedBytes);
