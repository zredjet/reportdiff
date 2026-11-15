using OpenCvSharp;

namespace ReportDiff.Core;

internal sealed record RowGroupValidation(IReadOnlyList<RowShiftGroup>? Groups, string Reason, string? Detail = null);

/// <summary>行整列と送りで共用する、移動区間ごとの画素再探索・支持面積・一意性の検証。</summary>
internal static class RowGroupValidator
{
    internal static RowGroupValidation Validate(Mat a, Mat b, IReadOnlyList<RowLine> linesA, IReadOnlyList<RowLine> linesB,
        IReadOnlyList<RowMatch> matches, ComparisonParameters parameters, RowOptions options)
    {
        if (matches.Count < options.MinSupportBands) return new(null, "insufficient_support");
        var width = a.Width;
        var refine = Units.RoundPixels(options.RefineMm, parameters.Dpi);
        var maxShift = Units.RoundPixels(options.MaxShiftMm, parameters.Dpi);
        var pending = new List<List<RowMatch>>();
        foreach (var match in matches)
        {
            var dy = linesA[match.A].Baseline - linesB[match.B].Baseline;
            var previous = pending.Count == 0 ? null : pending[^1];
            if (previous is null || Math.Abs(dy - (linesA[previous[0].A].Baseline - linesB[previous[0].B].Baseline)) > 2 * refine)
            { previous = []; pending.Add(previous); }
            previous.Add(match);
        }
        if (pending.Count > options.MaxSegments) return new(null, "too_many_segments");
        using var grayA = new Mat(); using var grayB = new Mat();
        Cv2.CvtColor(a, grayA, ColorConversionCodes.BGR2GRAY); Cv2.CvtColor(b, grayB, ColorConversionCodes.BGR2GRAY);
        var pixelsA = MatBuffers.Bytes(grayA); var pixelsB = MatBuffers.Bytes(grayB);
        var excluded = RowExclusions.Mask(a.Size(), parameters, refine);
        var groups = new List<RowShiftGroup>();
        foreach (var group in pending)
        {
            if (group.Count < options.MinSupportBands) return new(null, "insufficient_support", "support_bands");
            var deltas = group.Select(m => linesA[m.A].Baseline - linesB[m.B].Baseline).Order().ToArray();
            var center = (int)Math.Round((deltas[(deltas.Length - 1) / 2] + deltas[deltas.Length / 2]) / 2);
            var min = Math.Max(-maxShift, center - refine); var max = Math.Min(maxShift, center + refine);
            if (min > max) return new(null, "insufficient_support");
            var seenA = new bool[pixelsA.Length]; var seenB = new bool[pixelsB.Length];
            var maskA = new List<int>(); var maskB = new List<int>();
            var supportA = new List<int[]>(); var supportB = new List<int[]>();
            foreach (var match in group)
            {
                var rowA = Band(linesA[match.A], a.Height, b.Height, -max, -min, false);
                var rowB = Band(linesB[match.B], b.Height, a.Height, min, max, true);
                if (rowA is null || rowB is null) return new(null, "insufficient_support", "support_outside_page");
                supportA.Add(rowA); supportB.Add(rowB);
                foreach (var p in rowA) if (!seenA[p]) { seenA[p] = true; maskA.Add(p); }
                foreach (var p in rowB) if (!seenB[p]) { seenB[p] = true; maskB.Add(p); }
            }
            if (maskA.Count == 0 || maskB.Count == 0) return new(null, "insufficient_support");
            var maskAArray = maskA.ToArray(); var maskBArray = maskB.ToArray();
            var scores = new List<(int Dy, double Score)>();
            for (var dy = min; dy <= max; dy++)
            {
                long difference = 0; long sum = 0;
                Accumulate(maskAArray, pixelsA, pixelsB, -dy);
                Accumulate(maskBArray, pixelsB, pixelsA, dy);
                scores.Add((dy, sum == 0 ? 0 : 1 - difference / (double)sum));
                void Accumulate(int[] mask, byte[] source, byte[] target, int offset)
                {
                    var shiftOffset = offset * width;
                    foreach (var pixel in mask)
                    {
                        var da = 255 - source[pixel]; var db = 255 - target[pixel + shiftOffset];
                        difference += Math.Abs(da - db); sum += da + db;
                    }
                }
            }
            int[]? Band(RowLine row, int sourceHeight, int targetHeight, int firstOffset, int lastOffset, bool isB)
            {
                var top = (int)Math.Floor(row.Bounds.Top); var bottom = (int)Math.Ceiling(row.Bounds.Bottom);
                if (top < 0 || bottom > sourceHeight || top + firstOffset < 0 || bottom + lastOffset > targetHeight) return null;
                var band = new List<int>();
                for (var y = top; y < bottom; y++)
                for (var x = 0; x < width; x++)
                {
                    var pixel = y * width + x;
                    // 元の支持画素をA/B両側で固定。BからAの除外へ入る点は全dyで避ける。
                    if (!isB && excluded[pixel]) continue;
                    if (isB)
                    {
                        var blocked = false;
                        for (var offset = firstOffset; offset <= lastOffset; offset++)
                            if (excluded[pixel + offset * width]) { blocked = true; break; }
                        if (blocked) continue;
                    }
                    band.Add(pixel);
                }
                return band.ToArray();
            }
            var ordered = scores.OrderByDescending(s => s.Score).ThenBy(s => Math.Abs(s.Dy - center)).ThenBy(s => Math.Abs(s.Dy)).ToArray();
            var best = ordered[0]; double? gap = ordered.Length == 1 ? null : best.Score - ordered[1].Score;
            if (gap < options.MinScoreGap) return new(null, "ambiguous", "pixel_refinement");
            var required = Units.SquareMmToPixels(options.MinSupportInkMm2, parameters.Dpi) * 255;
            var support = 0;
            for (var i = 0; i < group.Count; i++)
            {
                if (SumInk(supportA[i], pixelsA) >= required && SumInk(supportB[i], pixelsB) >= required)
                    support++;
            }
            if (support < options.MinSupportBands) return new(null, "insufficient_support", "support_ink");
            groups.Add(new(best.Dy, group.AsReadOnly(), best.Score, gap, support));
        }
        return new(groups.AsReadOnly(), "candidate");

        static long SumInk(int[] indices, byte[] pixels)
        {
            long sum = 0;
            foreach (var p in indices) sum += 255 - pixels[p];
            return sum;
        }
    }
}
