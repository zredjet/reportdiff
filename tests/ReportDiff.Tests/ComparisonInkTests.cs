using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class ComparisonInkTests
{
    public static IEnumerable<object[]> Cases => TolerantDifferenceTests.Cases;

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReusedInkPreservesClassificationAndMasksForEveryGoldenCase(string id)
    {
        var parameters = GoldenData.Parameters(GoldenData.Cases().Single(c => c.GetProperty("id").GetString() == id));
        using var a = GoldenData.Image(id, "a");
        using var b = GoldenData.Image(id, "b");
        using var ink = new ComparisonInk();
        using var raw = TolerantDifference.Calculate(a, b, parameters, true, classificationInk: ink);
        using var expected = PageComparer.Cluster(raw, parameters, a, b);
        using var actual = PageComparer.Cluster(raw, parameters, a, b, classificationInk: ink);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.RawPixels, actual.RawPixels);
        Assert.Equal(expected.NoiseDropped, actual.NoiseDropped);
        Assert.Equal(expected.AbsorbedGroups, actual.AbsorbedGroups);
        Assert.Equal(expected.MaxShiftPx, actual.MaxShiftPx);
        Assert.Equal(expected.Warnings, actual.Warnings);
        Assert.Equal(expected.Clusters.Select(c => c with { RelatedClusterIds = [] }),
            actual.Clusters.Select(c => c with { RelatedClusterIds = [] }));
        for (var i = 0; i < expected.Clusters.Count; i++)
            Assert.Equal(expected.Clusters[i].RelatedClusterIds, actual.Clusters[i].RelatedClusterIds);
        AssertMask(expected.RawMask, actual.RawMask);
        AssertMask(expected.LabelMask, actual.LabelMask);
        AssertMask(expected.RemovalMask, actual.RemovalMask);
    }

    [Theory]
    [InlineData(300, 0, 0.001)]
    [InlineData(300, 0.3, 0.001)]
    [InlineData(300, 0.3, 1.5)]
    [InlineData(400, 0, 1.5)]
    [InlineData(400, 0.3, 1.5)]
    [InlineData(300, 0.6, 12.7)]
    [InlineData(400, 0.3, 20)]
    public void ReusedInkMatchesRecalculationAtImageStripeAndRegionEdges(int dpi, double edge, double radius)
    {
        using var parentA = new Mat(393, 128, MatType.CV_8UC3, Scalar.All(230));
        using var parentB = parentA.Clone();
        using var a = new Mat(parentA, new Rect(4, 4, 120, 385));
        using var b = new Mat(parentB, new Rect(4, 4, 120, 385));
        a.SetTo(Scalar.All(80)); b.SetTo(Scalar.All(80));
        foreach (var y in new[] { 0, 123, 252, 373 })
        {
            Cv2.Rectangle(a, new Rect(0, y, 120, 12), Scalar.All(230), 2);
            Cv2.Rectangle(b, new Rect(0, y, 120, 12), Scalar.All(230), 2);
            Cv2.Rectangle(a, new Rect(4, y + 3, 8, 6), Scalar.All(0), -1);
            Cv2.Rectangle(b, new Rect(104, y + 3, 8, 6), Scalar.All(0), -1);
            Cv2.Rectangle(a, new Rect(50, y + 3, 8, 6), new Scalar(180, 0, 0), -1);
            Cv2.Rectangle(b, new Rect(50, y + 3, 8, 6), new Scalar(0, 0, 180), -1);
        }
        var parameters = new ComparisonParameters { Dpi = dpi, Diff = new() { EdgeTolerance = edge },
            Ink = new() { BackgroundRadiusMm = radius } };
        using var featuresA = ComparisonFeatures.Create(a, parameters, true);
        using var featuresB = ComparisonFeatures.Create(b, parameters, true);
        using var ink = new ComparisonInk();
        ink.Capture(a, b, parameters, featuresA.Ink, featuresB.Ink);
        var beforeA = MatBuffers.Bytes(parentA); var beforeB = MatBuffers.Bytes(parentB);
        var raw = Enumerable.Repeat((byte)255, 120 * 385).ToArray();
        var labels = Enumerable.Repeat(1, raw.Length).ToArray();
        foreach (var region in new[] { new Rect(0, 0, 120, 385), new Rect(0, 0, 10, 10),
            new Rect(8, 126, 103, 4), new Rect(8, 255, 103, 4), new Rect(108, 375, 12, 10) })
        {
            var expected = DifferenceClassifier.Classify(a, b, parameters, raw, labels, [false, true], region, out var expectedMask);
            using var expectedOwned = expectedMask;
            var actual = DifferenceClassifier.Classify(a, b, parameters, raw, labels, [false, true], region, out var actualMask, ink);
            using var actualOwned = actualMask;
            Assert.Equal(expected, actual);
            AssertMask(expectedMask, actualMask);
        }
        Assert.Equal(beforeA, MatBuffers.Bytes(parentA));
        Assert.Equal(beforeB, MatBuffers.Bytes(parentB));
    }

    [Fact]
    public void InkSurvivesFeaturesButNotItsComparisonOwner()
    {
        using var a = GoldenData.Image("D01", "a"); using var b = GoldenData.Image("D01", "b");
        var parameters = new ComparisonParameters();
        using var fa = ComparisonFeatures.Create(a, parameters, true);
        using var fb = ComparisonFeatures.Create(b, parameters, true);
        using var ink = new ComparisonInk();
        ink.Capture(a, b, parameters, fa.Ink, fb.Ink);
        var (retainedA, retainedB) = ink.ReadFor(a, b, parameters);
        Assert.NotNull(retainedA); Assert.NotNull(retainedB);
        Assert.Equal(fa.Ink.Data, retainedA.Data); Assert.Equal(fb.Ink.Data, retainedB.Data);
        var expectedA = MatBuffers.Bytes(fa.Ink); var expectedB = MatBuffers.Bytes(fb.Ink);
        fa.Dispose(); fb.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { fa.Read(); });
        Assert.Equal(expectedA, MatBuffers.Bytes(retainedA)); Assert.Equal(expectedB, MatBuffers.Bytes(retainedB));
        ink.Dispose(); ink.Dispose();
        Assert.True(retainedA.IsDisposed); Assert.True(retainedB.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => ink.ReadFor(a, b, parameters));
        Assert.False(a.IsDisposed); Assert.False(b.IsDisposed);
    }

    [Fact]
    public void OnlyTheSameInputsDpiAndInkOptionsCanReuseMasks()
    {
        using var a = GoldenData.Image("D01", "a"); using var b = GoldenData.Image("D01", "b");
        using var other = a.Clone();
        var parameters = new ComparisonParameters();
        using var fa = ComparisonFeatures.Create(a, parameters, true);
        using var fb = ComparisonFeatures.Create(b, parameters, true);
        using var ink = new ComparisonInk();
        ink.Capture(a, b, parameters, fa.Ink, fb.Ink);
        Assert.Equal((null, null), ink.ReadFor(other, b, parameters));
        Assert.Equal((null, null), ink.ReadFor(a, other, parameters));
        Assert.Equal((null, null), ink.ReadFor(b, a, parameters));
        foreach (var changed in new[] { parameters with { Dpi = 400 },
            parameters with { Ink = parameters.Ink with { BackgroundRadiusMm = 2 } },
            parameters with { Ink = parameters.Ink with { ContrastThreshold = 100 } } })
            Assert.Equal((null, null), ink.ReadFor(a, b, changed));
        // エッジ膨張や除外は分類時に適用し、元のインク判定には影響しない。
        Assert.NotNull(ink.ReadFor(a, b, parameters with { Diff = new() { EdgeTolerance = 0 },
            Ink = new(), Exclude = [new(0, 0, 1, 1)] }).A);
        Assert.Throws<InvalidOperationException>(() => ink.Capture(a, b, parameters, fa.Ink, fb.Ink));
    }

    [Fact]
    public void FailedCaptureDoesNotPublishAPartialPair()
    {
        using var a = new Mat(5, 5, MatType.CV_8UC3, Scalar.All(255));
        using var b = a.Clone();
        using var mask = new Mat(5, 5, MatType.CV_8UC1, Scalar.All(0));
        using var disposedMask = mask.Clone(); disposedMask.Dispose();
        var parameters = new ComparisonParameters();
        using var ink = new ComparisonInk();
        Assert.Throws<ObjectDisposedException>(() => ink.Capture(a, b, parameters, mask, disposedMask));
        Assert.Equal((null, null), ink.ReadFor(a, b, parameters));
        ink.Capture(a, b, parameters, mask, mask);
        Assert.NotNull(ink.ReadFor(a, b, parameters).A);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("no_candidates")]
    [InlineData("no_shift")]
    public void EarlyPathsKeepNoClassificationInk(string scenario)
    {
        using var a = new Mat(20, 20, MatType.CV_8UC3, Scalar.All(255));
        using var b = a.Clone();
        if (scenario == "no_candidates") b.SetTo(Scalar.All(254));
        if (scenario == "no_shift") b.SetTo(Scalar.All(0));
        var parameters = new ComparisonParameters { Diff = new() { MaxShiftMm = scenario == "no_shift" ? 0 : 0.15 } };
        using var ink = new ComparisonInk();
        using var raw = TolerantDifference.Calculate(a, b, parameters, true, classificationInk: ink);
        Assert.Equal((null, null), ink.ReadFor(a, b, parameters));
    }

    private static void AssertMask(Mat? expected, Mat? actual)
    {
        Assert.Equal(expected is null, actual is null);
        if (expected is not null) Assert.Equal(0, Cv2.Norm(expected, actual!, NormTypes.INF));
    }
}
