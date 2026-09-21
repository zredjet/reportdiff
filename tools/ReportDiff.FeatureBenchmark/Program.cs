using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;

if (args.Length < 2) throw new ArgumentException("verify ゴールデンのディレクトリ / measure 入力PNG [dpi edge radius ink] を指定してください。");
var checks = 0;
if (args[0] == "verify")
{
    using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[1], "expected.json")));
    foreach (var item in golden.RootElement.GetProperty("cases").EnumerateArray())
    foreach (var side in new[] { "a", "b" })
    {
        var id = item.GetProperty("id").GetString()!;
        var p = item.GetProperty("params");
        using var image = Load(Path.Combine(args[1], $"{id}_{side}.png"));
        Verify(image, new() { Dpi = p.GetProperty("dpi").GetInt32(),
            Diff = new() { EdgeTolerance = p.GetProperty("edge_tolerance").GetDouble() },
            Ink = new() { BackgroundRadiusMm = p.GetProperty("ink_background_radius_mm").GetDouble(),
                ContrastThreshold = p.GetProperty("ink_contrast_threshold").GetDouble() } }, true, [new()]);
    }
    foreach (var height in new[] { 1, 63, 64, 65, 127, 128, 129, 255, 256, 257, 513, 17 * FeatureStripeSchedule.DefaultStripeRows + 1 })
    foreach (var width in new[] { 1, 37 })
        RandomCase(width, height, 300, 0.3, 1.5, true);
    foreach (var dpi in new[] { 300, 400 })
    foreach (var edge in new[] { 0.0, 0.3 })
    foreach (var radius in new[] { 0.001, 1.5, 20.0 })
    foreach (var ink in new[] { false, true })
        RandomCase(73, 17 * FeatureStripeSchedule.DefaultStripeRows + 1, dpi, edge, radius, ink);
    Console.WriteLine(JsonSerializer.Serialize(new { checks, exact = true, default_stripe_rows = FeatureStripeSchedule.DefaultStripeRows }));
}
else if (args[0] == "measure")
{
    using var image = Load(args[1]);
    var p = new ComparisonParameters { Dpi = args.Length > 2 ? int.Parse(args[2]) : 300,
        Diff = new() { EdgeTolerance = args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0.3 },
        Ink = new() { BackgroundRadiusMm = args.Length > 4 ? double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 1.5 } };
    var ink = args.Length <= 5 || bool.Parse(args[5]);
    var records = new List<object>(); string? expectedHash = null;
    for (var repeat = 0; repeat < 10; repeat++)
    {
        var start = Stopwatch.GetTimestamp();
        using var features = ComparisonFeatures.Create(image, p, ink);
        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var data = features.Read();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemoryMarshal.AsBytes(data.Values)); hash.AppendData(MemoryMarshal.AsBytes(data.Contrast));
        hash.AppendData(features.Ink.AsSpan<byte>());
        var digest = Convert.ToHexString(hash.GetHashAndReset());
        expectedHash ??= digest;
        if (digest != expectedHash) throw new InvalidOperationException("反復中に特徴量が変化しました。");
        records.Add(new { repeat, warmup = repeat < 3, milliseconds, features.WorkerCount, features.StripeRows, features.WorkerTemporaryBytes, sha256 = digest });
    }
    Console.WriteLine(JsonSerializer.Serialize(new { default_stripe_rows = FeatureStripeSchedule.DefaultStripeRows, width = image.Width,
        height = image.Height, dpi = p.Dpi, edge = p.Diff.EdgeTolerance, radius = p.Ink.BackgroundRadiusMm, ink, records }));
}
else throw new ArgumentException("verify または measure を指定してください。");

void RandomCase(int width, int height, int dpi, double edge, double radius, bool ink)
{
    using var parent = new Mat(height + 8, width + 8, MatType.CV_8UC3);
    var pixels = parent.AsSpan<Vec3b>(); var random = new Random(921);
    for (var i = 0; i < pixels.Length; i++) pixels[i] = new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
    using var before = parent.Clone();
    using var image = new Mat(parent, new Rect(3, 3, width, height));
    var execution = new[] { 1, 2, 4, 8 }.Select(n => new FeatureExecution { MaxDegreeOfParallelism = n,
        MinimumParallelPixels = 0, TemporaryMemoryBudget = long.MaxValue }).Append(new() { TemporaryMemoryBudget = 0 }).ToArray();
    Verify(image, new() { Dpi = dpi, Diff = new() { EdgeTolerance = edge }, Ink = new() { BackgroundRadiusMm = radius } }, ink, execution);
    if (Cv2.Norm(parent, before, NormTypes.INF) != 0) throw new InvalidOperationException("入力が変更されました。");
}

void Verify(Mat image, ComparisonParameters p, bool ink, FeatureExecution[] executions)
{
    using var lab = ImageInk.ToLab(image);
    using var blur = new Mat(); using var maximum = new Mat(); using var minimum = new Mat();
    using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
    if (p.Diff.EdgeTolerance != 0)
    {
        Cv2.Blur(lab, blur, new Size(3, 3)); Cv2.Dilate(lab, maximum, kernel); Cv2.Erode(lab, minimum, kernel);
        Cv2.Subtract(maximum, minimum, maximum);
    }
    using var expectedInk = ink ? ImageInk.FromLab(lab, p.Dpi, p.Ink) : new Mat();
    foreach (var execution in executions)
    {
        using var actual = ComparisonFeatures.Create(image, p, ink, execution);
        var data = actual.Read();
        if (!MemoryMarshal.AsBytes((p.Diff.EdgeTolerance == 0 ? lab : blur).AsSpan<Vec3f>()).SequenceEqual(MemoryMarshal.AsBytes(data.Values))
            || !MemoryMarshal.AsBytes(maximum.AsSpan<Vec3f>()).SequenceEqual(MemoryMarshal.AsBytes(data.Contrast))
            || !expectedInk.AsSpan<byte>().SequenceEqual(actual.Ink.AsSpan<byte>()))
            throw new InvalidOperationException($"特徴量不一致: {image.Width}x{image.Height}, dpi={p.Dpi}, edge={p.Diff.EdgeTolerance}, radius={p.Ink.BackgroundRadiusMm}, ink={ink}, workers={actual.WorkerCount}");
        checks++;
    }
}

static Mat Load(string path) => Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
