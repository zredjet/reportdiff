using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class MovementTests
{
    [Theory]
    [InlineData(24, 0)]
    [InlineData(-24, 0)]
    [InlineData(0, 24)]
    [InlineData(0, -24)]
    [InlineData(24, 24)]
    [InlineData(-24, 24)]
    [InlineData(24, -24)]
    [InlineData(-24, -24)]
    public void UniqueTranslationIsAnnotatedInBothDirections(int dx, int dy)
    {
        using var a = White(); using var b = White();
        Draw(a, new(200, 160)); Draw(b, new(200 + dx, 160 + dy));
        using var result = PageComparer.Compare(a, b, new());
        AssertMovement(result, dx, dy);
        using var reverse = PageComparer.Compare(b, a, new());
        AssertMovement(reverse, -dx, -dy);
        AssertDetectionUnchanged(a, b, new(), result);
    }

    [Theory]
    [InlineData(300, 0)]
    [InlineData(300, 0.3)]
    [InlineData(400, 0)]
    [InlineData(400, 0.3)]
    public void SearchBoundaryAndSeparateClustersUseSameVector(int dpi, double edge)
    {
        var radius = (int)Math.Floor(Units.MmToPixels(5, dpi));
        var p = new ComparisonParameters { Dpi = dpi, Diff = new() { EdgeTolerance = edge, MaxShiftMm = 0 } };
        using var parentA = new Mat(420, 620, MatType.CV_8UC3, Scalar.All(255));
        using var parentB = parentA.Clone();
        using var a = new Mat(parentA, new Rect(10, 10, 600, 400));
        using var b = new Mat(parentB, new Rect(10, 10, 600, 400));
        Draw(a, new(200, 160)); Draw(b, new(200 + radius, 160));
        Assert.False(a.IsContinuous());
        using var result = PageComparer.Compare(a, b, p);
        Assert.Equal(2, result.Clusters.Count);
        AssertMovement(result, radius, 0);
        Assert.Equal(new[] { 2 }, result.Clusters[0].RelatedClusterIds);
        Assert.Equal(new[] { 1 }, result.Clusters[1].RelatedClusterIds);
        AssertDetectionUnchanged(a, b, p, result);
        using var cloneA = a.Clone(); using var cloneB = b.Clone();
        using var continuous = PageComparer.Compare(cloneA, cloneB, p);
        AssertMovement(continuous, radius, 0);
        b.SetTo(Scalar.All(255)); Draw(b, new(201 + radius, 160));
        using var outside = PageComparer.Compare(a, b, p);
        AssertNoMovement(outside);
    }

    [Fact]
    public void StrictOnePixelMoveIsStillADifferenceAndCanBeAnnotated()
    {
        using var a = White(); using var b = White();
        Draw(a, new(200, 160)); Draw(b, new(201, 160));
        using var strict = PageComparer.Compare(a, b, Strict());
        AssertMovement(strict, 1, 0);
        using var normal = PageComparer.Compare(a, b, new());
        Assert.Equal("same", normal.Status); Assert.Empty(normal.Clusters);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("repeat-b")]
    [InlineData("repeat-a")]
    [InlineData("color")]
    [InlineData("gray")]
    [InlineData("shape")]
    [InlineData("leftover")]
    [InlineData("stationary-color")]
    [InlineData("flat")]
    [InlineData("long-line")]
    [InlineData("competing")]
    [InlineData("partial-group")]
    public void AmbiguousOrIncompleteChangesKeepOriginalClassification(string scenario)
    {
        using var a = White(); using var b = White();
        Draw(a, new(200, 160)); Draw(b, new(259, 160));
        switch (scenario)
        {
            case "copy": Draw(b, new(200, 160)); break;
            case "repeat-b": Draw(b, new(141, 160)); break;
            case "repeat-a": Draw(a, new(318, 160)); break; // 逆方向の候補が二つ。
            case "color": b.SetTo(Scalar.All(255)); Draw(b, new(259, 160), new(0, 0, 160)); break;
            case "gray": b.SetTo(Scalar.All(255)); Draw(b, new(259, 160), Scalar.All(80)); break;
            case "shape": Cv2.Line(b, new(263, 164), new(271, 175), Scalar.All(0), 2); break;
            case "leftover": Cv2.Rectangle(b, new Rect(200, 160, 5, 5), Scalar.All(0), -1); break;
            case "stationary-color": b.SetTo(Scalar.All(255)); Draw(b, new(200, 160), Scalar.All(80)); break;
            case "flat": a.SetTo(Scalar.All(245)); b.SetTo(Scalar.All(255)); break;
            case "long-line":
                a.SetTo(Scalar.All(255)); b.SetTo(Scalar.All(255));
                Cv2.Rectangle(a, new Rect(160, 160, 200, 6), Scalar.All(0), -1);
                Cv2.Rectangle(b, new Rect(184, 160, 200, 6), Scalar.All(0), -1); break;
            case "partial-group": Draw(a, new(220, 160)); Draw(b, new(220, 160)); break;
            case "competing":
                a.SetTo(Scalar.All(255)); b.SetTo(Scalar.All(255));
                Draw(a, new(160, 160)); Draw(a, new(240, 160)); Draw(b, new(200, 160)); break;
        }
        using var result = PageComparer.Compare(a, b, new());
        AssertNoMovement(result);
        AssertDetectionUnchanged(a, b, new(), result, kinds: true);
    }

    [Theory]
    [InlineData("excluded-source")]
    [InlineData("excluded-target")]
    [InlineData("limit")]
    [InlineData("noise")]
    [InlineData("edge")]
    public void MissingEvidenceDoesNotGetMovementLabels(string scenario)
    {
        using var a = White(); using var b = White();
        var x = scenario == "edge" ? 0 : 200;
        Draw(a, new(x, 160)); Draw(b, new(x + 59, 160));
        var p = Strict();
        if (scenario.StartsWith("excluded", StringComparison.Ordinal))
            p = p with { Exclude = [new(Units.PixelsToMm(scenario == "excluded-source" ? 198 : 257, 300),
                Units.PixelsToMm(158, 300), Units.PixelsToMm(10, 300), Units.PixelsToMm(10, 300))] };
        if (scenario == "limit") p = p with { Cluster = new() { MaxClustersPerPage = 1 } };
        if (scenario == "noise")
        {
            a.Set(165, 194, new Vec3b(0, 0, 0));
            p = p with { Cluster = new() { MergeXMm = 0, MergeYMm = 0 } };
        }
        using var result = PageComparer.Compare(a, b, p);
        AssertNoMovement(result);
        AssertDetectionUnchanged(a, b, p, result, kinds: true);
    }

    [Fact]
    public void InnerMovementDoesNotAbsorbChangedSurroundingFrame()
    {
        using var a = White(); using var b = White();
        Cv2.Rectangle(a, new Rect(50, 50, 450, 300), Scalar.All(0), 3);
        Cv2.Rectangle(b, new Rect(50, 50, 450, 300), Scalar.All(80), 3);
        Draw(a, new(200, 160)); Draw(b, new(224, 160));
        using var result = PageComparer.Compare(a, b, new());
        Assert.Equal(2, result.Clusters.Count);
        Assert.NotEqual("moved", result.Clusters[0].Kind);
        Assert.Equal("moved", result.Clusters[1].Kind);
        Assert.Equal(new MovementShift(24, 0), result.Clusters[1].ShiftPx);
        Assert.Empty(result.Clusters[1].RelatedClusterIds);
        AssertDetectionUnchanged(a, b, new(), result);
    }

    internal static void AssertDetectionUnchanged(Mat a, Mat b, ComparisonParameters p, PageComparison result, bool kinds = false)
    {
        using var baseline = PageComparer.Compare(a, b, p with { Move = p.Move with { SearchMm = 0 } });
        Assert.Equal(baseline.Status, result.Status);
        Assert.Equal(baseline.RawPixels, result.RawPixels);
        Assert.Equal(baseline.NoiseDropped, result.NoiseDropped);
        Assert.Equal(baseline.AbsorbedGroups, result.AbsorbedGroups);
        Assert.Equal(baseline.MaxShiftPx, result.MaxShiftPx);
        Assert.Equal(baseline.Warnings, result.Warnings);
        Assert.Equal(baseline.Clusters.Select(c => (c.Id, c.Bounds, c.Pixels)), result.Clusters.Select(c => (c.Id, c.Bounds, c.Pixels)));
        if (kinds) Assert.Equal(baseline.Clusters.Select(c => c.Kind), result.Clusters.Select(c => c.Kind));
        Assert.Equal(0, Cv2.Norm(baseline.RawMask, result.RawMask, NormTypes.INF));
        Assert.Equal(0, Cv2.Norm(baseline.LabelMask, result.LabelMask, NormTypes.INF));
        Assert.Equal(baseline.RemovalMask is null, result.RemovalMask is null);
        if (baseline.RemovalMask is not null) Assert.Equal(0, Cv2.Norm(baseline.RemovalMask, result.RemovalMask!, NormTypes.INF));
    }

    private static void AssertMovement(PageComparison result, int dx, int dy)
    {
        Assert.Equal("different", result.Status); Assert.NotEmpty(result.Clusters);
        foreach (var cluster in result.Clusters)
        {
            Assert.Equal("moved", cluster.Kind);
            Assert.Equal(new MovementShift(dx, dy), cluster.ShiftPx);
            Assert.Equal(result.Clusters.Where(c => c.Id != cluster.Id).Select(c => c.Id), cluster.RelatedClusterIds);
        }
    }

    private static void AssertNoMovement(PageComparison result)
    {
        foreach (var cluster in result.Clusters)
        {
            Assert.NotEqual("moved", cluster.Kind); Assert.Null(cluster.ShiftPx); Assert.Empty(cluster.RelatedClusterIds);
        }
    }

    internal static void Draw(Mat image, Point origin, Scalar? color = null)
    {
        var ink = color ?? Scalar.All(0);
        Cv2.Rectangle(image, new Rect(origin.X, origin.Y, 6, 22), ink, -1);
        Cv2.Rectangle(image, new Rect(origin.X, origin.Y + 16, 18, 6), ink, -1);
        Cv2.Rectangle(image, new Rect(origin.X + 12, origin.Y, 6, 5), ink, -1);
    }
    private static Mat White() => new(400, 600, MatType.CV_8UC3, Scalar.All(255));
    private static ComparisonParameters Strict() => new() { Diff = new() { MaxShiftMm = 0, EdgeTolerance = 0 } };
}
