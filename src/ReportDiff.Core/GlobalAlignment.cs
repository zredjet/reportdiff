using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>B に加える平行移動。右・下が正で、クラスタの A→B 移動量とは逆向き。</summary>
public sealed record GlobalShift(int Dx, int Dy);

public sealed record AlignmentResult(string Status, string Reason, GlobalShift? EstimatedShiftPx = null,
    double? ScoreBefore = null, double? ScoreAfter = null, double? ScoreGap = null,
    double? CoarseScoreGap = null, int? SupportCells = null)
{
    public static AlignmentResult Disabled { get; } = new("disabled", "disabled");
    public static AlignmentResult Skipped(string reason) => new("not_applied", reason);
}

/// <summary>整数 px の全体補正を推定する。入力を変更せず、採用後の画像作成も呼び出し側で明示する。</summary>
public static class GlobalAligner
{
    private readonly record struct Candidate(int X, int Y, double Score);
    private readonly record struct Match(double Score, double MassA, double MassB);

    public static AlignmentResult Estimate(Mat a, Mat b, ComparisonParameters parameters, AlignOptions options,
        bool sizeMismatch = false)
    {
        ValidateImage(a); ValidateImage(b);
        if (parameters.Dpi is < 72 or > 1200) throw new ArgumentException("DPI は 72〜1200 にしてください。");
        if (!double.IsFinite(options.MaxShiftMm) || options.MaxShiftMm is < 0 or > 20
            || !Probability(options.MinScore) || !Probability(options.MinScoreGap) || !Probability(options.MinImprovement)
            || options.CoarseMaxSideSamples is < 64 or > 4096 || options.RefineRadiusSamples is < 1 or > 8
            || options.MinSupportCells is < 1 or > 9 || options.MinSupportRows is < 1 or > 3
            || options.MinSupportColumns is < 1 or > 3
            || !double.IsFinite(options.MinInkAreaMm2) || options.MinInkAreaMm2 is <= 0 or > 10000)
            throw new ArgumentException("全体補正の探索距離・しきい値が不正です。");
        if (!options.Enabled) return AlignmentResult.Disabled;
        if (sizeMismatch || a.Size() != b.Size()) return AlignmentResult.Skipped("size_mismatch");
        var radius = (int)Math.Floor(Units.MmToPixels(options.MaxShiftMm, parameters.Dpi));
        if (radius == 0) return AlignmentResult.Skipped("zero_range");
        var width = a.Width; var height = a.Height;
        if (width <= 2 * radius || height <= 2 * radius) return AlignmentResult.Skipped("insufficient_area");
        var factor = 1;
        while (Math.Max(width, height) / (double)factor > options.CoarseMaxSideSamples) factor *= 2;
        using var darkA = Dark(a, factor); using var darkB = Dark(b, factor);
        using var fullMask = EvaluationMask(new(width, height), darkA.Size(), radius, 1, parameters);
        var fullRegion = new Rect(radius, radius, width - radius * 2, height - radius * 2);
        var before = Score(darkA, darkB, fullMask, fullRegion, 0, 0);
        if (Cv2.CountNonZero(fullMask) == 0) return AlignmentResult.Skipped("insufficient_area");
        if (before.MassA == 0 || before.MassB == 0) return AlignmentResult.Skipped("insufficient_information");

        Candidate best = new(0, 0, 0);
        double? coarseGap = null;
        double? gap = null;
        for (var scale = factor; scale >= 1; scale /= 2)
        {
            using var smallA = new Mat(); using var smallB = new Mat();
            var size = new Size(darkA.Width / scale, darkA.Height / scale);
            Cv2.Resize(darkA, smallA, size, 0, 0, InterpolationFlags.Area);
            Cv2.Resize(darkB, smallB, size, 0, 0, InterpolationFlags.Area);
            var r = (int)Math.Ceiling(radius / (double)scale);
            if (size.Width <= 2 * r || size.Height <= 2 * r) return AlignmentResult.Skipped("insufficient_area");
            // 粗い段階での端数切り上げ分も含め、どの候補でも同じ安全な領域を使う。
            using var mask = EvaluationMask(new(width, height), darkA.Size(), r * scale, scale, parameters);
            if (Cv2.CountNonZero(mask) == 0) return AlignmentResult.Skipped("insufficient_area");
            var region = new Rect(r, r, size.Width - 2 * r, size.Height - 2 * r);
            var minX = scale == factor ? -r : Math.Max(-r, best.X * 2 - options.RefineRadiusSamples);
            var maxX = scale == factor ? r : Math.Min(r, best.X * 2 + options.RefineRadiusSamples);
            var minY = scale == factor ? -r : Math.Max(-r, best.Y * 2 - options.RefineRadiusSamples);
            var maxY = scale == factor ? r : Math.Min(r, best.Y * 2 + options.RefineRadiusSamples);
            var candidates = new List<Candidate>();
            for (var dy = minY; dy <= maxY; dy++)
            for (var dx = minX; dx <= maxX; dx++)
                candidates.Add(new(dx, dy, Score(smallA, smallB, mask, region, dx, dy).Score));
            var ordered = candidates.OrderByDescending(c => c.Score)
                .ThenBy(c => Math.Abs(c.X) + Math.Abs(c.Y)).ThenBy(c => c.Y).ThenBy(c => c.X).ToArray();
            best = ordered[0];
            if (scale == factor)
            {
                var others = ordered.Where(c => Math.Abs(c.X - best.X) > 1 || Math.Abs(c.Y - best.Y) > 1).ToArray();
                coarseGap = others.Length == 0 ? null : best.Score - others[0].Score;
            }
            gap = ordered.Length < 2 ? null : best.Score - ordered[1].Score;
        }

        var support = new List<(int Row, int Column)>();
        var minimumMass = Units.SquareMmToPixels(options.MinInkAreaMm2, parameters.Dpi) * 255;
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        {
            var tile = Rect.Intersect(fullRegion, new Rect(column * width / 3, row * height / 3,
                (column + 1) * width / 3 - column * width / 3, (row + 1) * height / 3 - row * height / 3));
            if (tile.Width <= 0 || tile.Height <= 0) continue;
            var after = Score(darkA, darkB, fullMask, tile, best.X, best.Y);
            if (after.MassA >= minimumMass && after.MassB >= minimumMass && after.Score >= options.MinScore
                && after.Score - Score(darkA, darkB, fullMask, tile, 0, 0).Score >= options.MinImprovement)
                support.Add((row, column));
        }
        var reason = best.X == 0 && best.Y == 0 ? "no_shift"
            : best.Score < options.MinScore ? "low_score"
            : best.Score - before.Score < options.MinImprovement ? "low_improvement"
            : coarseGap < options.MinScoreGap || gap < options.MinScoreGap ? "ambiguous"
            : support.Count < options.MinSupportCells || support.Select(c => c.Row).Distinct().Count() < options.MinSupportRows
                || support.Select(c => c.Column).Distinct().Count() < options.MinSupportColumns ? "insufficient_support"
            : WouldCropContent(b, best.X, best.Y) ? "edge_content" : "applied";
        return new(reason == "applied" ? "applied" : "not_applied", reason, new(best.X, best.Y),
            before.Score, best.Score, gap, coarseGap, support.Count);
    }

    /// <summary>整数移動をコピーで適用する。再補間しない。返す Mat は呼び出し側が所有する。</summary>
    public static Mat TranslateB(Mat b, GlobalShift shift)
    {
        ValidateImage(b);
        var width = b.Width; var height = b.Height;
        if (Math.Abs((long)shift.Dx) >= width || Math.Abs((long)shift.Dy) >= height)
            throw new ArgumentException("補正量は画像サイズ未満にしてください。");
        var result = new Mat(b.Size(), MatType.CV_8UC3, Scalar.All(255));
        try
        {
            var w = width - Math.Abs(shift.Dx); var h = height - Math.Abs(shift.Dy);
            using var source = new Mat(b, new Rect(Math.Max(0, -shift.Dx), Math.Max(0, -shift.Dy), w, h));
            using var destination = new Mat(result, new Rect(Math.Max(0, shift.Dx), Math.Max(0, shift.Dy), w, h));
            source.CopyTo(destination);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static Match Score(Mat a, Mat b, Mat mask, Rect region, int dx, int dy)
    {
        using var aa = new Mat(a, region);
        using var bb = new Mat(b, new Rect(region.X - dx, region.Y - dy, region.Width, region.Height));
        using var mm = new Mat(mask, region);
        var massA = Cv2.Norm(aa, NormTypes.L1, mm); var massB = Cv2.Norm(bb, NormTypes.L1, mm);
        return new(massA + massB == 0 ? 0 : 1 - Cv2.Norm(aa, bb, NormTypes.L1, mm) / (massA + massB), massA, massB);
    }

    private static Mat EvaluationMask(Size original, Size padded, int radius, int scale, ComparisonParameters parameters)
    {
        using var allowed = new Mat(padded, MatType.CV_32FC1, Scalar.All(0));
        if (original.Width > 2 * radius && original.Height > 2 * radius)
        {
            using var interior = new Mat(allowed, new Rect(radius, radius, original.Width - 2 * radius, original.Height - 2 * radius));
            interior.SetTo(Scalar.All(1));
        }
        foreach (var e in parameters.Exclude)
        {
            if (!double.IsFinite(e.X) || !double.IsFinite(e.Y) || !double.IsFinite(e.W) || !double.IsFinite(e.H)
                || e.X < 0 || e.Y < 0 || e.W < 0 || e.H < 0) throw new ArgumentException("除外領域が不正です。");
            if (e.W == 0 || e.H == 0) continue;
            var left = (int)Math.Clamp(Math.Floor(Units.MmToPixels(e.X, parameters.Dpi)) - radius, 0, original.Width);
            var top = (int)Math.Clamp(Math.Floor(Units.MmToPixels(e.Y, parameters.Dpi)) - radius, 0, original.Height);
            var right = (int)Math.Clamp(Math.Ceiling(Units.MmToPixels(e.X + e.W, parameters.Dpi)) + radius, 0, original.Width);
            var bottom = (int)Math.Clamp(Math.Ceiling(Units.MmToPixels(e.Y + e.H, parameters.Dpi)) + radius, 0, original.Height);
            if (right <= left || bottom <= top) continue;
            using var excluded = new Mat(allowed, new Rect(left, top, right - left, bottom - top));
            excluded.SetTo(Scalar.All(0));
        }
        using var reduced = new Mat();
        Cv2.Resize(allowed, reduced, new Size(padded.Width / scale, padded.Height / scale), 0, 0, InterpolationFlags.Area);
        var mask = new Mat();
        try { Cv2.Compare(reduced, 1, mask, CmpTypes.EQ); return mask; }
        catch { mask.Dispose(); throw; }
    }

    private static Mat Dark(Mat source, int factor)
    {
        using var gray = new Mat(); using var dark = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY); Cv2.BitwiseNot(gray, dark);
        var padded = new Mat();
        try
        {
            Cv2.CopyMakeBorder(dark, padded, 0, (factor - source.Height % factor) % factor,
                0, (factor - source.Width % factor) % factor, BorderTypes.Constant | BorderTypes.Isolated, Scalar.All(0));
            return padded;
        }
        catch { padded.Dispose(); throw; }
    }

    private static bool WouldCropContent(Mat b, int dx, int dy)
    {
        var bands = new List<Rect>();
        if (dx < 0) bands.Add(new(0, 0, -dx, b.Height));
        if (dx > 0) bands.Add(new(b.Width - dx, 0, dx, b.Height));
        if (dy < 0) bands.Add(new(0, 0, b.Width, -dy));
        if (dy > 0) bands.Add(new(0, b.Height - dy, b.Width, dy));
        foreach (var band in bands)
        {
            using var roi = new Mat(b, band);
            if (Cv2.Norm(roi, NormTypes.L1) != 255.0 * roi.Total() * 3) return true;
        }
        return false;
    }

    private static bool Probability(double value) => double.IsFinite(value) && value is > 0 and <= 1;
    private static void ValidateImage(Mat image)
    {
        if (image.IsDisposed || image.Empty() || image.Dims != 2 || image.Type() != MatType.CV_8UC3)
            throw new ArgumentException("全体補正には空でない BGR 8bit 画像を指定してください。");
    }
}
