using OpenCvSharp;

namespace ReportDiff.Core;

public sealed record RowWord(string Text, PageBounds Bounds, IReadOnlyList<double> Baselines);
public sealed record RowLine(IReadOnlyList<RowWord> Words, PageBounds Bounds, double Baseline);
public sealed record RowTextResult(string Status, string? Detail, IReadOnlyList<RowWord> Words);
public sealed class RowResourceLimitException(string detail) : Exception(detail);

/// <summary>注釈と行整列で共用する、文字の整理と先頭語を基準にした行の重なり。</summary>
public static class TextLineLayout
{
    public static string Clean(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(part => string.Concat(part.Where(c => !char.IsControl(c))))).Trim();
    public static bool SameLine(Rect2d a, Rect2d b, double minimumOverlap) =>
        Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) >= Math.Min(a.Height, b.Height) * minimumOverlap;

    public static IReadOnlyList<RowLine> Lines(IEnumerable<RowWord> words, double minimumOverlap)
    {
        if (!double.IsFinite(minimumOverlap) || minimumOverlap is <= 0 or > 1)
            throw new ArgumentException("text.min_line_overlap: 0 より大きく 1 以下にしてください。");
        var candidates = words.Select(w => w with { Text = Clean(w.Text) }).Where(w => w.Text.Length > 0)
            .DistinctBy(w => (w.Text, w.Bounds)).OrderBy(w => w.Bounds.Top).ThenBy(w => w.Bounds.Left)
            .ThenBy(w => w.Text, StringComparer.Ordinal).ToArray();
        var lines = new List<List<RowWord>>();
        foreach (var word in candidates)
        {
            if (!PageMap.Finite(word.Bounds) || word.Bounds.Right <= word.Bounds.Left || word.Bounds.Bottom <= word.Bounds.Top
                || word.Baselines.Count == 0 || word.Baselines.Any(v => !double.IsFinite(v)))
                throw new ArgumentException("行の単語矩形・ベースラインが不正です。");
            var line = lines.FirstOrDefault(l => SameLine(l[0].Bounds.Rectangle, word.Bounds.Rectangle, minimumOverlap));
            if (line is null)
            {
                if (lines.Count == RowMatching.MaximumRows) throw new RowResourceLimitException("row_limit");
                line = []; lines.Add(line);
            }
            line.Add(word);
        }
        return lines.Select(l =>
        {
            var baselines = l.SelectMany(w => w.Baselines).Order().ToArray();
            var middle = baselines.Length / 2;
            var median = baselines.Length % 2 == 0 ? (baselines[middle - 1] + baselines[middle]) / 2 : baselines[middle];
            return new RowLine(l.OrderBy(w => w.Bounds.Left).ThenBy(w => w.Bounds.Top).ThenBy(w => w.Text, StringComparer.Ordinal).ToArray(),
                new(l.Min(w => w.Bounds.Left), l.Min(w => w.Bounds.Top), l.Max(w => w.Bounds.Right), l.Max(w => w.Bounds.Bottom)), median);
        }).ToArray();
    }
}

public sealed record RowOptions
{
    public bool Enabled { get; init; }
    public bool CarryEnabled { get; init; }
    public double MaxShiftMm { get; init; } = 20;
    public double MinWordMatch { get; init; } = 0.60;
    public double RefineMm { get; init; } = 0.3;
    public double MinImprovement { get; init; } = 0.05;
    public double MinScoreGap { get; init; } = 0.02;
    public int MinSupportBands { get; init; } = 2;
    public double MinSupportInkMm2 { get; init; } = 1;
    public int MaxSegments { get; init; } = 8;

    public RowOptions Validated(int dpi)
    {
        if (CarryEnabled && !Enabled) throw new ArgumentException("rows.carry_enabled: true には rows.enabled: true が必要です。");
        Range(MaxShiftMm, "max_shift_mm", 0, 100, true);
        Range(MinWordMatch, "min_word_match", 0, 1, true);
        Range(RefineMm, "refine_mm", 0, 2, false);
        Range(MinImprovement, "min_improvement", 0, 1, true);
        Range(MinScoreGap, "min_score_gap", 0, 1, true);
        Range(MinSupportBands, "min_support_bands", 2, 100, false);
        Range(MinSupportInkMm2, "min_support_ink_mm2", 0, 1000, true);
        Range(MaxSegments, "max_segments", 1, 64, false);
        if (dpi is < 72 or > 1200) throw new ArgumentException("dpi: 72〜1200 にしてください。");
        _ = Units.RoundPixels(MaxShiftMm, dpi); _ = Units.RoundPixels(RefineMm, dpi);
        _ = Units.SquareMmToPixels(MinSupportInkMm2, dpi);
        return this;
    }

    private static void Range(double value, string key, double min, double max, bool positive)
    {
        if (!double.IsFinite(value) || value < min || value > max || (positive && value == min))
            throw new ArgumentException($"rows.{key}: {min} {(positive ? "より大きく" : "以上")} {max} 以下の有限値にしてください。");
    }
}
