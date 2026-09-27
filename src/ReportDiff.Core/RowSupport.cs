using OpenCvSharp;

namespace ReportDiff.Core;

internal sealed record RowSupportScore(double Score, IReadOnlyList<int> BandsPerSegment);

/// <summary>競合する全仮説で、両側の元支持行と評価マスクを固定する。</summary>
internal static class RowSupport
{
    internal static IReadOnlyList<RowSupportScore>? Score(Mat a, Mat b, IReadOnlyList<RowLine> linesA, IReadOnlyList<RowLine> linesB,
        IReadOnlyList<CanonicalRowCandidate> candidates, ComparisonParameters parameters, RowOptions options, RowMatchingResult matching)
        => ScoreMaps(a, b, linesA, linesB, candidates.Select(c => c.Layout.DisplayMap).ToArray(), parameters, options, matching);

    internal static IReadOnlyList<RowSupportScore>? ScoreMaps(Mat a, Mat b, IReadOnlyList<RowLine> linesA, IReadOnlyList<RowLine> linesB,
        IReadOnlyList<PageMap> candidates, ComparisonParameters parameters, RowOptions options, RowMatchingResult matching)
    {
        using var ga = new Mat(); using var gb = new Mat();
        Cv2.CvtColor(a, ga, ColorConversionCodes.BGR2GRAY); Cv2.CvtColor(b, gb, ColorConversionCodes.BGR2GRAY);
        var da = MatBuffers.Bytes(ga); var db = MatBuffers.Bytes(gb); var width = a.Width;
        var bgrA = MatBuffers.Bytes(a); var bgrB = MatBuffers.Bytes(b);
        var pairings = candidates.Select(c => Pairs(c, linesA, linesB, options, parameters.Dpi, matching)).ToArray();
        var commonA = Intersect(pairings.Select(ps => ps.Select(p => p.A)));
        var commonB = Intersect(pairings.Select(ps => ps.Select(p => p.B)));
        if (commonA.Count == 0 || commonB.Count == 0) return null;
        var ay = Rows(linesA, commonA, a.Height); var by = Rows(linesB, commonB, b.Height);
        var mapsA = candidates.Select(c => Correspondence(c, true)).ToArray();
        var mapsB = candidates.Select(c => Correspondence(c, false)).ToArray();
        // 対応が全仮説で同じ帯は、支持の検証には残すが順位の根拠にならない。
        // 共通の固定範囲から、いずれかの仮説で対応が異なる帯を両側対称に全て選ぶ。
        var scoreA = candidates.Count == 1 ? ay : DistinguishingRows(ay, mapsA);
        var scoreB = candidates.Count == 1 ? by : DistinguishingRows(by, mapsB);
        var excludedA = RowExclusions.Mask(a.Size(), parameters, Units.RoundPixels(options.RefineMm, parameters.Dpi));
        // 元Bの評価点も全候補に共通。いずれかの写像で除外へ入る点は全候補で使わない。
        var excludedB = new bool[checked(width * b.Height)];
        for (var y = 0; y < by.Length; y++)
        {
            if (!by[y]) continue;
            for (var x = 0; x < width; x++)
                excludedB[y * width + x] = mapsB.Any(map => map[y] < 0 || excludedA[map[y] * width + x]);
        }
        var minimum = Units.SquareMmToPixels(options.MinSupportInkMm2, parameters.Dpi) * 255;
        var scores = new List<RowSupportScore>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index]; var mapA = mapsA[index]; var mapB = mapsB[index];
            long distance = 0; long mass = 0;
            Accumulate(da, db, scoreA, excludedA, mapA);
            Accumulate(db, da, scoreB, excludedB, mapB);
            if (mass == 0 && candidates.Count == 1) return null;
            var supports = new List<int>();
            foreach (var segment in candidate.Segments.Where(s => s.AStart is not null && s.BStart is not null))
            {
                var count = 0; var previousAEnd = -1.0; var previousBEnd = -1.0;
                foreach (var pair in pairings[index].Where(p => commonA.Contains(p.A) && commonB.Contains(p.B)))
                {
                    var la = linesA[pair.A]; var lb = linesB[pair.B];
                    if (la.Bounds.Top < segment.AStart || la.Bounds.Bottom > segment.AStart + segment.Length
                        || lb.Bounds.Top < segment.BStart || lb.Bounds.Bottom > segment.BStart + segment.Length
                        || la.Bounds.Top < previousAEnd || lb.Bounds.Top < previousBEnd) continue;
                    var ma = Mass(da, excludedA, la.Bounds, width); var mb = Mass(db, excludedB, lb.Bounds, width);
                    if (ma < minimum || mb < minimum) continue;
                    count++; previousAEnd = la.Bounds.Bottom; previousBEnd = lb.Bounds.Bottom;
                }
                // 内容区間の固定部分にも同じ支持を要求する。純白だけの区間は支持しない。
                if (count < options.MinSupportBands && (!White(bgrA, segment.AStart!.Value, segment.Length, width)
                    || !White(bgrB, segment.BStart!.Value, segment.Length, width))) return null;
                supports.Add(count);
            }
            // 同値化できず、共通支持画素でも区別できない候補は同点として見送る。
            scores.Add(new(mass == 0 ? 0 : 1 - distance / (double)mass, supports.AsReadOnly()));

            void Accumulate(byte[] source, byte[] target, bool[] rows, bool[] excluded, int[] mapping)
            {
                for (var y = 0; y < rows.Length; y++)
                {
                    if (!rows[y] || mapping[y] < 0) continue;
                    for (var x = 0; x < width; x++)
                    {
                        var pixel = y * width + x;
                        if (excluded[pixel]) continue;
                        var aa = 255 - source[pixel]; var bb = 255 - target[mapping[y] * width + x];
                        distance += Math.Abs(aa - bb); mass += aa + bb;
                    }
                }
            }
        }
        return scores.AsReadOnly();
    }

    internal static IReadOnlyList<RowMatch> Pairs(PageMap map, IReadOnlyList<RowLine> a, IReadOnlyList<RowLine> b, RowOptions options, int dpi,
        RowMatchingResult matching)
    {
        var result = new List<RowMatch>(); var radius = Units.RoundPixels(options.RefineMm, dpi);
        for (var i = 0; i < a.Count; i++)
        {
            var line = a[i];
            var band = map.Segments.FirstOrDefault(s => s.AStart is int ay && s.BStart is not null
                && line.Bounds.Top >= ay && line.Bounds.Bottom <= ay + s.Length);
            if (band is null) continue;
            var choices = Enumerable.Range(0, b.Count).Where(j => b[j].Bounds.Top >= band.BStart
                && b[j].Bounds.Bottom <= band.BStart + band.Length
                && Math.Abs(line.Baseline - b[j].Baseline - band.Dy!.Value) <= radius + 0.5).ToArray();
            // 間隔が詰まって相手行を一つに特定できない場合は支持にしない。
            // ページ全体の800万セル予算内で計算済みの単語一致を再利用する。
            if (choices.Length != 1 || !matching.Eligible![i * matching.Columns + choices[0]]) continue;
            result.Add(new(i, choices[0]));
        }
        return result;
    }

    private static HashSet<int> Intersect(IEnumerable<IEnumerable<int>> sets)
    {
        using var items = sets.GetEnumerator();
        if (!items.MoveNext()) return [];
        var common = items.Current.ToHashSet();
        while (items.MoveNext()) common.IntersectWith(items.Current);
        return common;
    }
    private static bool[] Rows(IReadOnlyList<RowLine> lines, HashSet<int> selected, int height)
    {
        var rows = new bool[height];
        foreach (var index in selected)
        {
            var top = Math.Clamp((int)Math.Floor(lines[index].Bounds.Top), 0, height);
            var bottom = Math.Clamp((int)Math.Ceiling(lines[index].Bounds.Bottom), top, height);
            Array.Fill(rows, true, top, bottom - top);
        }
        return rows;
    }
    private static int[] Correspondence(PageMap map, bool fromA)
    {
        var result = Enumerable.Repeat(-1, fromA ? map.SizeA.Height : map.SizeB.Height).ToArray();
        foreach (var band in map.Segments.Where(s => s.AStart is not null && s.BStart is not null))
        {
            var source = (fromA ? band.AStart : band.BStart)!.Value;
            var target = (fromA ? band.BStart : band.AStart)!.Value;
            for (var y = 0; y < band.Length; y++) result[source + y] = target + y;
        }
        return result;
    }
    private static bool[] DistinguishingRows(bool[] common, int[][] maps) => common.Select((selected, y) => selected
        && maps.Skip(1).Any(map => map[y] != maps[0][y])).ToArray();
    private static long Mass(byte[] gray, bool[] excluded, PageBounds box, int width)
    {
        long sum = 0;
        for (var y = (int)Math.Floor(box.Top); y < (int)Math.Ceiling(box.Bottom); y++)
        for (var x = 0; x < width; x++)
        {
            var pixel = y * width + x;
            if (!excluded[pixel]) sum += 255 - gray[pixel];
        }
        return sum;
    }
    private static bool White(byte[] bgr, int y, int length, int width) => bgr.AsSpan(y * width * 3, length * width * 3).IndexOfAnyExcept((byte)255) < 0;
}

internal static class RowExclusions
{
    internal static IReadOnlyList<RectMm> Rectangles(ComparisonParameters parameters) => parameters.Exclude
        .Concat(parameters.Regions.Where(r => r.Mode == "exclude").Select(r => r.Bounds)).ToArray();
    internal static bool[] Mask(Size size, ComparisonParameters parameters, int radius)
    {
        var mask = new bool[checked(size.Width * size.Height)];
        foreach (var rectangle in Rectangles(parameters))
        {
            var box = PageMap.CanvasRectangle(rectangle, parameters.Dpi, size, radius);
            for (var y = box.Top; y < box.Bottom; y++) Array.Fill(mask, true, y * size.Width + box.Left, box.Width);
        }
        return mask;
    }
}
