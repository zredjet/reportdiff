using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>抑制マスクを所有する。連結成分数は通常クラスタと独立した 8 近傍の数。</summary>
public sealed class RegionalComparison(IReadOnlyList<RegionResult> regions, IReadOnlyList<RegionRun> runs,
    Mat suppressedMask, int suppressedPixels, int suppressedComponents, int excludedPixels) : IDisposable
{
    public IReadOnlyList<RegionResult> Regions { get; } = regions;
    public IReadOnlyList<RegionRun> Runs { get; } = runs;
    public Mat SuppressedMask { get; } = suppressedMask;
    public int SuppressedPixels { get; } = suppressedPixels;
    public int SuppressedComponents { get; } = suppressedComponents;
    public int ExcludedPixels { get; } = excludedPixels;
    public void Dispose() => SuppressedMask.Dispose();
}

internal static class RegionalComparer
{
    public static PageComparison Compare(Mat a, Mat b, ComparisonParameters parameters)
    {
        parameters = parameters with { Exclude = parameters.Exclude.Concat(parameters.Regions.Where(r => r.Mode == "exclude").Select(r => r.Bounds)).Distinct().ToArray() };
        var map = new RegionMap(a.Width, a.Height, parameters);
        using var ink = new ComparisonInk();
        using var baseline = TolerantDifference.Calculate(a, b, parameters, true, null, ink);
        var original = MatBuffers.Bytes(baseline.RawMask);
        var combined = (byte[])original.Clone();
        var runs = new List<RegionRun> { new(0, parameters.Diff, baseline.AbsorbedGroups, baseline.MaxShiftPx) };
        var settings = map.Regions.Where((r, i) => r.Mode == "compare" && map.EffectivePixels[i] > 0)
            .Select(r => r.Diff).Distinct().Where(d => d != parameters.Diff);
        foreach (var diff in settings)
        {
            // 特徴量・探索データは設定ごとに解放する。設定数ぶん保持しない。
            using var variant = TolerantDifference.Calculate(a, b, parameters with { Diff = diff });
            var bytes = MatBuffers.Bytes(variant.RawMask);
            runs.Add(new(runs.Count, diff, variant.AbsorbedGroups, variant.MaxShiftPx));
            for (var pixel = 0; pixel < combined.Length; pixel++)
                if (!map.Excluded[pixel] && map.DiffAt(pixel) == diff) combined[pixel] = bytes[pixel];
        }
        var suppressed = new byte[combined.Length];
        var suppressedCounts = new int[map.Regions.Length]; var rawCounts = new int[map.Regions.Length];
        var excludedCounts = new int[map.Regions.Length]; var excludedCount = 0;
        for (var pixel = 0; pixel < combined.Length; pixel++)
        {
            var owner = map.Owners[pixel];
            if (map.Excluded[pixel])
            {
                if (original[pixel] != 0)
                {
                    excludedCount++;
                    if (owner >= 0 && map.Regions[owner].Mode == "exclude") excludedCounts[owner]++;
                }
                combined[pixel] = 0;
                continue;
            }
            if (owner < 0) continue;
            if (combined[pixel] != 0) rawCounts[owner]++;
            else if (original[pixel] != 0) { suppressed[pixel] = 255; suppressedCounts[owner]++; }
        }
        using var raw = new RawDifference(MatBuffers.Mask(combined, a.Width, a.Height), baseline.AbsorbedGroups, baseline.MaxShiftPx);
        var result = PageComparer.Cluster(raw, parameters, a, b, classificationInk: ink, regions: map);
        Mat? mask = null;
        try
        {
            mask = MatBuffers.Mask(suppressed, a.Width, a.Height);
            var components = CountComponents(mask);
            var regionResults = map.Regions.Select((r, i) =>
            {
                var box = map.Bounds[i];
                var count = 0;
                if (suppressedCounts[i] > 0)
                {
                    var local = new byte[checked(box.Width * box.Height)];
                    for (var y = 0; y < box.Height; y++)
                    for (var x = 0; x < box.Width; x++)
                    {
                        var pixel = (box.Y + y) * a.Width + box.X + x;
                        if (map.Owners[pixel] == i) local[y * box.Width + x] = suppressed[pixel];
                    }
                    using var localMask = MatBuffers.Mask(local, box.Width, box.Height);
                    count = CountComponents(localMask);
                }
                return new RegionResult(r.Index, r.Name, r.Mode, box, map.EffectivePixels[i], rawCounts[i],
                    suppressedCounts[i], count, excludedCounts[i], r.Mode == "compare" && map.EffectivePixels[i] > 0
                        ? runs.Single(run => run.Diff == r.Diff).Id : null,
                    box.Width == 0 || box.Height == 0 ? "outside_page" : map.EffectivePixels[i] == 0 ? "shadowed" : "applied");
            }).ToArray();
            result.Regional = new(regionResults, runs, mask, suppressedCounts.Sum(), components, excludedCount);
            mask = null;
            return result;
        }
        catch { result.Dispose(); throw; }
        finally { mask?.Dispose(); }
    }

    private static int CountComponents(Mat mask)
    {
        if (Cv2.CountNonZero(mask) == 0) return 0;
        using var labels = new Mat();
        return Cv2.ConnectedComponents(mask, labels, PixelConnectivity.Connectivity8) - 1;
    }
}
