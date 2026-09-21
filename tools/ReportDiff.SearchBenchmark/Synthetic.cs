using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;

[assembly: SupportedOSPlatform("windows")]
[assembly: SupportedOSPlatform("macOS")]

if (args.Length != 1 || File.Exists(args[0])) throw new ArgumentException("未使用のJSON出力先を指定してください。");
var records = new List<object>();
foreach (var edge in new[] { 0.0, 0.3 })
foreach (var scenario in new[] { "shift-only", "different", "shift-plus-change", "local-change", "edge-roi", "small" })
{
    var width = scenario == "small" ? 256 : 1100;
    var height = scenario == "small" ? 256 : 1000;
    using var parent = new Mat(height + 6, width + 8, MatType.CV_8UC3);
    var bytes = new byte[parent.Rows * parent.Cols * 3];
    new Random(2718).NextBytes(bytes); Marshal.Copy(bytes, 0, parent.Data, bytes.Length);
    using var a = new Mat(parent, new Rect(3, 2, width, height));
    if (scenario != "edge-roi") Cv2.Rectangle(a, new Rect(0, 0, width, height), Scalar.All(255), 16);
    using var parentB = parent.Clone();
    using var b = new Mat(parentB, new Rect(3, 2, width, height));
    using var transform = Mat.FromArray(new double[,] { { 1, 0, 1 }, { 0, 1, 0 } });
    using var shifted = new Mat();
    Cv2.WarpAffine(a, shifted, transform, a.Size(), InterpolationFlags.Nearest, BorderTypes.Replicate);
    shifted.CopyTo(b);
    if (scenario == "local-change") a.CopyTo(b);
    if (scenario == "different") b.SetTo(new Scalar(100, 180, 230));
    if (scenario is "shift-plus-change" or "local-change" or "edge-roi" or "small")
        Cv2.Rectangle(b, new Rect(width / 2, height / 2, 30, 30), new Scalar(100, 180, 230), -1);
    var beforeA = MatBuffers.Bytes(parent); var beforeB = MatBuffers.Bytes(parentB);
    var parameters = new ComparisonParameters { Diff = new() { EdgeTolerance = edge } };
    using var expected = TolerantDifference.Calculate(a, b, parameters, false);
    var times = new List<object>();
    for (var i = 0; i < 8; i++)
    {
        var timings = new ComparisonTimings(); var timer = Stopwatch.StartNew();
        using var result = TolerantDifference.Calculate(a, b, parameters, true, timings);
        timer.Stop();
        if (result.AbsorbedGroups != expected.AbsorbedGroups || result.MaxShiftPx != expected.MaxShiftPx
            || Cv2.Norm(result.RawMask, expected.RawMask, NormTypes.INF) != 0)
            throw new InvalidOperationException("独立した全ページ探索と結果が一致しません。");
        times.Add(new { warmup = i == 0, ms = timer.Elapsed.TotalMilliseconds, timings });
    }
    if (!beforeA.SequenceEqual(MatBuffers.Bytes(parent)) || !beforeB.SequenceEqual(MatBuffers.Bytes(parentB)))
        throw new InvalidOperationException("親画像が変更されています。");
    records.Add(new { scenario, edge, width, height, expected.AbsorbedGroups, expected.MaxShiftPx,
        raw_sha256 = Convert.ToHexString(SHA256.HashData(MatBuffers.Bytes(expected.RawMask))),
        exact = true, input_unchanged = true, times });
}
File.WriteAllText(args[0], JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
