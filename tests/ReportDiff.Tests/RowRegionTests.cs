using System.Runtime.Versioning;
using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class RowRegionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Mapped_regions_preserve_ownership_exclusion_and_comparison(bool reverse, bool exclusionOnly)
    {
        using var old = new Mat(240, 160, MatType.CV_8UC3, Scalar.All(255));
        Cv2.Rectangle(old, new(40, 140, 30, 20), Scalar.All(128), -1);
        Cv2.Rectangle(old, new(90, 140, 10, 20), Scalar.All(0), -1);
        Cv2.Rectangle(old, new(20, 50, 20, 10), Scalar.All(0), -1);
        using var changed = old.Clone();
        Cv2.Rectangle(changed, new(40, 140, 30, 20), Scalar.All(132), -1);
        Cv2.Rectangle(changed, new(90, 140, 10, 20), Scalar.All(255), -1);
        Cv2.Rectangle(changed, new(100, 145, 10, 20), Scalar.All(0), -1);
        using var inserted = new Mat(280, 160, MatType.CV_8UC3, Scalar.All(255));
        Copy(changed, 0, inserted, 0, 80); Copy(changed, 80, inserted, 120, 160);
        Cv2.Rectangle(inserted, new(30, 90, 60, 10), Scalar.All(0), -1);
        var original = new ComparisonParameters
        {
            Dpi = 254, Exclude = [new(10, 14.01, 1, 0.48)],
            Regions = exclusionOnly ? [] : [new(7, "外側", new(1, 4.01, 14, 15.98), "compare", new() { ColorThreshold = 15 }),
                new(2, "内側", new(3, 13.01, 5, 3.98), "compare", new() { ColorThreshold = 1, EdgeTolerance = 0, MaxShiftMm = 0 }),
                new(9, "除外", new(4.5, 14.51, 1, 0.48), "exclude", new())]
        };
        // 元Aが挿入済みなら設定の下端・上端を手計算で40pxずらす。
        RectMm InSource(RectMm r) => r with { Y = r.Y >= 8 ? r.Y + 4 : r.Y, H = r.Y < 8 && r.Y + r.H > 8 ? r.H + 4 : r.H };
        var parameters = reverse ? original with { Exclude = original.Exclude.Select(InSource).ToArray(),
            Regions = original.Regions.Select(r => r with { Bounds = InSource(r.Bounds) }).ToArray() } : original;
        var a = reverse ? inserted : old; var b = reverse ? old : inserted;
        var bands = new[] { new PageSegment(0, 80, 0, 0), new PageSegment(80, 40, null, 80), new PageSegment(120, 160, 80, 120) };
        if (reverse) bands = bands.Select(s => s with { AStart = s.BStart, BStart = s.AStart }).ToArray();
        var surface = RowComparisonSurface.Create(a, b, new(a.Size(), b.Size(), inserted.Size(), bands),
            [RowBandKind.Paired, RowBandKind.Structural, RowBandKind.Paired], parameters).Surface!;
        Assert.NotNull(surface);
        var mapped = RegionMap.ForRows(surface, parameters); var reference = new RegionMap(old.Width, old.Height, original);
        Assert.Equal(reference.Owners, mapped.Owners); Assert.Equal(reference.Excluded, mapped.Excluded);
        Assert.Equal(reference.EffectivePixels, mapped.EffectivePixels); Assert.Equal(reference.Bounds, mapped.Bounds);
        using var ca = surface.ContentMap.Render(a, PageSpace.A); using var cb = surface.ContentMap.Render(b, PageSpace.B);
        using var result = RegionalComparer.Compare(ca, cb, parameters with { Exclude = [] }, mapped);
        using var expected = PageComparer.Compare(reverse ? changed : old, reverse ? old : changed, original);
        Assert.True(expected.RawPixels > 0);
        Assert.Equal(expected.RawPixels, result.RawPixels); Assert.Equal(expected.Clusters, result.Clusters);
        Assert.Equal(0, Cv2.Norm(expected.RawMask, result.RawMask, NormTypes.INF));
        Assert.Equal(0, Cv2.Norm(expected.LabelMask, result.LabelMask, NormTypes.INF));
        if (!exclusionOnly)
        {
            Assert.Equal(expected.Regional!.Regions, result.Regional!.Regions);
            Assert.Equal(expected.Regional.Runs, result.Regional.Runs);
            Assert.Equal(0, Cv2.Norm(expected.Regional.SuppressedMask, result.Regional.SuppressedMask, NormTypes.INF));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_excluded_number_stays_excluded_after_adoption(bool reverse)
    {
        using var input = new RowSelectionTests.Inputs("R02");
        var words = reverse ? input.TextB.Words : input.TextA.Words;
        var number = words.Single(w => w.Text == (reverse ? "111" : "555")).Bounds;
        var rect = new Rect((int)Math.Floor(number.Left) - 2, (int)Math.Floor(number.Top) - 2,
            (int)Math.Ceiling(number.Right) - (int)Math.Floor(number.Left) + 4,
            (int)Math.Ceiling(number.Bottom) - (int)Math.Floor(number.Top) + 4);
        var parameters = new ComparisonParameters { Exclude = [PageMap.CanvasMillimeters(rect, 300)] };
        using var result = RowComparer.Compare(reverse ? input.B : input.A, reverse ? input.A : input.B, parameters,
            new() { Enabled = true }, () => reverse ? (input.TextB, input.TextA) : (input.TextA, input.TextB));
        Assert.Equal("applied", result.Decision.Status); Assert.Empty(result.Comparison.Clusters);
        Assert.True(result.Comparison.Regional!.ExcludedPixels > 0);
    }

    private static void Copy(Mat from, int y, Mat to, int targetY, int height)
    {
        using var source = new Mat(from, new Rect(0, y, from.Width, height));
        using var target = new Mat(to, new Rect(0, targetY, from.Width, height)); source.CopyTo(target);
    }
}
