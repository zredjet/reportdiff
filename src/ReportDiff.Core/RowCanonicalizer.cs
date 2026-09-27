using OpenCvSharp;

namespace ReportDiff.Core;

public sealed record RowEquivalentPosition(PageSpace Side, int FirstStart, int LastStart, int Step, int Length);
internal sealed record CanonicalRowCandidate(RowLayoutCandidate Layout, IReadOnlyList<RowEquivalentPosition> EquivalentPositions);

/// <summary>同じ高さ・全画素の反復帯だけを、連続範囲の先頭へ正規化する。</summary>
internal static class RowCanonicalizer
{
    internal static CanonicalRowCandidate? Normalize(Mat a, Mat b, RowLayoutCandidate candidate, ComparisonParameters parameters)
    {
        var bands = candidate.DisplayMap.Segments.ToList();
        var kinds = candidate.Kinds.ToList();
        var positions = new List<RowEquivalentPosition>();
        for (var i = 0; i < bands.Count; i++)
        {
            if (kinds[i] != RowBandKind.Structural) continue;
            var gap = bands[i]; var step = gap.Length;
            var side = gap.AStart is not null ? PageSpace.A : PageSpace.B;
            var source = side == PageSpace.A ? a : b;
            var last = (gap.AStart ?? gap.BStart)!.Value;
            // 挿入側だけでなく、対応している他方の帯も全画素一致することを要求する。
            while (i > 0 && i + 1 < bands.Count && kinds[i - 1] == RowBandKind.Paired && kinds[i + 1] == RowBandKind.Paired
                && bands[i - 1].Length >= step)
            {
                var before = bands[i - 1]; var after = bands[i + 1];
                var start = (gap.AStart ?? gap.BStart)!.Value;
                if (!Equal(source, start, a, before.AStart!.Value + before.Length - step, step)
                    || !Equal(source, start, b, before.BStart!.Value + before.Length - step, step)) break;
                gap = gap with { AStart = gap.AStart - step, BStart = gap.BStart - step };
                bands[i] = gap;
                bands[i - 1] = before with { Length = before.Length - step };
                bands[i + 1] = after with { AStart = after.AStart - step, BStart = after.BStart - step, Length = after.Length + step };
                if (bands[i - 1].Length == 0) { bands.RemoveAt(i - 1); kinds.RemoveAt(i - 1); i--; }
            }
            var first = (gap.AStart ?? gap.BStart)!.Value;
            if (i + 1 < bands.Count && kinds[i + 1] == RowBandKind.Paired)
            {
                var after = bands[i + 1];
                for (var offset = 0; offset + step <= after.Length; offset += step)
                {
                    if (!Equal(source, first, a, after.AStart!.Value + offset, step)
                        || !Equal(source, first, b, after.BStart!.Value + offset, step)) break;
                    last = Math.Max(last, first + offset + step);
                }
            }
            positions.Add(new(side, first, last, step, step));
        }
        var y = 0;
        for (var i = 0; i < bands.Count; i++) { bands[i] = bands[i] with { CanvasStart = y }; y += bands[i].Length; }
        var map = new PageMap(a.Size(), b.Size(), new(a.Width, y), bands);
        var surface = RowComparisonSurface.Create(a, b, map, kinds, parameters);
        return surface.Success ? new(candidate with { DisplayMap = map, Kinds = kinds.AsReadOnly(), Surface = surface.Surface! }, positions.AsReadOnly()) : null;
    }

    private static bool Equal(Mat a, int ay, Mat b, int by, int length)
    {
        if (ay < 0 || by < 0 || (long)ay + length > a.Height || (long)by + length > b.Height || a.Width != b.Width) return false;
        using var aa = new Mat(a, new Rect(0, ay, a.Width, length));
        using var bb = new Mat(b, new Rect(0, by, b.Width, length));
        return Cv2.Norm(aa, bb, NormTypes.INF) == 0;
    }
}
