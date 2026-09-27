using System.Globalization;
using ReportDiff.Core;

namespace ReportDiff.Report;

internal static class ExclusionSnippet
{
    public static string Create(ReportPage page, ReportCluster cluster, int dpi, double marginMm)
    {
        if (!double.IsFinite(marginMm) || marginMm is < 0 or > 20)
            throw new ArgumentException("除外 YAML の余白は 0〜20 の有限の mm 値にしてください。", nameof(marginMm));
        if (page.RowAlignment.Status == "applied" && cluster.DisplayPartsPx is { } parts)
        {
            var size = page.RowAlignment.AlignedSizePx!;
            var map = new PageMap(new(size.W, size.H), new(size.W, size.H), new(page.SizePx.W, page.SizePx.H),
                page.RowAlignment.Segments.Select(s => new PageSegment(s.CanvasStart, s.Length, s.AStart, s.BStart)));
            var candidates = new List<RectMm>();
            foreach (var part in parts)
            {
                var mm = PageMap.CanvasMillimeters(new(part.X, part.Y, part.W, part.H), dpi);
                var expanded = PageMap.ExclusionCandidate(mm, map.CanvasSize, dpi, marginMm);
                var px = PageMap.ContinuousPixels(expanded, dpi);
                foreach (var source in map.MapBoundsParts(new(px.Left, px.Top, px.Right, px.Bottom), PageSpace.Canvas, PageSpace.A))
                {
                    var sourceMm = new RectMm(Units.PixelsToMm(source.Left, dpi), Units.PixelsToMm(source.Top, dpi),
                        Units.PixelsToMm(source.Right - source.Left, dpi), Units.PixelsToMm(source.Bottom - source.Top, dpi));
                    var sourceCandidate = PageMap.ExclusionCandidate(sourceMm, map.SizeA, dpi, 0);
                    if (sourceCandidate.W > 0 && sourceCandidate.H > 0) candidates.Add(sourceCandidate);
                }
            }
            return candidates.Count == 0 ? "# 設定用Aに対応する範囲がありません。"
                : string.Join('\n', candidates.Distinct().OrderBy(r => r.Y).ThenBy(r => r.X).Select(Line));
        }
        var box = cluster.BboxMm;
        var candidate = PageMap.ExclusionCandidate(new(box.X, box.Y, box.W, box.H), new(page.SizePx.W, page.SizePx.H), dpi, marginMm);
        return Line(candidate);
        string Line(RectMm r) => FormattableString.Invariant($"- {{page: {page.Page}, x: {Number(r.X)}, y: {Number(r.Y)}, w: {Number(r.W)}, h: {Number(r.H)}, note: \"\"}}");
    }

    private static string Number(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
}
