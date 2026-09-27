using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class AnchoredContentDataTests
{
    [Theory]
    [InlineData(-1, 1)] [InlineData(0, 0)] [InlineData(0, -1)] [InlineData(int.MaxValue, 1)]
    public void Original_spans_reject_invalid_half_open_ranges(int top, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new OriginalRowSpan(new(PageSpace.A, 1), top, height));

    [Fact]
    public void C_does_not_accept_missing_anchors_foreign_pages_or_uncovered_edges()
    {
        var a = new PageFlowPageKey(PageSpace.A, 1); var b = new PageFlowPageKey(PageSpace.B, 1);
        Assert.Throws<ArgumentException>(() => new AnchoredContentPiece(0, 5, null, null));
        Assert.Throws<ArgumentException>(() => new AnchoredContentPiece(0, 5, new(b, 0, 5), null));
        Assert.Throws<ArgumentException>(() => new AnchoredContentPiece(0, 5, new(a, 0, 4), null));
        foreach (var bad in new[] {
            new AnchoredContentPiece[] { new(0, 5, new(a, 0, 5), new(b, 0, 5)) },
            [new(0, 10, new(new(PageSpace.A, 2), 0, 10), new(b, 0, 10))],
            [new(0, 5, new(a, 0, 5), new(b, 0, 5)), new(5, 5, new(a, 4, 5), new(b, 5, 5))],
            [new(0, 5, new(a, 0, 5), new(b, 0, 5)), new(6, 4, new(a, 6, 4), new(b, 6, 4))],
            [new(0, 10, null, new(b, 0, 10))] })
            Assert.Throws<ArgumentException>(() => new AnchoredContentSurface(new(1), a, new(20, 10), bad));
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)]
    public void Content_keys_require_real_owner_pages_and_cluster_ids(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentSurfaceId(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentClusterKey(new(1), value));
    }

    [Fact]
    public void Physical_display_preserves_every_original_row_and_validates_padding_roles()
    {
        var a = new PageFlowPageKey(PageSpace.A, 1); var b = new PageFlowPageKey(PageSpace.B, 1);
        var first = new AnchoredDisplaySegment(new(0, 5, new(a, 0, 5), new(b, 0, 5)), AnchoredBandRole.Paired);
        Assert.Throws<ArgumentException>(() => new AnchoredDisplayPlan(1, new(20, 10), [first]));
        Assert.Throws<ArgumentException>(() => new AnchoredDisplayPlan(2, new(20, 5), [first]));
        Assert.Throws<ArgumentException>(() => new AnchoredDisplayPlan(1, new(20, 5), [first with { Role = AnchoredBandRole.Carry }]));
        Assert.Throws<ArgumentException>(() => new AnchoredDisplayPlan(1, new(20, 10), [first,
            new(new(5, 5, new(a, 4, 5), new(b, 5, 5)), AnchoredBandRole.Paired)]));
    }

    [Fact]
    public void Parts_keep_cross_page_provenance_and_small_fragments_without_counting_twice()
    {
        var content = new ContentClusterKey(new(1), 1); var c = new Rect(581, 700, 2, 1); var d = new Rect(581, 300, 2, 1);
        var a = new OriginalPixelBounds(new(PageSpace.A, 1), c); var b = new OriginalPixelBounds(new(PageSpace.B, 2), d);
        var part = new ContentDisplayPart(content, 2, c, d, a, b, ContentDisplaySides.B, 2);
        Assert.Equal(1, part.Content.Surface.OwnerPage); Assert.Equal(2, part.DisplayPage); Assert.Equal(2, part.Pixels);
        Assert.Throws<ArgumentException>(() => new ContentDisplayPart(content, 2, c, d, a, b, ContentDisplaySides.A, 2));
        Assert.Throws<ArgumentException>(() => new ContentDisplayPart(content, 2, c, d, a, null, ContentDisplaySides.B, 2));
        Assert.Throws<ArgumentException>(() => new ContentDisplayPart(content, 2, c, d, a, b, ContentDisplaySides.B, 3));
        Assert.Throws<ArgumentException>(() => new ContentDisplayPart(content, 2, c, d, a, b, 0, 2));
        Assert.Throws<ArgumentException>(() => new OriginalPixelBounds(new(PageSpace.A, 1), new(int.MaxValue, 0, 2, 1)));
    }
}
