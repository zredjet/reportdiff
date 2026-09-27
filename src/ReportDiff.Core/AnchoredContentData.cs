using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>元画像Oだけの全幅帯。G/C/Dの座標と共用しない。</summary>
public sealed record OriginalRowSpan
{
    public PageFlowPageKey Page { get; }
    public int Top { get; }
    public int Height { get; }
    public int Bottom => Top + Height;
    public OriginalRowSpan(PageFlowPageKey page, int top, int height)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (top < 0 || height <= 0 || (long)top + height > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(height));
        Page = page; Top = top; Height = height;
    }
    internal PageFlowBand ToBand() => new(Page, Top, Height);
}

public sealed record ContentSurfaceId
{
    public int OwnerPage { get; }
    public ContentSurfaceId(int ownerPage)
    { if (ownerPage < 1) throw new ArgumentOutOfRangeException(nameof(ownerPage)); OwnerPage = ownerPage; }
}

public sealed record ContentClusterKey
{
    public ContentSurfaceId Surface { get; }
    public int ClusterId { get; }
    public ContentClusterKey(ContentSurfaceId surface, int clusterId)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (clusterId < 1) throw new ArgumentOutOfRangeException(nameof(clusterId));
        Surface = surface; ClusterId = clusterId;
    }
}

public sealed record AnchoredContentPiece
{
    public int Top { get; }
    public int Height { get; }
    public OriginalRowSpan? A { get; }
    public OriginalRowSpan? B { get; }
    public AnchoredContentPiece(int top, int height, OriginalRowSpan? a, OriginalRowSpan? b)
    {
        if (top < 0 || height <= 0 || (long)top + height > int.MaxValue || a is null && b is null
            || a is not null && (a.Page.Side != PageSpace.A || a.Height != height)
            || b is not null && (b.Page.Side != PageSpace.B || b.Height != height))
            throw new ArgumentException("内容帯の側・高さ・範囲が不正です。");
        Top = top; Height = height; A = a; B = b;
    }
}

/// <summary>計画が所有する不変の内容面。元画素の一致・採用はまだ証明しない。</summary>
public sealed class AnchoredContentSurface
{
    public ContentSurfaceId Id { get; }
    public PageFlowPageKey Anchor { get; }
    public Size Size { get; }
    public IReadOnlyList<AnchoredContentPiece> Pieces { get; }
    // 配列は予約済みの構築処理から所有権を受け取る。公開APIから配列を受け取らない。
    internal AnchoredContentSurface(ContentSurfaceId id, PageFlowPageKey anchor, Size size, AnchoredContentPiece[] pieces)
    {
        if (id.OwnerPage != anchor.Page || size.Width <= 0 || size.Height <= 0) throw new ArgumentException("内容面の所属が不正です。");
        var next = 0;
        foreach (var piece in pieces)
        {
            var kept = anchor.Side == PageSpace.A ? piece.A : piece.B;
            if (piece.Top != next || kept?.Page != anchor || kept.Top != next || piece.Height > size.Height - next)
                throw new ArgumentException("保持側は元ページ全高を連続して覆う必要があります。");
            next += piece.Height;
        }
        if (next != size.Height) throw new ArgumentException("内容面に欠落があります。");
        Id = id; Anchor = anchor; Size = size; Pieces = Array.AsReadOnly(pieces);
    }
}

public enum AnchoredBandRole { Paired, Cause, Carry, WhiteSpace }
public sealed record AnchoredDisplaySegment(AnchoredContentPiece Band, AnchoredBandRole Role);

public sealed class AnchoredDisplayPlan
{
    public int Page { get; }
    public Size OriginalSize { get; }
    public Size Size { get; }
    public IReadOnlyList<AnchoredDisplaySegment> Segments { get; }
    internal AnchoredDisplayPlan(int page, Size originalSize, AnchoredDisplaySegment[] segments)
    {
        if (page < 1 || originalSize.Width <= 0 || originalSize.Height <= 0) throw new ArgumentException("表示ページが不正です。");
        int next = 0, a = 0, b = 0;
        foreach (var segment in segments)
        {
            var s = segment.Band;
            if (!Enum.IsDefined(segment.Role) || s.Top != next) throw new ArgumentException("表示帯の順序が不正です。");
            if ((segment.Role == AnchoredBandRole.Paired) != (s.A is not null && s.B is not null))
                throw new ArgumentException("表示帯の種類と元画像の有無が一致しません。");
            Check(s.A, ref a); Check(s.B, ref b); next = checked(next + s.Height);
        }
        if (a != originalSize.Height || b != originalSize.Height) throw new ArgumentException("表示面は両元画像の全行を各1回保持してください。");
        Page = page; OriginalSize = originalSize; Size = new(originalSize.Width, next); Segments = Array.AsReadOnly(segments);
        void Check(OriginalRowSpan? span, ref int position)
        {
            if (span is null) return;
            if (span.Page.Page != page || span.Top != position || span.Bottom > originalSize.Height)
                throw new ArgumentException("表示面の元物理ページ・範囲が不正です。");
            position = span.Bottom;
        }
    }
}

public sealed record OriginalPixelBounds
{
    public PageFlowPageKey Page { get; }
    public Rect Bounds { get; }
    public OriginalPixelBounds(PageFlowPageKey page, Rect bounds)
    { ArgumentNullException.ThrowIfNull(page); Validate(bounds); Page = page; Bounds = bounds; }
    internal static void Validate(Rect bounds)
    {
        if (bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0
            || (long)bounds.X + bounds.Width > int.MaxValue || (long)bounds.Y + bounds.Height > int.MaxValue)
            throw new ArgumentException("画素矩形の範囲が不正です。");
    }
}

[Flags] public enum ContentDisplaySides { A = 1, B = 2 }
public sealed record ContentDisplayPart
{
    public ContentClusterKey Content { get; }
    public int DisplayPage { get; }
    public Rect ContentBounds { get; }
    public Rect DisplayBounds { get; }
    public OriginalPixelBounds? SourceA { get; }
    public OriginalPixelBounds? SourceB { get; }
    public ContentDisplaySides DisplaySides { get; }
    public int Pixels { get; }
    public ContentDisplayPart(ContentClusterKey content, int page, Rect contentBounds, Rect displayBounds,
        OriginalPixelBounds? sourceA, OriginalPixelBounds? sourceB, ContentDisplaySides sides, int pixels)
    {
        ArgumentNullException.ThrowIfNull(content); OriginalPixelBounds.Validate(contentBounds); OriginalPixelBounds.Validate(displayBounds);
        if (page < 1 || contentBounds.Size != displayBounds.Size || contentBounds.X != displayBounds.X
            || pixels < 1 || pixels > (long)contentBounds.Width * contentBounds.Height || (int)sides is < 1 or > 3)
            throw new ArgumentException("表示断片の所属・寸法・画素数が不正です。");
        Check(sourceA, PageSpace.A, ContentDisplaySides.A); Check(sourceB, PageSpace.B, ContentDisplaySides.B);
        Content = content; DisplayPage = page; ContentBounds = contentBounds; DisplayBounds = displayBounds;
        SourceA = sourceA; SourceB = sourceB; DisplaySides = sides; Pixels = pixels;
        void Check(OriginalPixelBounds? source, PageSpace side, ContentDisplaySides flag)
        {
            if (source is not null && (source.Page.Side != side || source.Bounds.Size != contentBounds.Size || source.Bounds.X != contentBounds.X)
                || sides.HasFlag(flag) && (source is null || source.Page.Page != page))
                throw new ArgumentException("表示断片の元ページが不正です。");
        }
    }
}
