using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>Structural は行の支持・帯の過不足を別途検証した帯だけに指定する。画素の非白だけから推定しない。</summary>
public enum RowBandKind { Paired, Structural, WhiteSpace }

public sealed record RowContentPiece(int ContentStart, int DisplayStart, int Length, int? AStart, int? BStart, RowBandKind Kind);
public sealed record RowMaskFragment(Rect ContentBounds, Rect DisplayBounds, PageBounds? SourceA, PageBounds? SourceB, int Pixels);
public sealed record RowSurfaceBuild(RowComparisonSurface? Surface, string? Detail)
{
    public bool Success => Surface is not null;
}

/// <summary>
/// 全体補正後の A/B を入力とする、内容比較面 C と表示面 D の対応。
/// 入力・出力画像を所有しない。C で通常の比較を完結し、D は結果の表示にだけ用いる。
/// </summary>
public sealed class RowComparisonSurface
{
    public PageMap DisplayMap { get; }
    public PageMap ContentMap { get; }
    public IReadOnlyList<RowContentPiece> Pieces { get; }
    public IReadOnlyList<PageSegment> OmittedBands { get; }
    public int NeighborhoodRadius { get; }
    public long OmittedPixels => OmittedBands.Sum(b => (long)b.Length * DisplayMap.CanvasSize.Width);
    public long PreservedWhitePixels => Pieces.Where(p => p.Kind == RowBandKind.WhiteSpace)
        .Sum(p => (long)p.Length * ContentMap.CanvasSize.Width);

    private RowComparisonSurface(PageMap display, RowContentPiece[] pieces, PageSegment[] omitted, int radius)
    {
        DisplayMap = display;
        Pieces = Array.AsReadOnly(pieces);
        OmittedBands = Array.AsReadOnly(omitted);
        NeighborhoodRadius = radius;
        ContentMap = new(display.SizeA, display.SizeB,
            new(display.CanvasSize.Width, pieces.Sum(p => p.Length)),
            pieces.Select(p => new PageSegment(p.ContentStart, p.Length, p.AStart, p.BStart)));
    }

    /// <summary>
    /// 表示写像は、補正後の両入力の全行をそれぞれ一度ずつ覆う必要がある。
    /// 横方向の補正は入力で済ませる。元 PDF への対応は全体補正の PageMap と合成する。
    /// 支持済みの構造帯以外を C から削除する経路はない。
    /// </summary>
    public static RowSurfaceBuild Create(Mat a, Mat b, PageMap display,
        IReadOnlyList<RowBandKind> kinds, ComparisonParameters parameters)
    {
        if (a.Empty() || b.Empty() || a.Dims != 2 || b.Dims != 2 || a.Type() != MatType.CV_8UC3 || b.Type() != MatType.CV_8UC3
            || a.Size() != display.SizeA || b.Size() != display.SizeB)
            throw new ArgumentException("内容比較面には写像と同じ寸法の BGR 8bit 入力が必要です。");
        if (display.Dx != 0 || a.Width != b.Width || display.CanvasSize.Width != a.Width)
            return new(null, "invalid_display_geometry");
        if (kinds.Count != display.Segments.Count) return new(null, "invalid_band_roles");
        if (!CanRepresent(display.SizeA) || !CanRepresent(display.SizeB) || !CanRepresent(display.CanvasSize))
            return new(null, "resource_limit");
        var nextA = 0; var nextB = 0; var contentY = 0;
        var removedA = false; var removedB = false; var whiteA = false; var whiteB = false;
        var pieces = new List<RowContentPiece>(); var omitted = new List<PageSegment>();
        for (var i = 0; i < display.Segments.Count; i++)
        {
            var band = display.Segments[i]; var kind = kinds[i];
            if (!Cover(band.AStart, band.Length, a.Height, ref nextA)
                || !Cover(band.BStart, band.Length, b.Height, ref nextB)) return new(null, "incomplete_source_coverage");
            var paired = band.AStart is not null && band.BStart is not null;
            if (paired != (kind == RowBandKind.Paired) || !Enum.IsDefined(kind)) return new(null, "invalid_band_roles");
            if (paired)
            {
                if (removedA && removedB) return new(null, "unanchored_join");
                if (whiteA && whiteB) return new(null, "unresolved_white_correspondence");
                removedA = removedB = whiteA = whiteB = false;
            }
            else
            {
                var isA = band.AStart is not null;
                var white = PureWhite(isA ? a : b, (isA ? band.AStart : band.BStart)!.Value, band.Length);
                if (kind == RowBandKind.WhiteSpace)
                {
                    if (!white) return new(null, "nonwhite_space");
                    if (isA) whiteA = true; else whiteB = true;
                }
                else
                {
                    if (white) return new(null, "white_structural_band");
                    if (isA) removedA = true; else removedB = true;
                    omitted.Add(band);
                    continue;
                }
            }
            pieces.Add(new(contentY, band.CanvasStart, band.Length, band.AStart, band.BStart, kind));
            contentY = checked(contentY + band.Length);
        }
        if (nextA != a.Height || nextB != b.Height) return new(null, "incomplete_source_coverage");
        if (removedA && removedB) return new(null, "unanchored_join");
        if (whiteA && whiteB) return new(null, "unresolved_white_correspondence");
        if (!pieces.Any(p => p.Kind == RowBandKind.Paired)) return new(null, "no_common_bands");
        var radius = RequiredRadius(parameters);
        var surface = new RowComparisonSurface(display, pieces.ToArray(), omitted.ToArray(), radius);
        return surface.HasContinuousNeighborhoods() ? new(surface, null) : new(null, "unsupported_common_neighborhood");
    }

    // BGR 画像と現在の Core の配列添字が表現可能かを割当前に検査する。RSS の保証ではない。
    internal static bool CanRepresent(Size size) => size.Width > 0 && size.Height > 0 && (long)size.Width * size.Height <= int.MaxValue / 3;

    internal static int RequiredRadius(ComparisonParameters parameters)
    {
        if (parameters.Dpi is < 72 or > 1200) throw new ArgumentException("dpi: 72〜1200 にしてください。");
        var ink = parameters.Ink.BackgroundRadiusMm;
        if (!double.IsFinite(ink) || ink is <= 0 or > 20)
            throw new ArgumentException("ink.background_radius_mm: 0 より大きく 20 以下にしてください。");
        var radius = Math.Max(1, Units.RoundPixels(ink, parameters.Dpi));
        foreach (var diff in parameters.Regions.Where(r => r.Mode == "compare").Select(r => r.Diff).Append(parameters.Diff))
        {
            var shift = Math.Round(Units.MmToPixels(diff.MaxShiftMm, parameters.Dpi));
            if (!double.IsFinite(shift) || diff.MaxShiftMm < 0 || shift > (Math.Sqrt(int.MaxValue) - 1) / 2)
                throw new ArgumentException("diff.max_shift_mm: 探索候補数を整数で表現できる有限の非負値にしてください。");
            radius = Math.Max(radius, checked(2 + (int)shift));
        }
        return radius;
    }

    private bool HasContinuousNeighborhoods()
    {
        // 片側ごとに実在する連続区間を伸ばす。行ごとの配列や半径に比例する走査を作らない。
        var a = ContinuousRuns(PageSpace.A); var b = ContinuousRuns(PageSpace.B);
        var ai = 0; var bi = 0; var height = ContentMap.CanvasSize.Height;
        for (var y = 0; y < height; y++)
        {
            var left = Math.Max(0, y - NeighborhoodRadius);
            var right = (int)Math.Min(height, (long)y + NeighborhoodRadius + 1);
            while (ai < a.Count && a[ai].End <= left) ai++;
            while (bi < b.Count && b[bi].End <= left) bi++;
            if (!Supports(a, ai, left, right, ContentMap.SizeA.Height)
                && !Supports(b, bi, left, right, ContentMap.SizeB.Height)) return false;
        }
        return true;

        bool Supports(List<SourceRun> runs, int index, int left, int right, int sourceHeight) => index < runs.Count
            && runs[index] is var r && r.Start <= left && r.End >= right
            && (left != 0 || r.SourceStart == 0)
            && (right != height || (long)r.SourceStart + r.End - r.Start == sourceHeight);
    }

    private sealed record SourceRun(int Start, int End, int SourceStart);
    private List<SourceRun> ContinuousRuns(PageSpace side)
    {
        var result = new List<SourceRun>();
        foreach (var piece in Pieces)
        {
            var start = side == PageSpace.A ? piece.AStart : piece.BStart;
            if (start is not int source) continue;
            if (result.Count > 0 && result[^1] is var last && last.End == piece.ContentStart
                && (long)last.SourceStart + last.End - last.Start == source)
                result[^1] = last with { End = checked(piece.ContentStart + piece.Length) };
            else result.Add(new(piece.ContentStart, checked(piece.ContentStart + piece.Length), source));
        }
        return result;
    }

    /// <summary>C の値を D の対応行へ写す。間の構造帯は 0。型は維持し、補間・再クラスタ化はしない。</summary>
    public Mat ToDisplay(Mat content)
    {
        if (content.Empty() || content.Dims != 2 || content.Size() != ContentMap.CanvasSize)
            throw new ArgumentException("内容比較面と同じ寸法を指定してください。");
        var output = new Mat(DisplayMap.CanvasSize, content.Type(), Scalar.All(0));
        try
        {
            foreach (var piece in Pieces)
            {
                using var from = new Mat(content, new Rect(0, piece.ContentStart, content.Width, piece.Length));
                using var to = new Mat(output, new Rect(0, piece.DisplayStart, content.Width, piece.Length));
                from.CopyTo(to);
            }
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    /// <summary>D で確定した領域所有・除外を C へ転写する。矩形の優先順位を計算し直さない。</summary>
    public Mat ToContent(Mat display)
    {
        if (display.Empty() || display.Dims != 2 || display.Size() != DisplayMap.CanvasSize)
            throw new ArgumentException("表示面と同じ寸法を指定してください。");
        var output = new Mat(ContentMap.CanvasSize, display.Type());
        try
        {
            foreach (var piece in Pieces)
            {
                using var from = new Mat(display, new Rect(0, piece.DisplayStart, display.Width, piece.Length));
                using var to = new Mat(output, new Rect(0, piece.ContentStart, display.Width, piece.Length));
                from.CopyTo(to);
            }
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    /// <summary>
    /// 呼び出し元で一つのクラスタに限定した生差分を帯別の実画素矩形にする。
    /// 外接矩形内の別クラスタを含む全ページマスクを渡さない。ここでは再クラスタ化しない。
    /// globalMap 指定時の元座標は、全体補正前の画像へ直接戻す。
    /// </summary>
    public IReadOnlyList<RowMaskFragment> Fragments(Mat clusterRawMask, PageMap? globalMap = null)
    {
        if (clusterRawMask.Empty() || clusterRawMask.Type() != MatType.CV_8UC1 || clusterRawMask.Size() != ContentMap.CanvasSize)
            throw new ArgumentException("クラスタの生差分は内容比較面と同じ寸法の 8bit マスクにしてください。");
        if (globalMap is not null && (globalMap.CanvasSize != DisplayMap.SizeA || globalMap.CanvasSize != DisplayMap.SizeB))
            throw new ArgumentException("全体補正のキャンバスと行整列の入力寸法が一致していません。");
        var fragments = new List<RowMaskFragment>();
        foreach (var piece in Pieces)
        {
            using var band = new Mat(clusterRawMask, new Rect(0, piece.ContentStart, clusterRawMask.Width, piece.Length));
            var pixels = Cv2.CountNonZero(band);
            if (pixels == 0) continue;
            var local = Cv2.BoundingRect(band);
            var content = new Rect(local.X, local.Y + piece.ContentStart, local.Width, local.Height);
            var display = new Rect(local.X, local.Y + piece.DisplayStart, local.Width, local.Height);
            var bounds = new PageBounds(content.Left, content.Top, content.Right, content.Bottom);
            var a = ContentMap.MapBounds(bounds, PageSpace.Canvas, PageSpace.A);
            var b = ContentMap.MapBounds(bounds, PageSpace.Canvas, PageSpace.B);
            if (globalMap is not null)
            {
                a = a is { } aa ? globalMap.MapBounds(aa, PageSpace.Canvas, PageSpace.A) : null;
                b = b is { } bb ? globalMap.MapBounds(bb, PageSpace.Canvas, PageSpace.B) : null;
            }
            fragments.Add(new(content, display, a, b, pixels));
        }
        return fragments.AsReadOnly();
    }

    private static bool Cover(int? start, int length, int height, ref int next)
    {
        if (start is not int value) return true;
        if (value != next || (long)value + length > height) return false;
        next += length;
        return true;
    }

    private static bool PureWhite(Mat source, int start, int length)
    {
        using var band = new Mat(source, new Rect(0, start, source.Width, length));
        // 全チャンネルが 255 の場合だけ認める。濃淡・インクの閾値は使わない。
        using var channels = band.Reshape(1);
        Cv2.MinMaxLoc(channels, out double minimum, out double _);
        return minimum == 255;
    }
}
