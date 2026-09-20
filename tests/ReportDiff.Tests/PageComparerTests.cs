using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class PageComparerTests
{
    public static IEnumerable<object[]> Cases => TolerantDifferenceTests.Cases;

    [Theory]
    [MemberData(nameof(Cases))]
    public void AllGoldenPagesMatchReference(string id)
    {
        var expected = GoldenData.Cases().Single(c => c.GetProperty("id").GetString() == id);
        using var a = GoldenData.Image(id, "a");
        using var b = GoldenData.Image(id, "b");
        using var result = PageComparer.Compare(a, b, GoldenData.Parameters(expected));
        using var unclassified = PageComparer.Compare(a, b, GoldenData.Parameters(expected), true, classify: false);
        Assert.Equal(unclassified.Status, result.Status);
        Assert.Equal(unclassified.Clusters, result.Clusters.Select(c => c with { Kind = null, ShiftPx = null, RelatedClusterIds = [] }));
        Assert.Equal(unclassified.RawPixels, result.RawPixels);
        Assert.Equal(unclassified.NoiseDropped, result.NoiseDropped);
        Assert.Equal(unclassified.AbsorbedGroups, result.AbsorbedGroups);
        Assert.Equal(unclassified.MaxShiftPx, result.MaxShiftPx);
        Assert.Equal(unclassified.Warnings, result.Warnings);
        Assert.Equal(0, Cv2.Norm(unclassified.RawMask, result.RawMask, NormTypes.INF));
        Assert.Equal(0, Cv2.Norm(unclassified.LabelMask, result.LabelMask, NormTypes.INF));
        MovementTests.AssertDetectionUnchanged(a, b, GoldenData.Parameters(expected), result);
        Assert.Equal(expected.GetProperty("status").GetString(), result.Status);
        var pixels = expected.GetProperty("raw_pixels").GetInt32();
        Assert.InRange(result.RawPixels, pixels * 0.98, pixels * 1.02);
        Assert.Equal(expected.GetProperty("absorbed_groups").GetInt32(), result.AbsorbedGroups);
        Assert.Equal(expected.GetProperty("max_shift_px").GetInt32(), result.MaxShiftPx);
        Assert.Equal(expected.GetProperty("noise_dropped").GetInt32(), result.NoiseDropped);
        var clusters = expected.GetProperty("clusters").EnumerateArray().ToArray();
        Assert.Equal(clusters.Length, result.Clusters.Count);
        for (var i = 0; i < clusters.Length; i++)
        {
            var c = clusters[i]; var actual = result.Clusters[i];
            Assert.Equal(c.GetProperty("id").GetInt32(), actual.Id);
            Near(c.GetProperty("x").GetInt32(), actual.Bounds.X);
            Near(c.GetProperty("y").GetInt32(), actual.Bounds.Y);
            Near(c.GetProperty("w").GetInt32(), actual.Bounds.Width);
            Near(c.GetProperty("h").GetInt32(), actual.Bounds.Height);
            Assert.Equal(actual.Pixels / (double)(actual.Bounds.Width * actual.Bounds.Height), actual.FillRatio);
        }
    }

    private static void Near(int expected, int actual) => Assert.InRange(actual, expected - 1, expected + 1);

    [Fact]
    public void RatioThresholdIsExclusiveAndExclusionsRunFirst()
    {
        using var mask = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mask, new Rect(0, 0, 3, 10), Scalar.All(255), -1);
        using var raw = new RawDifference(mask.Clone(), 2, 1);
        var p = new ComparisonParameters { Cluster = new() { MaxDiffRatio = 0.3, MinPixels = 1, MergeXMm = 0, MergeYMm = 0 } };
        using var boundary = PageComparer.Cluster(raw, p);
        Assert.Equal("different", boundary.Status);
        raw.RawMask.Set(9, 9, (byte)255);
        using var exceeded = PageComparer.Cluster(raw, p);
        Assert.Equal("too_different", exceeded.Status); Assert.Empty(exceeded.Clusters);
        Assert.Contains("TOO_DIFFERENT", exceeded.Warnings);
        Assert.Equal(2, exceeded.AbsorbedGroups); Assert.Equal(1, exceeded.MaxShiftPx);
        using var excluded = PageComparer.Cluster(raw, p with { Exclude = [new(0, 0, 10, 10)] });
        Assert.Equal("same", excluded.Status); Assert.Equal(0, excluded.RawPixels);
    }

    [Fact]
    public void NoiseAndClusterLimitPreserveOnlyAcceptedContours()
    {
        using var mask = new Mat(40, 80, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mask, new Rect(2, 2, 1, 2), Scalar.All(255), -1);
        Cv2.Rectangle(mask, new Rect(15, 2, 2, 2), Scalar.All(255), -1);
        Cv2.Rectangle(mask, new Rect(30, 2, 3, 2), Scalar.All(255), -1);
        Cv2.Rectangle(mask, new Rect(45, 2, 4, 2), Scalar.All(255), -1);
        using var raw = new RawDifference(mask.Clone(), 0, 0);
        var p = new ComparisonParameters { Cluster = new() { MinPixels = 4, MaxClustersPerPage = 2, MergeXMm = 0, MergeYMm = 0 } };
        using var result = PageComparer.Cluster(raw, p);
        Assert.Equal(20, result.RawPixels); Assert.Equal(1, result.NoiseDropped);
        Assert.Equal(new[] { 6, 8 }, result.Clusters.Select(c => c.Pixels));
        Assert.Equal(new[] { 1, 2 }, result.Clusters.Select(c => c.Id));
        Assert.Contains("CLUSTER_LIMIT", result.Warnings);
        Assert.Equal(14, Cv2.CountNonZero(result.LabelMask));
        Assert.Equal((byte)0, result.LabelMask.At<byte>(2, 15));
        using var noiseOnly = PageComparer.Cluster(raw, p with { Cluster = p.Cluster with { MinPixels = 9 } });
        Assert.Equal("same", noiseOnly.Status); Assert.Equal(4, noiseOnly.NoiseDropped);
        Assert.Equal(20, noiseOnly.RawPixels); Assert.Equal(0, Cv2.CountNonZero(noiseOnly.LabelMask));
    }

    [Fact]
    public void ExclusionsRoundOutwardAndClipToPage()
    {
        using var mask = new Mat(5, 5, MatType.CV_8UC1, Scalar.All(255));
        using var raw = new RawDifference(mask.Clone(), 0, 0);
        static double Mm(double px) => Units.PixelsToMm(px, 300);
        var p = new ComparisonParameters { Exclude = [new(Mm(1.2), Mm(2.2), Mm(0.2), Mm(0.2)), new(Mm(4.2), 0, 500, 500)] };
        using var result = PageComparer.Cluster(raw, p);
        Assert.Equal(19, result.RawPixels);
        Assert.Equal((byte)0, result.RawMask.At<byte>(2, 1));
        Assert.Equal((byte)255, result.RawMask.At<byte>(1, 1));
        Assert.Equal(25, Cv2.CountNonZero(raw.RawMask));
    }
}
