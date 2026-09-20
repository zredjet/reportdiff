using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class OptimizationTests
{
    public static IEnumerable<object[]> Cases => TolerantDifferenceTests.Cases;

    [Theory]
    [MemberData(nameof(Cases))]
    public void OptimizationPreservesEveryGoldenResult(string id)
    {
        var test = GoldenData.Cases().Single(c => c.GetProperty("id").GetString() == id);
        using var a = GoldenData.Image(id, "a");
        using var b = GoldenData.Image(id, "b");
        CompareBoth(a, b, GoldenData.Parameters(test));
    }

    [Theory]
    [InlineData(17, 0.3, 0.15)]
    [InlineData(18, 0, 0.15)]
    [InlineData(19, 0.3, 0.30)]
    public void ImageEdgesAndNonContinuousViewsPreserveResults(int seed, double edge, double shift)
    {
        using var original = new Mat(60, 80, MatType.CV_8UC3, Scalar.All(255));
        var random = new Random(seed);
        for (var i = 0; i < 20; i++)
            Cv2.Rectangle(original, new Rect(random.Next(75), random.Next(55), 5, 5),
                new Scalar(random.Next(256), random.Next(256), random.Next(256)), -1);
        using var transform = Mat.FromArray(new double[,] { { 1, 0, 1 }, { 0, 1, -1 } });
        using var modified = new Mat();
        Cv2.WarpAffine(original, modified, transform, original.Size(), InterpolationFlags.Nearest, BorderTypes.Replicate);
        Cv2.Line(modified, new Point(0, 30), new Point(79, 30), Scalar.All(0), 2);
        using var a = new Mat(original, new Rect(1, 1, 78, 58));
        using var b = new Mat(modified, new Rect(1, 1, 78, 58));
        Assert.False(a.IsContinuous());
        CompareBoth(a, b, new() { Diff = new() { EdgeTolerance = edge, MaxShiftMm = shift } });
    }

    private static void CompareBoth(Mat a, Mat b, ComparisonParameters parameters)
    {
        using var baseline = PageComparer.Compare(a, b, parameters, false);
        using var optimized = PageComparer.Compare(a, b, parameters, true);
        Assert.Equal(baseline.Status, optimized.Status);
        Assert.Equal(baseline.RawPixels, optimized.RawPixels);
        Assert.Equal(baseline.AbsorbedGroups, optimized.AbsorbedGroups);
        Assert.Equal(baseline.MaxShiftPx, optimized.MaxShiftPx);
        Assert.Equal(baseline.NoiseDropped, optimized.NoiseDropped);
        Assert.Equal(baseline.Clusters.Select(c => c with { RelatedClusterIds = [] }),
            optimized.Clusters.Select(c => c with { RelatedClusterIds = [] }));
        for (var i = 0; i < baseline.Clusters.Count; i++)
            Assert.Equal(baseline.Clusters[i].RelatedClusterIds, optimized.Clusters[i].RelatedClusterIds);
        Assert.Equal(baseline.RemovalMask is null, optimized.RemovalMask is null);
        if (baseline.RemovalMask is not null)
            Assert.Equal(0, Cv2.Norm(baseline.RemovalMask, optimized.RemovalMask!, NormTypes.INF));
        Assert.Equal(baseline.Warnings, optimized.Warnings);
        Assert.Equal(0, Cv2.Norm(baseline.RawMask, optimized.RawMask, NormTypes.INF));
        Assert.Equal(0, Cv2.Norm(baseline.LabelMask, optimized.LabelMask, NormTypes.INF));
    }
}
