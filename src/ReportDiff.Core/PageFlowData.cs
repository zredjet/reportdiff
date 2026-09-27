using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>送りの元ページ。Canvas座標は受け付けない。</summary>
public sealed record PageFlowPageKey
{
    public PageSpace Side { get; }
    public int Page { get; }
    public PageFlowPageKey(PageSpace side, int page)
    {
        if (side is not (PageSpace.A or PageSpace.B) || page < 1) throw new ArgumentException("送りの元A/Bと1以上のページ番号を指定してください。");
        Side = side; Page = page;
    }
}

/// <summary>全幅帯。論理リンクは補正後G、OriginalLinksは元画像Oの座標。両者を混用しない。</summary>
public sealed record PageFlowBand
{
    public PageFlowPageKey Page { get; }
    public int Top { get; }
    public int Height { get; }
    public int Bottom => Top + Height;
    public PageFlowBand(PageFlowPageKey page, int top, int height)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (top < 0 || height <= 0 || (long)top + height > int.MaxValue) throw new ArgumentException("送り帯は正の高さを持つ元ページの範囲にしてください。");
        Page = page; Top = top; Height = height;
    }
}

public sealed record PageFlowLine(string Text, PageBounds Bounds, double Baseline);
public sealed record PageFlowUsage(int Pages, int Lines, long TextCharacters, long Pixels, long DescriptorBytes);

/// <summary>元画像Oの識別情報。補正後Gの行記述とは独立し、行ハッシュや本文は複製しない。</summary>
public sealed record PageFlowOriginalIdentity(PageFlowPageKey Key, Size Size, string PixelSha256);

/// <summary>比較座標Gの記述。Mat・PDF・語配列を保持しない。行ハッシュは候補絞り込み用。</summary>
public sealed class PageFlowPageDescriptor
{
    private readonly byte[] rowHashes;
    private readonly bool[] nonwhite;
    public PageFlowPageKey Key { get; }
    public Size Size { get; }
    public string TextStatus { get; }
    public string? TextDetail { get; }
    public IReadOnlyList<PageFlowLine> Lines { get; }
    public string PixelSha256 { get; }
    public PageMap? GlobalMap { get; }
    public PageFlowOriginalIdentity Original => original ?? new(Key, Size, PixelSha256);
    private readonly PageFlowOriginalIdentity? original;

    internal PageFlowPageDescriptor(PageFlowPageKey key, Size size, string textStatus, string? textDetail,
        PageFlowLine[] lines, byte[] hashes, bool[] nonwhiteRows, string digest, PageFlowOriginalIdentity? original = null, PageMap? globalMap = null)
    {
        this.original = original; GlobalMap = globalMap;
        Key = key; Size = size; TextStatus = textStatus; TextDetail = textDetail;
        Lines = Array.AsReadOnly(lines); rowHashes = hashes; nonwhite = nonwhiteRows; PixelSha256 = digest;
    }

    internal PageFlowBand? MapOriginalBand(PageFlowBand band)
    {
        if (band.Page != Key || band.Bottom > Size.Height) return null;
        if (GlobalMap is not { } map) return band;
        var parts = map.MapBoundsParts(new(0, band.Top, Size.Width, band.Bottom), PageSpace.Canvas, Key.Side);
        if (parts.Count != 1) return null;
        var bounds = parts[0];
        if (bounds.Left != 0 || bounds.Right != Original.Size.Width || bounds.Bottom - bounds.Top != band.Height
            || bounds.Top != (int)bounds.Top) return null;
        return new(band.Page, (int)bounds.Top, band.Height);
    }

    public bool RowHasNonwhite(int row) => (uint)row < (uint)Size.Height ? nonwhite[row]
        : throw new ArgumentOutOfRangeException(nameof(row));

    public bool SameRowHashes(PageFlowPageDescriptor other, int top, int otherTop, int height)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (top < 0 || otherTop < 0 || height <= 0 || (long)top + height > Size.Height || (long)otherTop + height > other.Size.Height)
            throw new ArgumentOutOfRangeException(nameof(height));
        return Size.Width == other.Size.Width && rowHashes.AsSpan(top * 32, height * 32)
            .SequenceEqual(other.rowHashes.AsSpan(otherTop * 32, height * 32));
    }
}

public sealed class PageFlowDocumentDescriptor
{
    public IReadOnlyList<PageFlowPageDescriptor> Pages { get; }
    public PageFlowUsage Usage { get; }
    internal PageFlowDocumentDescriptor(PageFlowPageDescriptor[] pages, PageFlowUsage usage)
    { Pages = Array.AsReadOnly(pages); Usage = usage; }
}
