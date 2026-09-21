using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class DifferenceClassificationTests
{
    [Theory]
    [InlineData("D01", "removed")]
    [InlineData("D02", "removed")]
    [InlineData("D03", "changed")]
    [InlineData("D07", "color_changed")]
    [InlineData("D09", "changed")]
    [InlineData("D10", "added")]
    // 濃淡差でアンチエイリアスの一部がインクしきい値をまたぎ、元のインク形状が異なる。
    [InlineData("D14", "changed")]
    public void GoldenExamplesHaveConservativeKindsInBothDirections(string id, string kind)
    {
        using var a = GoldenData.Image(id, "a");
        using var b = GoldenData.Image(id, "b");
        using var forward = PageComparer.Compare(a, b, new());
        using var reverse = PageComparer.Compare(b, a, new());
        Assert.Equal(kind, Assert.Single(forward.Clusters).Kind);
        Assert.Equal(kind switch { "added" => "removed", "removed" => "added", _ => kind }, Assert.Single(reverse.Clusters).Kind);
    }

    [Theory]
    [InlineData(300, 0)]
    [InlineData(300, 0.3)]
    [InlineData(400, 0)]
    [InlineData(400, 0.3)]
    public void AllKindsAtPageEdgesAndNonContinuousInputs(int dpi, double edge)
    {
        using var parentA = White(260, 260);
        using var parentB = White(260, 260);
        using var a = new Mat(parentA, new Rect(10, 10, 240, 240));
        using var b = new Mat(parentB, new Rect(10, 10, 240, 240));
        Assert.False(a.IsContinuous());
        Cv2.Rectangle(b, new Rect(0, 0, 10, 8), Scalar.All(0), -1);
        Cv2.Rectangle(a, new Rect(230, 232, 10, 8), Scalar.All(0), -1);
        Cv2.Rectangle(a, new Rect(80, 80, 12, 8), new Scalar(180, 0, 0), -1);
        Cv2.Rectangle(b, new Rect(80, 80, 12, 8), new Scalar(0, 0, 180), -1);
        // 一つのクラスタに追加・削除を混在させる。
        Cv2.Rectangle(a, new Rect(80, 140, 6, 8), Scalar.All(0), -1);
        Cv2.Rectangle(b, new Rect(90, 140, 6, 8), Scalar.All(0), -1);
        var p = new ComparisonParameters { Move = new() { SearchMm = 0 }, Dpi = dpi, Diff = new() { MaxShiftMm = 0, EdgeTolerance = edge } };
        using var result = PageComparer.Compare(a, b, p);
        Assert.Equal(new[] { "added", "color_changed", "changed", "removed" }, result.Clusters.Select(c => c.Kind));
        Assert.NotNull(result.RemovalMask);
        Assert.Equal((byte)255, result.RemovalMask.At<byte>(235, 235));
        Assert.Equal((byte)0, result.RemovalMask.At<byte>(2, 2));
        using var continuousA = a.Clone(); using var continuousB = b.Clone();
        using var continuous = PageComparer.Compare(continuousA, continuousB, p);
        Assert.Equal(result.Clusters, continuous.Clusters);
        Assert.Equal(0, Cv2.Norm(result.RemovalMask, continuous.RemovalMask!, NormTypes.INF));
    }

    [Fact]
    public void FrameAndInnerClusterAreClassifiedIndependently()
    {
        using var a = White(200, 200); using var b = White(200, 200);
        Cv2.Rectangle(a, new Rect(20, 20, 160, 160), Scalar.All(0), 2);
        Cv2.Rectangle(b, new Rect(80, 80, 10, 10), Scalar.All(0), -1);
        using var result = PageComparer.Compare(a, b, Strict());
        Assert.Equal(new[] { "removed", "added" }, result.Clusters.Select(c => c.Kind));
        Assert.Equal(result.Clusters[0].Pixels, Cv2.CountNonZero(result.RemovalMask!));
        Assert.Equal((byte)0, result.RemovalMask!.At<byte>(85, 85));
    }

    [Fact]
    public void DroppedNoiseExcludedAndLimitedClustersDoNotGetRemovalPixels()
    {
        using var a = White(240, 100); using var b = White(240, 100);
        Cv2.Rectangle(a, new Rect(10, 20, 2, 1), Scalar.All(0), -1); // ノイズ。
        Cv2.Rectangle(a, new Rect(60, 20, 2, 2), Scalar.All(0), -1); // 上限で除外。
        Cv2.Rectangle(a, new Rect(120, 20, 8, 8), Scalar.All(0), -1); // 設定で除外。
        Cv2.Rectangle(a, new Rect(180, 20, 10, 10), Scalar.All(0), -1);
        var p = Strict() with
        {
            Cluster = new() { MinPixels = 4, MaxClustersPerPage = 1, MergeXMm = 0, MergeYMm = 0 },
            Exclude = [new(Units.PixelsToMm(119, 300), 0, Units.PixelsToMm(10, 300), 20)]
        };
        using var result = PageComparer.Compare(a, b, p);
        Assert.Equal("removed", Assert.Single(result.Clusters).Kind);
        Assert.Equal(1, result.NoiseDropped); Assert.Contains("CLUSTER_LIMIT", result.Warnings);
        Assert.Equal(100, Cv2.CountNonZero(result.RemovalMask!));
        Assert.Equal(106, result.RawPixels);
    }

    [Fact]
    public void DilationCannotTurnDifferentOriginalShapesIntoColorChange()
    {
        using var a = White(100, 100); using var b = White(100, 100);
        Cv2.Rectangle(a, new Rect(40, 40, 10, 10), new Scalar(180, 0, 0), -1);
        Cv2.Rectangle(b, new Rect(40, 40, 10, 10), new Scalar(0, 0, 180), -1);
        a.Set(45, 45, new Vec3b(255, 255, 255)); // 1px の穴は膨張で消える。
        using var result = PageComparer.Compare(a, b, new() { Diff = new() { MaxShiftMm = 0 } });
        Assert.Equal("changed", Assert.Single(result.Clusters).Kind);
        Assert.Null(result.RemovalMask); // 差分は両方の膨張したインク上にある。
        using var excluded = PageComparer.Compare(a, b, new()
        {
            Diff = new() { MaxShiftMm = 0 },
            Exclude = [new(Units.PixelsToMm(44, 300), Units.PixelsToMm(44, 300), Units.PixelsToMm(3, 300), Units.PixelsToMm(3, 300))]
        });
        Assert.Equal("color_changed", Assert.Single(excluded.Clusters).Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.3)]
    public void CroppedInkCalculationMatchesWholePageIncludingContextOutsideComponent(double edge)
    {
        using var a = White(240, 160); using var b = White(240, 160);
        // 周囲の明るい画素は対象の外。ROI の端を局所背景として誤用すると分類が変わる。
        a.SetTo(Scalar.All(80)); b.SetTo(Scalar.All(80));
        Cv2.Rectangle(a, new Rect(80, 50, 45, 45), Scalar.All(230), 2);
        Cv2.Rectangle(b, new Rect(80, 50, 45, 45), Scalar.All(230), 2);
        Cv2.Rectangle(a, new Rect(88, 58, 5, 5), Scalar.All(0), -1);
        var p = Strict() with { Diff = new() { MaxShiftMm = 0, EdgeTolerance = edge } };
        using var result = PageComparer.Compare(a, b, p);
        using var labA = ImageInk.ToLab(a); using var labB = ImageInk.ToLab(b);
        using var inkA = ImageInk.FromLab(labA, p.Dpi, p.Ink); using var inkB = ImageInk.FromLab(labB, p.Dpi, p.Ink);
        if (edge > 0)
        {
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            Cv2.Dilate(inkA, inkA, kernel); Cv2.Dilate(inkB, inkB, kernel);
        }
        var height = a.Height; var width = a.Width;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var removed = result.RawMask.At<byte>(y, x) != 0 && result.LabelMask.At<byte>(y, x) != 0
                && inkA.At<byte>(y, x) != 0 && inkB.At<byte>(y, x) == 0;
            Assert.Equal(removed, result.RemovalMask?.At<byte>(y, x) == 255);
        }
    }

    [Theory]
    [InlineData("same")]
    [InlineData("noise")]
    [InlineData("too_different")]
    public void UnclassifiedPagesAllocateNoRemovalMask(string scenario)
    {
        using var a = White(100, 100); using var b = White(100, 100);
        if (scenario == "noise") a.Set(40, 40, new Vec3b(0, 0, 0));
        if (scenario == "too_different") a.SetTo(Scalar.All(0));
        var timings = new ComparisonTimings();
        using var result = PageComparer.Compare(a, b, Strict(), true, timings);
        Assert.Empty(result.Clusters); Assert.Null(result.RemovalMask);
        Assert.Equal(scenario == "too_different" ? scenario : "same", result.Status);
        Assert.Equal(0, timings.ClassificationManagedBytes);
    }

    private static ComparisonParameters Strict() => new() { Diff = new() { MaxShiftMm = 0, EdgeTolerance = 0 } };
    private static Mat White(int width, int height) => new(height, width, MatType.CV_8UC3, Scalar.All(255));
}
