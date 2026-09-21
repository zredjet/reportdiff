using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class PageMapTests
{
    // B に 2 行、A に 3 行を挿入したキャンバス。両側に詰め物を持つ。
    private static PageMap Bands(int dx = 0) => new(new(8, 10), new(8, 9), new(8, 12),
        [new(0, 3, 0, 0), new(3, 2, null, 3), new(5, 4, 3, 5), new(9, 3, 7, null)], dx);

    [Theory]
    [InlineData(PageSpace.A)] [InlineData(PageSpace.B)]
    public void EveryVisiblePixelRoundTripsAndPaddingHasNoSource(PageSpace side)
    {
        var map = Bands(1);
        var size = map.SizeOf(side);
        for (var y = 0; y < size.Height; y++)
        for (var x = 0; x < size.Width; x++)
        {
            var p = new Point2d(x, y);
            var canvas = map.MapPoint(p, side, PageSpace.Canvas);
            if (side == PageSpace.B && x == 7) Assert.Null(canvas);
            else Assert.Equal(p, map.MapPoint(canvas!.Value, PageSpace.Canvas, side));
        }
        Assert.Null(map.MapPoint(new(1, 3.5), PageSpace.Canvas, PageSpace.A));
        Assert.Null(map.MapPoint(new(1, 10), PageSpace.Canvas, PageSpace.B));
        Assert.Null(map.MapPoint(new(0, 1), PageSpace.Canvas, PageSpace.B));
    }

    [Fact]
    public void BoundariesUseFollowingBandForPointsAndPreviousBandForRectangleBottom()
    {
        var map = Bands();
        Assert.Equal(new Point2d(1, 5), map.MapPoint(new(1, 3), PageSpace.A, PageSpace.Canvas));
        Assert.Equal(new PageBounds(1, 1, 4, 3), map.MapBounds(new(1, 1, 4, 3), PageSpace.A, PageSpace.Canvas));
        Assert.Equal(new PageBounds(1, 5, 4, 7), map.MapBounds(new(1, 3, 4, 5), PageSpace.A, PageSpace.Canvas));
        Assert.Equal(new PageBounds(1, 2, 4, 7), map.MapBounds(new(1, 2, 4, 5), PageSpace.A, PageSpace.Canvas));
        Assert.Equal(new PageBounds(1, 2, 4, 5), map.MapBounds(new(1, 2, 4, 7), PageSpace.Canvas, PageSpace.A));
        Assert.Null(map.MapBounds(new(1, 3, 4, 5), PageSpace.Canvas, PageSpace.A));
        Assert.Null(map.MapBounds(new(1, 9, 4, 12), PageSpace.Canvas, PageSpace.B));
        Assert.Null(map.MapPoint(new(1, 12), PageSpace.Canvas, PageSpace.A));
        Assert.Null(map.MapPoint(new(1, 10), PageSpace.A, PageSpace.Canvas));
        Assert.Null(map.MapPoint(new(8, 1), PageSpace.A, PageSpace.Canvas));
        Assert.Null(map.MapPoint(new(-1, 1), PageSpace.A, PageSpace.Canvas));
    }

    [Fact]
    public void DirectSideMappingHonorsMissingBandsAndHorizontalShift()
    {
        var map = Bands(-1);
        Assert.Equal(new Point2d(3, 6), map.MapPoint(new(2, 4), PageSpace.A, PageSpace.B));
        Assert.Equal(new Point2d(2, 4), map.MapPoint(new(3, 6), PageSpace.B, PageSpace.A));
        Assert.Null(map.MapPoint(new(2, 3.5), PageSpace.B, PageSpace.A));
        Assert.Null(map.MapPoint(new(2, 8), PageSpace.A, PageSpace.B));
        Assert.Equal(new PageBounds(3, 2, 5, 7), map.MapBounds(new(2, 2, 4, 5), PageSpace.A, PageSpace.B));
        Assert.Equal(-2L, map.Segments[2].Dy);
        Assert.Null(map.Segments[1].Dy);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(2, 3)] [InlineData(-2, 3)] [InlineData(2, -3)] [InlineData(-2, -3)]
    [InlineData(7, 9)] [InlineData(-7, -9)]
    public void GlobalMapUsesOneBandAndRendersExactTranslation(int dx, int dy)
    {
        var map = PageMap.Global(new(8, 10), new(8, 10), new(8, 10), new(dx, dy));
        Assert.Single(map.Segments);
        using var source = Pattern(8, 10);
        using var rendered = map.Render(source, PageSpace.B);
        for (var y = 0; y < 10; y++)
        for (var x = 0; x < 8; x++)
        {
            var valid = x - dx >= 0 && x - dx < 8 && y - dy >= 0 && y - dy < 10;
            Assert.Equal(valid ? source.At<Vec3b>(y - dy, x - dx) : new Vec3b(255, 255, 255), rendered.At<Vec3b>(y, x));
            Assert.Equal(valid ? new Point2d(x - dx, y - dy) : (Point2d?)null,
                map.MapPoint(new(x, y), PageSpace.Canvas, PageSpace.B));
        }
    }

    [Theory]
    [InlineData(PageSpace.A)] [InlineData(PageSpace.B)]
    public void MultipleBandsRenderSourceRowsWithOnlyPaddingAdded(PageSpace side)
    {
        var map = Bands(); var size = map.SizeOf(side);
        using var source = Pattern(size.Width, size.Height);
        using var result = map.Render(source, side);
        int?[] expectedRows = side == PageSpace.A ? [0, 1, 2, null, null, 3, 4, 5, 6, 7, 8, 9]
            : [0, 1, 2, 3, 4, 5, 6, 7, 8, null, null, null];
        for (var y = 0; y < 12; y++)
        for (var x = 0; x < 8; x++)
            Assert.Equal(expectedRows[y] is int row ? source.At<Vec3b>(row, x) : new Vec3b(255, 255, 255), result.At<Vec3b>(y, x));
    }

    [Fact]
    public void NormalizationKeepsOriginalSizeAndHasNoInverseInWhitePadding()
    {
        var map = PageMap.Global(new(6, 5), new(8, 4), new(8, 5));
        Assert.Null(map.MapPoint(new(6, 1), PageSpace.Canvas, PageSpace.A));
        Assert.Null(map.MapPoint(new(1, 4), PageSpace.Canvas, PageSpace.B));
        Assert.Equal(new PageBounds(0, 0, 6, 5), map.MapBounds(new(-3, -3, 9, 9), PageSpace.A, PageSpace.Canvas));
        Assert.Equal(new PageBounds(0, 0, 8, 4), map.MapBounds(new(-3, -3, 9, 9), PageSpace.B, PageSpace.Canvas));
        using var source = Pattern(6, 5); using var rendered = map.Render(source, PageSpace.A);
        Assert.Equal(source.At<Vec3b>(4, 5), rendered.At<Vec3b>(4, 5));
        Assert.Equal(new Vec3b(255, 255, 255), rendered.At<Vec3b>(4, 6));
    }

    [Fact]
    public void PdfBoundsUseOriginalRasterThenClipBeforeTranslation()
    {
        var map = PageMap.Global(new(300, 400), new(300, 400), new(300, 400), new(20, -10));
        var source = map.PdfToSource(PageSpace.B, 150, 200, -5, 175, 10, 205);
        Assert.Equal(new PageBounds(-10, -10, 20, 50), source);
        Assert.Equal(new PageBounds(20, 0, 40, 40), map.MapBounds(source, PageSpace.B, PageSpace.Canvas));
        // 元ページ外の文字を補正で復活させない。
        Assert.Null(map.MapBounds(new(-10, 20, -1, 30), PageSpace.B, PageSpace.Canvas));
    }

    [Theory]
    [InlineData(72)] [InlineData(144)] [InlineData(254)] [InlineData(300)] [InlineData(1200)]
    public void CoordinateConversionsPreserveFractionalEdgesAndExpansionOrder(int dpi)
    {
        var size = new Size(101, 99);
        var box = PageMap.CanvasMillimeters(new(1, 2, 3, 4), dpi);
        Assert.Equal(Units.PixelsToMm(1, dpi), box.X);
        Assert.Equal(Units.PixelsToMm(4, dpi), box.H);
        var mm = new RectMm(Units.PixelsToMm(1.25, dpi), Units.PixelsToMm(2.25, dpi),
            Units.PixelsToMm(3.5, dpi), Units.PixelsToMm(4.5, dpi));
        Assert.Equal(new Rect(1, 2, 4, 5), PageMap.CanvasRectangle(mm, dpi, size));
        Assert.InRange(PageMap.ContinuousPixels(mm, dpi).X, 1.24999999, 1.25000001);
        var outside = new RectMm(Units.PixelsToMm(200, dpi), 0, 1, 1);
        Assert.Equal(0, PageMap.CanvasRectangle(outside, dpi, size, 3).Width);
        Assert.Equal(new Rect(0, 0, 5, 5), PageMap.CropRectangle(new(1, 1, 2, 2), size, Units.PixelsToMm(1.5, dpi), dpi));
    }

    [Fact]
    public void InvalidBoundsHaveNoMappingAndSegmentDefinitionsAreValidated()
    {
        var map = Bands();
        foreach (var b in new PageBounds[] { new(0, 0, 0, 3), new(1, 3, 2, 1), new(double.NaN, 0, 3, 3), new(0, 0, double.PositiveInfinity, 3) })
            Assert.Null(map.MapBounds(b, PageSpace.A, PageSpace.Canvas));
        foreach (var bands in new PageSegment[][] { [], [new(1, 11, 0, 0)], [new(0, 12, null, null)],
            [new(0, 0, 0, 0), new(0, 12, 0, 0)], [new(0, 5, 0, 0), new(5, 7, 4, 5)], [new(0, 11, 0, 0)] })
            Assert.Throws<ArgumentException>(() => new PageMap(new(8, 12), new(8, 12), new(8, 12), bands));
        Assert.Null(PageMap.Global(new(8, 10), new(8, 10), new(8, 10), new(int.MinValue, int.MinValue))
            .MapPoint(new(1, 1), PageSpace.B, PageSpace.Canvas));
        var original = new PageSegment[] { new(0, 12, 0, 0) };
        var owned = new PageMap(new(8, 12), new(8, 12), new(8, 12), original);
        original[0] = new(0, 12, null, 0);
        Assert.Equal(0, owned.Segments[0].AStart);
    }

    private static Mat Pattern(int width, int height)
    {
        var image = new Mat(height, width, MatType.CV_8UC3);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++) image.Set(y, x, new Vec3b((byte)(y + 1), (byte)(x + 1), 70));
        return image;
    }
}
