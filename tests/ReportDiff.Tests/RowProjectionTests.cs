using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

public sealed class RowProjectionTests
{
    [Fact]
    public void Split_cluster_keeps_identity_pixels_and_content_fill_without_reclustering()
    {
        using var images = new Images(200, 80, 80, 60);
        using var mask = Mask(images.A.Size());
        Cv2.Rectangle(mask, new(42, 78, 2, 4), Scalar.All(255), -1);
        var p = new ComparisonParameters { Cluster = new() { MergeXMm = 0, MergeYMm = 0, MinPixels = 5 } };
        using var raw = new RawDifference(mask.Clone(), 3, 2);
        using var content = PageComparer.Cluster(raw, p, retainProjection: true);
        using var result = RowProjection.Create(content, images.Surface, images.A, images.B, p);
        var c = Assert.Single(result.Comparison.Clusters);
        Assert.Equal(1, c.Id); Assert.Equal(8, c.Pixels); Assert.Equal(new Rect(42, 78, 2, 64), c.Bounds);
        Assert.Equal(8.0 / 128, c.FillRatio); Assert.Equal(1, c.Row!.ContentFillRatio);
        Assert.Equal(new Rect(42, 78, 2, 4), c.Row.ContentBounds);
        Assert.Equal([new Rect(42, 78, 2, 2), new Rect(42, 140, 2, 2)], c.Row.Parts.Select(p => p.DisplayBounds));
        Assert.Equal([4, 4], c.Row.Parts.Select(p => p.Pixels));
        Assert.Equal(3, result.Comparison.AbsorbedGroups); Assert.Equal(2, result.Comparison.MaxShiftPx);
        using var roundtrip = images.Surface.ToContent(result.Comparison.RawMask);
        Assert.Equal(0, Cv2.Norm(mask, roundtrip, NormTypes.INF));
        using var roi = new Mat(result.Comparison.RawMask, new Rect(0, 80, 80, 60)); Assert.Equal(0, Cv2.CountNonZero(roi));
    }

    [Fact]
    public void A_frame_and_an_inner_cluster_never_share_fragment_pixels()
    {
        using var images = new Images(200, 180, 80, 60);
        using var mask = Mask(images.A.Size());
        Cv2.Rectangle(mask, new(20, 40, 100, 100), Scalar.All(255), 1);
        Cv2.Rectangle(mask, new(60, 78, 4, 6), Scalar.All(255), -1);
        var p = new ComparisonParameters { Cluster = new() { MergeXMm = 0, MergeYMm = 0 } };
        using var raw = new RawDifference(mask.Clone(), 0, 0);
        using var content = PageComparer.Cluster(raw, p, retainProjection: true);
        using var result = RowProjection.Create(content, images.Surface, images.A, images.B, p);
        Assert.Equal(2, result.Comparison.Clusters.Count);
        foreach (var cluster in result.Comparison.Clusters)
        {
            Assert.Equal(cluster.Pixels, cluster.Row!.Parts.Sum(p => p.Pixels));
            Assert.Equal(content.Clusters.Single(c => c.Id == cluster.Row.ContentId).Pixels, cluster.Pixels);
        }
        var inner = result.Comparison.Clusters.Single(c => c.Pixels == 24);
        Assert.Equal([new Rect(60, 78, 4, 2), new Rect(60, 140, 4, 4)], inner.Row!.Parts.Select(p => p.DisplayBounds));
        Assert.Equal(396, result.Comparison.Clusters.Single(c => c.Id != inner.Id).Pixels);
    }

    [Theory]
    [InlineData("too_different")]
    [InlineData("noise")]
    [InlineData("limit")]
    public void Display_padding_does_not_change_noise_ratio_or_cluster_limits(string mode)
    {
        using var images = new Images(200, 80, 80, 100);
        using var mask = Mask(images.A.Size());
        if (mode == "too_different") Cv2.Rectangle(mask, new(0, 0, 32, 200), Scalar.All(255), -1);
        else
        {
            Cv2.Rectangle(mask, new(10, 78, 2, 4), Scalar.All(255), -1);
            Cv2.Rectangle(mask, new(50, 120, 2, 3), Scalar.All(255), -1);
            mask.Set(10, 70, (byte)255);
        }
        var p = new ComparisonParameters { Cluster = new() { MinPixels = 5, MergeXMm = 0, MergeYMm = 0,
            MaxClustersPerPage = mode == "limit" ? 1 : 500 } };
        using var raw = new RawDifference(mask.Clone(), 0, 0);
        using var content = PageComparer.Cluster(raw, p, retainProjection: true);
        using var result = RowProjection.Create(content, images.Surface, images.A, images.B, p);
        Assert.Equal(content.NoiseDropped, result.Comparison.NoiseDropped); Assert.Equal(content.RawPixels, result.Comparison.RawPixels);
        Assert.Equal(content.Warnings, result.Comparison.Warnings); Assert.Equal(content.Clusters.Count, result.Comparison.Clusters.Count);
        Assert.Equal(mode == "noise", result.DifferenceCountComplete);
        if (mode == "too_different") Assert.Equal("too_different", result.Comparison.Status);
        else Assert.Equal(1, result.Comparison.NoiseDropped);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(28, true)]
    [InlineData(55, true)]
    public void Movement_ids_follow_display_order_and_nonuniform_windows_are_omitted(int cut, bool omitted)
    {
        using var images = new Images(300, 400, cut, 30, white: true);
        using var cb = images.A.Clone();
        MovementTests.Draw(images.A, new(200, 50)); MovementTests.Draw(cb, new(160, 70));
        images.CopyContentB(cb);
        var p = new ComparisonParameters { Diff = new() { MaxShiftMm = 0, EdgeTolerance = 0 },
            Cluster = new() { MergeXMm = 0.6, MergeYMm = 0.3 } };
        using var content = PageComparer.Compare(images.A, cb, p, true, retainProjection: true);
        Assert.Equal(2, content.Clusters.Count); Assert.All(content.Clusters, c => Assert.Equal("moved", c.Kind));
        using var result = RowProjection.Create(content, images.Surface, images.A, images.B, p);
        Assert.Equal(2, result.Comparison.Clusters.Count);
        if (!omitted)
        {
            Assert.Equal([2, 1], result.Comparison.Clusters.Select(c => c.Row!.ContentId));
            Assert.All(result.Comparison.Clusters, c => Assert.Equal(new MovementShift(-40, 20), c.ShiftPx));
            Assert.Equal([2], result.Comparison.Clusters[0].RelatedClusterIds);
            Assert.Equal([1], result.Comparison.Clusters[1].RelatedClusterIds);
            Assert.Empty(result.AnnotationOmissions);
        }
        else
        {
            Assert.All(result.Comparison.Clusters, c => { Assert.Equal("changed", c.Kind); Assert.Null(c.ShiftPx); Assert.Empty(c.RelatedClusterIds); });
            var omission = Assert.Single(result.AnnotationOmissions);
            Assert.Equal("nonuniform_display_mapping", omission.Reason); Assert.Equal([1, 2], omission.ClusterIds);
        }
        using var raw = images.Surface.ToContent(result.Comparison.RawMask);
        using var removed = images.Surface.ToContent(result.Comparison.RemovalMask!);
        Assert.Equal(0, Cv2.Norm(content.RawMask, raw, NormTypes.INF));
        Assert.Equal(0, Cv2.Norm(content.RemovalMask!, removed, NormTypes.INF));
    }

    [Fact]
    public void A_movement_between_uniform_windows_gets_the_display_displacement()
    {
        using var images = new Images(500, 400, 200, 60, white: true);
        using var cb = images.A.Clone();
        MovementTests.Draw(images.A, new(200, 150)); MovementTests.Draw(cb, new(200, 250)); images.CopyContentB(cb);
        var p = new ComparisonParameters { Diff = new() { MaxShiftMm = 0, EdgeTolerance = 0 }, Move = new() { SearchMm = 12 } };
        using var content = PageComparer.Compare(images.A, cb, p, true, retainProjection: true);
        Assert.All(content.Clusters, c => Assert.Equal(new MovementShift(0, 100), c.ShiftPx)); Assert.NotEmpty(content.Clusters);
        using var display = RowProjection.Create(content, images.Surface, images.A, images.B, p);
        Assert.Empty(display.AnnotationOmissions);
        Assert.All(display.Comparison.Clusters, c => Assert.Equal(new MovementShift(0, 160), c.ShiftPx));
    }

    [Fact]
    public void Region_suppression_count_is_not_recounted_when_its_mask_splits()
    {
        using var images = new Images(200, 180, 80, 60, white: true);
        Cv2.Rectangle(images.A, new(60, 75, 20, 10), Scalar.All(128), -1);
        using var cb = images.A.Clone(); Cv2.Rectangle(cb, new(60, 75, 20, 10), Scalar.All(160), -1); images.CopyContentB(cb);
        var p = new ComparisonParameters { Regions = [new(0, "広い領域", new(0, 0, 100, 100), "compare", new() { ColorThreshold = 100 })] };
        using var content = RegionalComparer.Compare(images.A, cb, p, RegionMap.ForRows(images.Surface, p), retainProjection: true);
        Assert.Equal(1, content.Regional!.SuppressedComponents);
        using var display = RowProjection.Create(content, images.Surface, images.A, images.B, p);
        Assert.Equal(1, display.Comparison.Regional!.SuppressedComponents);
        Assert.Equal(content.Regional.SuppressedPixels, display.Comparison.Regional.SuppressedPixels);
        using var labels = new Mat(); Assert.Equal(3, Cv2.ConnectedComponents(display.Comparison.Regional.SuppressedMask, labels));
        Assert.Equal(content.Regional.Regions[0].Bounds, display.Regions[0].ContentBounds);
        Assert.Equal(new Rect(0, 0, 180, 260), display.Regions[0].DisplayBounds);
    }

    [Fact]
    public void Overlay_keeps_split_difference_parts_apart_and_inputs_unchanged()
    {
        using var images = new Images(240, 240, 100, 80, white: true);
        using var mask = Mask(images.A.Size()); Cv2.Rectangle(mask, new(140, 95, 10, 10), Scalar.All(255), -1);
        using var raw = new RawDifference(mask.Clone(), 0, 0);
        using var content = PageComparer.Cluster(raw, new(), retainProjection: true);
        using var display = RowProjection.Create(content, images.Surface, images.A, images.B, new());
        using var b = images.Surface.DisplayMap.Render(images.B, PageSpace.B); using var original = b.Clone();
        using var overlay = ReportImages.RowOverlay(b, display, 300);
        Assert.Equal(0, Cv2.Norm(b, original, NormTypes.INF));
        Assert.Equal(new Vec3b(0, 0, 255), overlay.At<Vec3b>(98, 145));
        Assert.Equal(new Vec3b(0, 0, 255), overlay.At<Vec3b>(182, 145));
        // 間の挿入帯を外接矩形の赤塗りや輪郭でつながない。
        Assert.Equal(original.At<Vec3b>(140, 145), overlay.At<Vec3b>(140, 145));
        var folder = Environment.GetEnvironmentVariable("REPORTDIFF_ROW_PREVIEW_DIR");
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "split-overlay.png"), overlay.ImEncode(".png"));
        }
    }

    internal static Mat Mask(Size size) => new(size, MatType.CV_8UC1, Scalar.All(0));
    internal sealed class Images : IDisposable
    {
        internal Mat A { get; }
        internal Mat B { get; }
        internal RowComparisonSurface Surface { get; }
        private readonly int cut, extra;
        internal Images(int height, int width, int cut, int extra, bool white = false)
        {
            this.cut = cut; this.extra = extra;
            A = new(height, width, MatType.CV_8UC3, Scalar.All(white ? 255 : 128));
            B = new(height + extra, width, MatType.CV_8UC3, Scalar.All(128)); CopyContentB(A);
            Surface = RowComparisonSurface.Create(A, B, new(A.Size(), B.Size(), B.Size(),
                [new(0, cut, 0, 0), new(cut, extra, null, cut), new(cut + extra, height - cut, cut, cut + extra)]),
                [RowBandKind.Paired, RowBandKind.Structural, RowBandKind.Paired], new()).Surface!;
        }
        internal void CopyContentB(Mat source)
        {
            using var top = new Mat(source, new Rect(0, 0, source.Width, cut)); using var targetTop = new Mat(B, new Rect(0, 0, source.Width, cut));
            top.CopyTo(targetTop);
            using var bottom = new Mat(source, new Rect(0, cut, source.Width, source.Height - cut));
            using var targetBottom = new Mat(B, new Rect(0, cut + extra, source.Width, source.Height - cut)); bottom.CopyTo(targetBottom);
        }
        public void Dispose() { A.Dispose(); B.Dispose(); }
    }
}
