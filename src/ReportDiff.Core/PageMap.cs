using OpenCvSharp;

namespace ReportDiff.Core;

public enum PageSpace { A, B, Canvas }

/// <summary>同じ長さの縦帯を対応付ける。元の開始位置が null の側は詰め物。範囲は半開区間。</summary>
public sealed record PageSegment(int CanvasStart, int Length, int? AStart, int? BStart)
{
    public long? Dy => AStart is int a && BStart is int b ? (long)a - b : null;
}

/// <summary>丸め前の四辺。幅から右端を再計算して PDF 座標の端数を変えない。</summary>
public readonly record struct PageBounds(double Left, double Top, double Right, double Bottom)
{
    public Rect2d Rectangle => new(Left, Top, Right - Left, Bottom - Top);
}

/// <summary>
/// 元 A・元 B と比較キャンバスの座標写像。縦帯は平行移動のみ、横は B の Dx のみ。
/// 画像を所有しない。設定の mm 矩形は比較キャンバス座標なので補正を再適用しない。
/// </summary>
public sealed class PageMap
{
    public Size SizeA { get; }
    public Size SizeB { get; }
    public Size CanvasSize { get; }
    public int Dx { get; }
    public IReadOnlyList<PageSegment> Segments { get; }

    public PageMap(Size sizeA, Size sizeB, Size canvasSize, IEnumerable<PageSegment> segments, int dx = 0)
    {
        if (sizeA.Width <= 0 || sizeA.Height <= 0 || sizeB.Width <= 0 || sizeB.Height <= 0
            || canvasSize.Width <= 0 || canvasSize.Height <= 0)
            throw new ArgumentException("写像のページ寸法は正にしてください。");
        var bands = segments.ToArray();
        long end = 0; long? endA = null; long? endB = null;
        foreach (var band in bands)
        {
            if (band.CanvasStart != end || band.Length <= 0 || (band.AStart is null && band.BStart is null))
                throw new ArgumentException("写像の帯はキャンバス全高を隙間なく覆い、少なくとも片側の元範囲を持つ必要があります。");
            end += band.Length;
            CheckSource(band.AStart, band.Length, ref endA);
            CheckSource(band.BStart, band.Length, ref endB);
        }
        if (end != canvasSize.Height) throw new ArgumentException("写像の帯とキャンバスの高さが一致していません。");
        SizeA = sizeA; SizeB = sizeB; CanvasSize = canvasSize; Dx = dx;
        Segments = Array.AsReadOnly(bands);
    }

    // 全体補正の端は元範囲がページ外へ伸びる。点・矩形・描画時に元サイズで切る。
    public static PageMap Global(Size sizeA, Size sizeB, Size canvasSize, GlobalShift? shift = null)
    {
        int? startB = shift?.Dy == int.MinValue ? null : -(shift?.Dy ?? 0);
        return new(sizeA, sizeB, canvasSize, [new(0, canvasSize.Height, 0, startB)], shift?.Dx ?? 0);
    }

    public static PageMap Unaligned(Size sizeA, Size sizeB) =>
        Global(sizeA, sizeB, new(Math.Max(sizeA.Width, sizeB.Width), Math.Max(sizeA.Height, sizeB.Height)));

    public Size SizeOf(PageSpace space) => space switch
    {
        PageSpace.A => SizeA, PageSpace.B => SizeB, PageSpace.Canvas => CanvasSize,
        _ => throw new ArgumentOutOfRangeException(nameof(space))
    };

    /// <summary>画素位置の対応。右端・下端、元画像外、詰め物には対応がない。</summary>
    public Point2d? MapPoint(Point2d point, PageSpace from, PageSpace to)
    {
        if (!Inside(point, SizeOf(from))) return null;
        _ = SizeOf(to);
        if (from == to) return point;
        foreach (var band in Segments)
        {
            if (Start(band, from) is not int source || Start(band, to) is not int target
                || point.Y < source || point.Y >= (double)source + band.Length) continue;
            var mapped = new Point2d(point.X + OffsetX(to) - OffsetX(from), point.Y + ((double)target - source));
            // A→B もキャンバス上に見える範囲に限る。
            var canvas = new Point2d(point.X - OffsetX(from), point.Y + ((double)band.CanvasStart - source));
            return Inside(canvas, CanvasSize) && Inside(mapped, SizeOf(to)) ? mapped : null;
        }
        return null;
    }

    /// <summary>
    /// 対応する各帯との正面積の交差を写し、その外接矩形を返す。
    /// 上端は次の帯、下端は前の帯を使うため、帯間の詰め物をまたぐ領域は高さが伸びる。
    /// 詰め物だけの領域には対応がない。
    /// </summary>
    public PageBounds? MapBounds(PageBounds bounds, PageSpace from, PageSpace to)
    {
        var sourceSize = SizeOf(from); var targetSize = SizeOf(to);
        if (!Finite(bounds)) return null;
        bounds = Clip(bounds, sourceSize);
        if (!Positive(bounds)) return null;
        if (from == to) return bounds;
        PageBounds? result = null;
        foreach (var band in Segments)
        {
            if (Start(band, from) is not int source || Start(band, to) is not int target) continue;
            var top = Math.Max(bounds.Top, source); var bottom = Math.Min(bounds.Bottom, (double)source + band.Length);
            if (bottom <= top) continue;
            var canvas = Clip(new(bounds.Left - OffsetX(from), top + ((double)band.CanvasStart - source),
                bounds.Right - OffsetX(from), bottom + ((double)band.CanvasStart - source)), CanvasSize);
            if (!Positive(canvas)) continue;
            var mapped = Clip(new(canvas.Left + OffsetX(to), canvas.Top + ((double)target - band.CanvasStart),
                canvas.Right + OffsetX(to), canvas.Bottom + ((double)target - band.CanvasStart)), targetSize);
            if (!Positive(mapped)) continue;
            result = result is { } r ? new(Math.Min(r.Left, mapped.Left), Math.Min(r.Top, mapped.Top),
                Math.Max(r.Right, mapped.Right), Math.Max(r.Bottom, mapped.Bottom)) : mapped;
        }
        return result;
    }

    /// <summary>整数画素を補間せず写す。対応しない帯・ページ端は白で埋める。戻り値は呼び出し側が解放する。</summary>
    public Mat Render(Mat source, PageSpace side)
    {
        if (side == PageSpace.Canvas || source.Empty() || source.Type() != MatType.CV_8UC3 || source.Size() != SizeOf(side))
            throw new ArgumentException("元 A/B のサイズと一致する BGR 8bit 画像を指定してください。");
        var output = new Mat(CanvasSize, MatType.CV_8UC3, Scalar.All(255));
        try
        {
            foreach (var band in Segments)
            {
                if (Start(band, side) is not int start) continue;
                var dx = -OffsetX(side); var dy = (double)band.CanvasStart - start;
                var bounds = Clip(new(dx, Math.Max(band.CanvasStart, dy), dx + source.Width,
                    Math.Min((double)band.CanvasStart + band.Length, dy + source.Height)), CanvasSize);
                if (!Positive(bounds)) continue;
                var target = new Rect((int)bounds.Left, (int)bounds.Top, (int)(bounds.Right - bounds.Left), (int)(bounds.Bottom - bounds.Top));
                using var input = new Mat(source, new Rect((int)(bounds.Left - dx), (int)(bounds.Top - dy), target.Width, target.Height));
                using var destination = new Mat(output, target);
                input.CopyTo(destination);
            }
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    /// <summary>PDF の左下原点を元描画サイズへ換算する。CropBox 原点は PDF リーダーが補正済み。</summary>
    public PageBounds PdfToSource(PageSpace side, double widthPoints, double heightPoints,
        double left, double bottom, double right, double top)
    {
        if (side == PageSpace.Canvas) throw new ArgumentException("PDF の元 A/B を指定してください。");
        var size = SizeOf(side);
        var sx = size.Width / widthPoints; var sy = size.Height / heightPoints;
        return new(left * sx, (heightPoints - top) * sy, right * sx, (heightPoints - bottom) * sy);
    }

    public static bool Finite(PageBounds b) => double.IsFinite(b.Left) && double.IsFinite(b.Top)
        && double.IsFinite(b.Right) && double.IsFinite(b.Bottom);

    /// <summary>比較キャンバスの mm 矩形を画素へ。左上切り捨て・右下切り上げの後、拡張してからクリップする。</summary>
    public static Rect CanvasRectangle(RectMm r, int dpi, Size size, int paddingPixels = 0)
    {
        var b = RoundedPixels(r, dpi);
        var left = (int)Math.Clamp(b.Left - paddingPixels, 0, size.Width);
        var top = (int)Math.Clamp(b.Top - paddingPixels, 0, size.Height);
        var right = (int)Math.Clamp(b.Right + paddingPixels, 0, size.Width);
        var bottom = (int)Math.Clamp(b.Bottom + paddingPixels, 0, size.Height);
        return new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public static PageBounds RoundedPixels(RectMm r, int dpi) => new(Math.Floor(Units.MmToPixels(r.X, dpi)),
        Math.Floor(Units.MmToPixels(r.Y, dpi)), Math.Ceiling(Units.MmToPixels(r.X + r.W, dpi)), Math.Ceiling(Units.MmToPixels(r.Y + r.H, dpi)));

    // テキストの除外判定は丸めない連続座標、画像の除外は上の整数座標を使う。
    public static Rect2d ContinuousPixels(RectMm r, int dpi) => new(Units.MmToPixels(r.X, dpi), Units.MmToPixels(r.Y, dpi),
        Units.MmToPixels(r.W, dpi), Units.MmToPixels(r.H, dpi));

    public static RectMm CanvasMillimeters(Rect r, int dpi) => new(Units.PixelsToMm(r.X, dpi), Units.PixelsToMm(r.Y, dpi),
        Units.PixelsToMm(r.Width, dpi), Units.PixelsToMm(r.Height, dpi));

    public static Rect CropRectangle(Rect bounds, Size size, double marginMm, int dpi)
    {
        var margin = Units.MmToPixels(marginMm, dpi);
        var left = (int)Math.Max(0, Math.Floor(bounds.X - margin));
        var top = (int)Math.Max(0, Math.Floor(bounds.Y - margin));
        var right = (int)Math.Min(size.Width, Math.Ceiling(bounds.Right + margin));
        var bottom = (int)Math.Min(size.Height, Math.Ceiling(bounds.Bottom + margin));
        return new(left, top, right - left, bottom - top);
    }

    public static RectMm ExclusionCandidate(RectMm box, Size size, int dpi, double marginMm)
    {
        var page = CanvasMillimeters(new(0, 0, size.Width, size.Height), dpi);
        var left = Math.Clamp(Math.Floor((box.X - marginMm) * 2) / 2, 0, page.W);
        var top = Math.Clamp(Math.Floor((box.Y - marginMm) * 2) / 2, 0, page.H);
        var right = Math.Clamp(Math.Ceiling((box.X + box.W + marginMm) * 2) / 2, left, page.W);
        var bottom = Math.Clamp(Math.Ceiling((box.Y + box.H + marginMm) * 2) / 2, top, page.H);
        return new(left, top, right - left, bottom - top);
    }

    private static void CheckSource(int? start, int length, ref long? end)
    {
        if (start is not int value) return;
        if (end is long previous && value < previous) throw new ArgumentException("元ページの帯は順序を保ち、重複しないようにしてください。");
        end = (long)value + length;
    }
    private static bool Inside(Point2d p, Size size) => p.X >= 0 && p.X < size.Width && p.Y >= 0 && p.Y < size.Height;
    private static bool Positive(PageBounds b) => b.Right > b.Left && b.Bottom > b.Top;
    private static PageBounds Clip(PageBounds b, Size size) => new(Math.Max(0, b.Left), Math.Max(0, b.Top),
        Math.Min(size.Width, b.Right), Math.Min(size.Height, b.Bottom));
    // 各空間の x = キャンバス x + offset。
    private double OffsetX(PageSpace space) => space == PageSpace.B ? -(double)Dx : 0;
    private static int? Start(PageSegment band, PageSpace space) => space switch
    {
        PageSpace.A => band.AStart, PageSpace.B => band.BStart, PageSpace.Canvas => band.CanvasStart,
        _ => throw new ArgumentOutOfRangeException(nameof(space))
    };
}
