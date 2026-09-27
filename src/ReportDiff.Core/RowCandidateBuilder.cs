using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ReportDiff.Core;

internal sealed record RowShiftGroup(int Dy, IReadOnlyList<RowMatch> Matches, double Score, double? Gap, int SupportBands);
internal sealed record RowLayoutCandidate(PageMap DisplayMap, IReadOnlyList<RowBandKind> Kinds,
    IReadOnlyList<RowShiftGroup> Groups, RowComparisonSurface Surface);
internal sealed record RowCandidateBuild(RowLayoutCandidate? Candidate, string Reason, string? Detail = null);

/// <summary>行 LCS の一つの対応について、画素の再探索・独立した支持・切断の可否を検証する。</summary>
internal static class RowCandidateBuilder
{
    public static RowCandidateBuild Build(Mat a, Mat b, IReadOnlyList<RowLine> linesA, IReadOnlyList<RowLine> linesB,
        IReadOnlyList<RowMatch> matches, ComparisonParameters parameters, RowOptions options,
        IReadOnlyList<RowLine>? cutLinesA = null, IReadOnlyList<RowLine>? cutLinesB = null)
    {
        var validation = RowGroupValidator.Validate(a, b, linesA, linesB, matches, parameters, options);
        if (validation.Groups is null) return new(null, validation.Reason, validation.Detail);
        var groups = validation.Groups;
        var width = a.Width;
        var exclusions = RowExclusions.Rectangles(parameters).Select(r => PageMap.CanvasRectangle(r, parameters.Dpi, a.Size())).ToArray();
        byte[]? bgrA = null, bgrB = null;
        if (groups[0].Dy != 0) return new(null, "insufficient_support", "missing_initial_anchor");
        if (groups.All(g => g.Dy == 0)) return new(null, "low_improvement", "no_shift");
        var bands = new List<PageSegment>(); var kinds = new List<RowBandKind>();
        var nextA = 0; var nextB = 0; var displayY = 0;
        for (var index = 1; index < groups.Count; index++)
        {
            var before = groups[index - 1]; var after = groups[index];
            var delta = after.Dy - before.Dy;
            if (delta == 0) continue;
            // 余分な帯のない側で境界を選び、前後双方の平行移動で相手の切断位置を検証する。
            var left = before.Matches[^1]; var right = after.Matches[0];
            var sourceLines = delta < 0 ? linesA : linesB;
            var previousLine = sourceLines[delta < 0 ? left.A : left.B];
            var nextLine = sourceLines[delta < 0 ? right.A : right.B];
            var lower = (int)Math.Ceiling(previousLine.Bounds.Bottom) + 1;
            var upper = (int)Math.Floor(nextLine.Bounds.Top);
            var preferred = (lower + upper) / 2;
            var cut = -1;
            foreach (var value in Enumerable.Range(lower, Math.Max(0, upper - lower + 1)).OrderBy(y => Math.Abs(y - preferred)).ThenBy(y => y))
            {
                var aa = delta < 0 ? value : value + before.Dy;
                var bb = delta < 0 ? value - before.Dy : value;
                var endA = aa + Math.Max(0, delta); var endB = bb + Math.Max(0, -delta);
                if (aa < nextA || bb < nextB || aa - nextA != bb - nextB || endA >= a.Height || endB >= b.Height) continue;
                if (!SafeCut(a, cutLinesA ?? linesA, aa) || !SafeCut(a, cutLinesA ?? linesA, endA)
                    || !SafeCut(b, cutLinesB ?? linesB, bb) || !SafeCut(b, cutLinesB ?? linesB, endB)) continue;
                var extra = delta < 0 ? b : a;
                var extraStart = delta < 0 ? bb : aa;
                var extraLines = delta < 0 ? linesB : linesA;
                var pairedLines = matches.Select(m => delta < 0 ? m.B : m.A);
                // 文字を含まない非白の切れ端は構造変化の根拠にならない。
                // 固定フッター前の純白余白と、直前の縦罫線の切れ端を混同しない。
                if (!IsWhite(extra, extraStart, Math.Abs(delta))
                    && !HasUnmatched(extraLines, pairedLines, extraStart, extraStart + Math.Abs(delta))
                    && !FullyExcluded(delta, aa, bb)) continue;
                if (cut < 0) { cut = value; continue; }
                // 同じ行ずれでも、切断位置によって残る画素が変わるなら別仮説。
                // 支持点とスコアは同じなので一意ではない。罫線上の濃淡変更を挿入帯へ隠さない。
                var first = Math.Min(cut, value); var length = Math.Abs(cut - value);
                var changing = delta < 0 ? b : a;
                var start1 = delta < 0 ? first - before.Dy : first + before.Dy;
                var start2 = delta < 0 ? first - after.Dy : first + after.Dy;
                using var one = new Mat(changing, new Rect(0, start1, width, length));
                using var other = new Mat(changing, new Rect(0, start2, width, length));
                if (Cv2.Norm(one, other, NormTypes.INF) != 0) return new(null, "ambiguous", "non_equivalent_safe_cuts");
            }
            if (cut < 0) return new(null, "no_bands", "unsafe_cut");
            var startA = delta < 0 ? cut : cut + before.Dy;
            var startB = delta < 0 ? cut - before.Dy : cut;
            Add(startA - nextA, nextA, nextB, RowBandKind.Paired);
            if (delta < 0)
            {
                var kind = IsWhite(b, startB, -delta) ? RowBandKind.WhiteSpace : RowBandKind.Structural;
                if (kind == RowBandKind.Structural && !HasUnmatched(linesB, matches.Select(m => m.B), startB, startB - delta)
                    && !FullyExcluded(delta, startA, startB))
                    return new(null, "insufficient_support", "unexplained_band");
                Add(-delta, null, startB, kind); nextA = startA; nextB = startB - delta;
            }
            else
            {
                var kind = IsWhite(a, startA, delta) ? RowBandKind.WhiteSpace : RowBandKind.Structural;
                if (kind == RowBandKind.Structural && !HasUnmatched(linesA, matches.Select(m => m.A), startA, startA + delta)
                    && !FullyExcluded(delta, startA, startB))
                    return new(null, "insufficient_support", "unexplained_band");
                Add(delta, startA, null, kind); nextA = startA + delta; nextB = startB;
            }
        }
        var paired = Math.Min(a.Height - nextA, b.Height - nextB);
        Add(paired, nextA, nextB, RowBandKind.Paired); nextA += paired; nextB += paired;
        if (nextA < a.Height)
        {
            if (!IsWhite(a, nextA, a.Height - nextA)) return new(null, "no_bands", "edge_content");
            Add(a.Height - nextA, nextA, null, RowBandKind.WhiteSpace);
        }
        if (nextB < b.Height)
        {
            if (!IsWhite(b, nextB, b.Height - nextB)) return new(null, "no_bands", "edge_content");
            Add(b.Height - nextB, null, nextB, RowBandKind.WhiteSpace);
        }
        var display = new PageMap(a.Size(), b.Size(), new(a.Width, displayY), bands);
        var built = RowComparisonSurface.Create(a, b, display, kinds, parameters);
        return built.Success ? new(new(display, kinds.AsReadOnly(), groups, built.Surface!), "candidate")
            : new(null, built.Detail == "resource_limit" ? "resource_limit" : "no_bands", built.Detail);

        void Add(int length, int? ay, int? by, RowBandKind kind)
        {
            if (length <= 0) return;
            bands.Add(new(displayY, length, ay, by)); kinds.Add(kind); displayY = checked(displayY + length);
        }

        bool FullyExcluded(int delta, int ay, int by)
        {
            if (exclusions.Length == 0) return false;
            // 前後の独立した支持で過不足を確認した帯に限る。除外内の文字を支持へ戻さない。
            // Bだけの帯には、Aの切断の両側をまたぐ一つの除外矩形だけが伸びる。
            var covers = delta > 0 ? exclusions : exclusions.Where(e => e.Top < ay && e.Bottom > ay).ToArray();
            if (covers.Length == 0) return false;
            var bytes = delta > 0 ? bgrA ??= MatBuffers.Bytes(a) : bgrB ??= MatBuffers.Bytes(b);
            var start = delta > 0 ? ay : by;
            for (var y = start; y < start + Math.Abs(delta); y++)
            for (var x = 0; x < width; x++)
            {
                var pixel = (y * width + x) * 3;
                if (bytes[pixel] == 255 && bytes[pixel + 1] == 255 && bytes[pixel + 2] == 255) continue;
                if (!covers.Any(e => x >= e.Left && x < e.Right && (delta < 0 || y >= e.Top && y < e.Bottom))) return false;
            }
            return true;
        }
    }

    private static bool HasUnmatched(IReadOnlyList<RowLine> lines, IEnumerable<int> matched, int start, int end)
    {
        var paired = matched.ToHashSet();
        return lines.Where((l, i) => !paired.Contains(i)).Any(l => l.Bounds.Top >= start && l.Bounds.Bottom <= end);
    }

    private static bool IsWhite(Mat source, int start, int length)
    {
        using var band = new Mat(source, new Rect(0, start, source.Width, length)); using var channels = band.Reshape(1);
        Cv2.MinMaxLoc(channels, out double minimum, out double _); return minimum == 255;
    }

    private static bool SafeCut(Mat image, IReadOnlyList<RowLine> lines, int y)
    {
        if (y <= 0 || y >= image.Height) return false;
        // 行矩形ではなく各語の矩形を守る。縦画を罫線として消すことを防ぐ。
        if (lines.SelectMany(l => l.Words).Any(w => w.Bounds.Top < y + 1 && w.Bounds.Bottom > y - 1)) return false;
        var imageWidth = image.Width;
        var bytes = new byte[checked(imageWidth * 3)]; Marshal.Copy(image.Ptr(y), bytes, 0, bytes.Length);
        for (var x = 0; x < imageWidth;)
        {
            if (bytes[x * 3] == 255 && bytes[x * 3 + 1] == 255 && bytes[x * 3 + 2] == 255) { x++; continue; }
            var first = x;
            while (x < imageWidth && (bytes[x * 3] != 255 || bytes[x * 3 + 1] != 255 || bytes[x * 3 + 2] != 255)) x++;
            var width = x - first;
            // 横罫線・塗りを切らず、上下に幅の4倍以上続く細長い画素列だけ通す。
            var margin = checked(4 * width);
            if (y < margin || (long)y + margin >= image.Height) return false;
            for (var row = y - margin; row <= y + margin; row++)
            for (var column = first; column < x; column++)
            {
                var p = image.At<Vec3b>(row, column);
                if (p.Item0 == 255 && p.Item1 == 255 && p.Item2 == 255) return false;
            }
        }
        return true;
    }
}
