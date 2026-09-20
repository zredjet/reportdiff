using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;

// ソリューションの受け入れテストとは分離した、合成データだけの手動計測。
var iterations = args.Length >= 1 && int.TryParse(args[0], out var requested) ? requested : 3;
if (iterations is < 1 or > 20) throw new ArgumentException("計測回数は 1〜20 にしてください。");
var classificationMode = args.Length > 1 ? args[1] : null;
if (args.Length > 2 || classificationMode is not (null or "--classification=on" or "--classification=off"))
    throw new ArgumentException("追加引数は --classification=on または --classification=off にしてください。");
var classify = classificationMode != "--classification=off";
const int dpi = 300;
var width = Units.RoundPixels(210, dpi);
var height = Units.RoundPixels(297, dpi);
using var original = Render(false);
using var changed = Render(true);
using var transform = Mat.FromArray(new double[,] { { 1, 0, 1 }, { 0, 1, 1 } });
using var shifted = new Mat();
Cv2.WarpAffine(changed, shifted, transform, original.Size(), InterpolationFlags.Nearest, BorderTypes.Replicate);
var parameters = new ComparisonParameters();
var records = new List<object>();
foreach (var (name, image) in new[] { ("完全一致", original), ("1 か所変更", changed), ("1 か所変更＋全体 1px ずれ", shifted) })
{
    // 画像・期待結果は共通。最適化前後のマスクまで同一であることを確認する。
    using var baseline = PageComparer.Compare(original, image, parameters, classificationMode is not null, classify: false);
    using var optimized = PageComparer.Compare(original, image, parameters, true, classify: classify);
    if (baseline.Status != optimized.Status || baseline.RawPixels != optimized.RawPixels
        || baseline.AbsorbedGroups != optimized.AbsorbedGroups || baseline.MaxShiftPx != optimized.MaxShiftPx
        || !baseline.Clusters.SequenceEqual(optimized.Clusters.Select(c => c with { Kind = null }))
        || Cv2.Norm(baseline.RawMask, optimized.RawMask, NormTypes.INF) != 0
        || Cv2.Norm(baseline.LabelMask, optimized.LabelMask, NormTypes.INF) != 0)
        throw new InvalidOperationException("最適化前後の結果が一致しません。");
    if (name != "完全一致" && (optimized.Status != "different" || optimized.Clusters.Count != 1))
        throw new InvalidOperationException("合成データの変更 1 か所を検出できません。");
    foreach (var useBounds in classificationMode is null ? new[] { false, true } : new[] { true })
    {
        var samples = new List<(double TotalMs, ComparisonTimings Stages)>();
        for (var i = 0; i < iterations; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            var timings = new ComparisonTimings();
            var watch = Stopwatch.StartNew();
            using var result = PageComparer.Compare(original, image, parameters, useBounds, timings, classify);
            watch.Stop();
            samples.Add((watch.Elapsed.TotalMilliseconds, timings));
        }
        var ordered = samples.OrderBy(sample => sample.TotalMs).ToArray();
        var median = ordered[ordered.Length / 2];
        records.Add(new
        {
            scenario = name, strategy = useBounds ? "group_bounds" : "full_page",
            classification = classify, iterations, total_ms = samples.Select(sample => sample.TotalMs).ToArray(),
            median_ms = median.TotalMs, stages_at_median_ms = new
            {
                median.Stages.PreparationMs, median.Stages.CandidatesMs, median.Stages.GroupingMs,
                median.Stages.ShiftsMs, median.Stages.ClusteringMs, median.Stages.ClassificationMs
            },
            classification_managed_bytes = median.Stages.ClassificationManagedBytes,
            retained_removal_mask_bytes = median.Stages.RetainedRemovalMaskBytes,
            status = optimized.Status, clusters = optimized.Clusters.Count, raw_pixels = optimized.RawPixels,
            absorbed_groups = optimized.AbsorbedGroups, max_shift_px = optimized.MaxShiftPx
        });
        Console.Error.WriteLine($"{name} / {(useBounds ? "最適化後" : "最適化前")}: {median.TotalMs:F1} ms");
    }
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    generated_at = DateTimeOffset.Now,
    os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    dotnet_runtime = Environment.Version.ToString(),
    cpu_count = Environment.ProcessorCount,
    width, height, dpi, glyphs = 600, validation_runs_per_scenario = 2,
    memory_note = "ClassificationManagedBytes は分類のスレッド別マネージド割当量。RetainedRemovalMaskBytes は保持するネイティブ削除マスクのみで、一時 Mat やプロセスのピークを含まない。",
    results_equal = true, measurements = records
}, new JsonSerializerOptions { WriteIndented = true }));

Mat Render(bool change)
{
    var image = new Mat(height, width, MatType.CV_8UC3, Scalar.All(255));
    try
    {
        for (var row = 0; row < 30; row++)
        for (var column = 0; column < 20; column++)
            Cv2.PutText(image, change && row == 15 && column == 10 ? "8" : "0",
                new Point(Units.RoundPixels(12 + column * 9.5, dpi), Units.RoundPixels(14 + row * 9.2, dpi)),
                HersheyFonts.HersheySimplex, 1.3, Scalar.All(0), 2, LineTypes.AntiAlias);
        return image;
    }
    catch { image.Dispose(); throw; }
}
