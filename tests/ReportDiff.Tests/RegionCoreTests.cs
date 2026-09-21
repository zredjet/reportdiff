using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class RegionCoreTests
{
    public static IEnumerable<object[]> UniformCases() => from id in new[] { "I01", "I10", "I11", "D01", "D07", "D10", "D20", "D21" }
        from profile in new[] { "normal", "strict", "loose" } select new object[] { id, profile };

    [Theory]
    [MemberData(nameof(UniformCases))]
    public void WholePageRegionMatchesPageProfileIncludingClassificationRemovalAndMovement(string id, string profile)
    {
        using var a = GoldenData.Image(id, "a"); using var b = GoldenData.Image(id, "b");
        var baseline = GoldenData.Parameters(GoldenData.Cases().Single(c => c.GetProperty("id").GetString() == id));
        var target = baseline with { Diff = ConfigurationLoader.ApplyProfile(baseline.Diff, profile) };
        var regional = baseline with { Regions = [Region(0, "全面", new(0, 0, a.Width, a.Height), target.Diff, baseline.Dpi)] };
        using var expected = PageComparer.Compare(a, b, target);
        using var actual = PageComparer.Compare(a, b, regional);
        SameResult(expected, actual);
        using var expectedReverse = PageComparer.Compare(b, a, target);
        using var actualReverse = PageComparer.Compare(b, a, regional);
        SameResult(expectedReverse, actualReverse);
        Assert.InRange(actual.Regional!.Runs.Count, 1, 2);
    }

    [Fact]
    public void StrictRegionDetectsOnePixelShiftOnlyInsideAndCrossingDifferenceStaysOneCluster()
    {
        using var a = White(); using var b = White();
        Draw(a, 50, 80); Draw(b, 51, 80); Draw(a, 200, 80); Draw(b, 201, 80);
        using var ordinary = PageComparer.Compare(a, b, new());
        Assert.Equal("same", ordinary.Status);
        var p = new ComparisonParameters { Regions = [Region(0, "厳密", new(0, 0, 120, 240), Strict)] };
        using var result = PageComparer.Compare(a, b, p);
        Assert.Single(result.Clusters); Assert.True(result.Clusters[0].Bounds.Right < 120);
        a.SetTo(Scalar.All(255)); b.SetTo(Scalar.All(255));
        Cv2.Rectangle(b, new Rect(110, 90, 20, 12), Scalar.All(0), -1);
        using var crossing = PageComparer.Compare(a, b, p);
        Assert.Equal("added", Assert.Single(crossing.Clusters).Kind);
        Assert.True(crossing.Clusters[0].Bounds.X < 120 && crossing.Clusters[0].Bounds.Right > 120);
        using var reverse = PageComparer.Compare(b, a, p);
        Assert.Equal("removed", Assert.Single(reverse.Clusters).Kind);
        Assert.Equal(reverse.RawPixels, Cv2.CountNonZero(reverse.RemovalMask!));
    }

    [Fact]
    public void SuppressionIsRawEightConnectivityAndExclusionsAreSeparateWithoutDoubleCounting()
    {
        using var a = White(); using var b = White();
        b.Set(50, 50, new Vec3b(0, 0, 0)); b.Set(51, 51, new Vec3b(0, 0, 0));
        b.Set(60, 60, new Vec3b(0, 0, 0)); b.Set(80, 80, new Vec3b(0, 0, 0));
        var noDifference = Strict with { ColorThreshold = 1000 };
        var p = new ComparisonParameters { Diff = Strict, Exclude = [Box(new(79, 79, 3, 3))], Regions =
        [Region(0, "抑制", new(40, 40, 60, 60), noDifference), Region(1, "除外", new(79, 79, 3, 3), Strict) with { Mode = "exclude" }] };
        using var result = PageComparer.Compare(a, b, p);
        Assert.Equal("same", result.Status); Assert.Empty(result.Clusters);
        var regional = result.Regional!;
        Assert.Equal(3, regional.SuppressedPixels); Assert.Equal(2, regional.SuppressedComponents);
        Assert.Equal(1, regional.ExcludedPixels); Assert.Equal(1, regional.Regions[1].ExcludedPixels);
        Assert.Equal(3, regional.Regions[0].SuppressedPixels); Assert.Equal(2, regional.Regions[0].SuppressedComponents);
        Assert.Equal((byte)0, regional.SuppressedMask.At<byte>(80, 80));
        Assert.Equal(2, regional.Runs.Count); Assert.Equal(0, result.AbsorbedGroups);
    }

    [Theory]
    [InlineData(72)] [InlineData(254)] [InlineData(300)] [InlineData(600)]
    public void NestedOwnershipExclusionPriorityClippingAndDeduplicatedRuns(int dpi)
    {
        using var a = White(); using var b = White();
        var p = new ComparisonParameters { Dpi = dpi, Regions =
        [Region(0, "外除外", new(0, 0, 200, 200), Strict, dpi) with { Mode = "exclude" },
         Region(1, "内比較", new(50, 50, 20, 20), new(), dpi),
         Region(2, "ページ外", new(500, 500, 20, 20), Strict, dpi),
         Region(3, "一部ページ外", new(250, 0, 100, 100), Strict, dpi),
         Region(4, "同設定", new(250, 150, 20, 20), Strict, dpi)] };
        using var result = PageComparer.Compare(a, b, p);
        var regions = result.Regional!.Regions;
        Assert.Equal("shadowed", regions[1].Status); Assert.Equal(0, regions[1].EffectivePixels);
        Assert.Equal("outside_page", regions[2].Status);
        Assert.Equal(320, regions[3].Bounds.Right);
        Assert.Equal(2, result.Regional.Runs.Count);
        Assert.Equal(regions[3].RunId, regions[4].RunId);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void MovementAcrossBoundaryRequiresBothCoordinateSettings(bool colorChange, bool strictDestination)
    {
        using var a = White(); using var b = White();
        Draw(a, 180, 100); Draw(b, 230, 100, colorChange ? 32 : 0);
        var tolerant = Strict with { ColorThreshold = 20 };
        var p = new ComparisonParameters { Diff = strictDestination ? tolerant : Strict,
            Regions = [Region(0, "移動先", new(220, 0, 100, 240), strictDestination ? Strict : tolerant)] };
        using var reference = PageComparer.Compare(a, b, p with { Regions = [], Diff = tolerant });
        Assert.All(reference.Clusters, c => Assert.Equal("moved", c.Kind));
        using var forward = PageComparer.Compare(a, b, p);
        using var reverse = PageComparer.Compare(b, a, p);
        Assert.Single(forward.Clusters); Assert.Single(reverse.Clusters);
        if (colorChange)
        {
            Assert.Equal("changed", forward.Clusters[0].Kind); Assert.Null(forward.Clusters[0].ShiftPx);
            Assert.Equal("changed", reverse.Clusters[0].Kind); Assert.Null(reverse.Clusters[0].ShiftPx);
        }
        else
        {
            Assert.Equal("moved", forward.Clusters[0].Kind); Assert.Equal(new MovementShift(50, 0), forward.Clusters[0].ShiftPx);
            Assert.Equal("moved", reverse.Clusters[0].Kind); Assert.Equal(new MovementShift(-50, 0), reverse.Clusters[0].ShiftPx);
        }
        Assert.Equal(336, forward.RawPixels); Assert.Equal(336, reverse.RawPixels);
    }

    [Fact]
    public void UniformMovementKeepsRelatedIdsAndPartialOrCompetingEvidenceStaysUnannotated()
    {
        foreach (var scenario in new[] { "separate", "partial", "competing" })
        {
            using var a = White(); using var b = White();
            Draw(a, 120, 100); Draw(b, 170, 100);
            if (scenario == "partial") { Draw(a, 140, 100); Draw(b, 140, 100); }
            if (scenario == "competing") Draw(a, 220, 100);
            var p = new ComparisonParameters { Diff = Strict, Cluster = new() { MergeXMm = 1, MergeYMm = 1 } };
            using var expected = PageComparer.Compare(a, b, p);
            using var result = PageComparer.Compare(a, b, p with { Regions = [Region(0, "全面", new(0, 0, 320, 240), Strict)] });
            SameResult(expected, result);
            if (scenario == "separate")
            {
                Assert.Equal(2, result.Clusters.Count);
                Assert.Equal(new[] { 2 }, result.Clusters[0].RelatedClusterIds);
            }
            else Assert.All(result.Clusters, c => Assert.NotEqual("moved", c.Kind));
        }
    }

    [Fact]
    public void ClassificationChoosesOriginalOrExpandedInkPerPixelAcrossOneCluster()
    {
        using var a = White(); using var b = White();
        Cv2.Rectangle(a, new Rect(90, 100, 20, 1), Scalar.All(0), -1);
        Cv2.Rectangle(b, new Rect(90, 101, 20, 1), Scalar.All(0), -1);
        var p = new ComparisonParameters { Move = new() { SearchMm = 0 },
            Regions = [Region(0, "左は厳密", new(0, 0, 100, 240), Strict)] };
        var map = new RegionMap(a.Width, a.Height, p);
        var raw = new byte[a.Width * a.Height]; var labels = new int[raw.Length];
        for (var y = 100; y <= 101; y++)
        for (var x = 90; x < 110; x++) { raw[y * a.Width + x] = 255; labels[y * a.Width + x] = 1; }
        var kinds = DifferenceClassifier.Classify(a, b, p, raw, labels, [false, true], new(80, 90, 40, 30), out var removed, regions: map);
        using (removed)
        {
            Assert.Equal("changed", kinds[1]); Assert.NotNull(removed);
            Assert.Equal(10, Cv2.CountNonZero(removed));
            Assert.Equal((byte)255, removed.At<byte>(100, 99));
            Assert.Equal((byte)0, removed.At<byte>(100, 100));
        }
        using var rawDifference = new RawDifference(MatBuffers.Mask(raw, a.Width, a.Height), 0, 0);
        using var clustered = PageComparer.FromRawDifference(rawDifference, p, a, b);
        Assert.Equal("changed", Assert.Single(clustered.Clusters).Kind);
        Assert.Equal(10, Cv2.CountNonZero(clustered.RemovalMask!));
        Assert.Throws<ArgumentException>(() => PageComparer.FromRawDifference(rawDifference, p, a));
    }

    [Fact]
    public void ColorClassificationAndRawClusteringAcceptNonContinuousInputs()
    {
        using var parentA = new Mat(260, 340, MatType.CV_8UC3, Scalar.All(255));
        using var parentB = parentA.Clone();
        using var a = new Mat(parentA, new Rect(5, 5, 320, 240));
        using var b = new Mat(parentB, new Rect(5, 5, 320, 240));
        Cv2.Rectangle(a, new Rect(90, 100, 20, 10), new Scalar(180, 0, 0), -1);
        Cv2.Rectangle(b, new Rect(90, 100, 20, 10), new Scalar(0, 0, 180), -1);
        var p = new ComparisonParameters { Regions = [Region(0, "左", new(0, 0, 100, 240), Strict)] };
        using var result = PageComparer.Compare(a, b, p);
        Assert.False(a.IsContinuous()); Assert.Equal("color_changed", Assert.Single(result.Clusters).Kind);
        using var plainRaw = TolerantDifference.Calculate(a, b, p);
        using var separated = PageComparer.FromRawDifference(plainRaw, p with { Regions = [] }, a, b);
        using var ordinary = PageComparer.Compare(a, b, p with { Regions = [] });
        SameResult(ordinary, separated);
    }

    internal static DiffOptions Strict => new() { MaxShiftMm = 0, EdgeTolerance = 0 };
    internal static Mat White() => new(240, 320, MatType.CV_8UC3, Scalar.All(255));
    internal static RectMm Box(Rect box, int dpi = 300) => new(Units.PixelsToMm(box.X, dpi), Units.PixelsToMm(box.Y, dpi),
        Units.PixelsToMm(box.Width, dpi), Units.PixelsToMm(box.Height, dpi));
    internal static ComparisonRegion Region(int index, string name, Rect box, DiffOptions diff, int dpi = 300) => new(index, name, Box(box, dpi), "compare", diff);
    internal static void Draw(Mat image, int x, int y, int gray = 0)
    {
        foreach (var r in new[] { new Rect(0, 0, 4, 22), new Rect(0, 18, 18, 4), new Rect(14, 0, 4, 6) })
            Cv2.Rectangle(image, new Rect(x + r.X, y + r.Y, r.Width, r.Height), Scalar.All(gray), -1);
    }
    internal static void SameResult(PageComparison expected, PageComparison actual)
    {
        Assert.Equal(expected.Status, actual.Status); Assert.Equal(expected.RawPixels, actual.RawPixels);
        Assert.Equal(expected.NoiseDropped, actual.NoiseDropped); Assert.Equal(expected.Clusters.Count, actual.Clusters.Count);
        for (var i = 0; i < expected.Clusters.Count; i++)
        {
            var e = expected.Clusters[i]; var a = actual.Clusters[i];
            Assert.Equal(e.Id, a.Id); Assert.Equal(e.Bounds, a.Bounds); Assert.Equal(e.Pixels, a.Pixels);
            Assert.Equal(e.Kind, a.Kind); Assert.Equal(e.ShiftPx, a.ShiftPx); Assert.Equal(e.RelatedClusterIds, a.RelatedClusterIds);
        }
        Assert.Equal(0, Cv2.Norm(expected.RawMask, actual.RawMask, NormTypes.INF));
        Assert.Equal(0, Cv2.Norm(expected.LabelMask, actual.LabelMask, NormTypes.INF));
        Assert.Equal(expected.RemovalMask is null, actual.RemovalMask is null);
        if (expected.RemovalMask is not null) Assert.Equal(0, Cv2.Norm(expected.RemovalMask, actual.RemovalMask!, NormTypes.INF));
    }
}
