using OpenCvSharp;
using ReportDiff.Core;

internal static class SurfaceProbe
{
    // 自動推定ではなく、合成入力の独立した正解IDから与える写像。採用結果には使わない。
    internal static object Run(FlowCase test, int page, Mat a, Mat b, string folder)
    {
        var oldIds = Enumerable.Range(0, test.Rows).ToArray();
        var newIds = oldIds.ToList(); newIds.Insert(FlowFixture.Insertion, -1);
        var idsA = oldIds.Chunk(FlowFixture.Capacity).ElementAt(page - 1);
        var idsB = newIds.Chunk(FlowFixture.Capacity).ElementAt(page - 1);
        var top = Units.RoundPixels(FlowFixture.Top * 25.4 / 72, 300);
        var step = Units.RoundPixels(FlowFixture.Step * 25.4 / 72, 300);
        var segments = new List<PageSegment>(); var kinds = new List<RowBandKind>();
        var ay = 0; var by = 0; var dy = 0;
        Add(top, true, true, RowBandKind.Paired);
        foreach (var (id, indexA) in idsA.Select((id, i) => (id, i)))
        {
            var indexB = Array.IndexOf(idsB, id); if (indexB < 0) continue;
            var aStart = top + indexA * step; var bStart = top + indexB * step;
            Add(aStart - ay, true, false, RowBandKind.Structural);
            Add(bStart - by, false, true, RowBandKind.Structural);
            Add(step, true, true, RowBandKind.Paired);
        }
        Add(top + idsA.Length * step - ay, true, false, RowBandKind.Structural);
        Add(top + idsB.Length * step - by, false, true, RowBandKind.Structural);
        var end = Math.Max(ay, by);
        Add(end - ay, true, false, RowBandKind.WhiteSpace);
        Add(end - by, false, true, RowBandKind.WhiteSpace);
        Add(a.Height - end, true, true, RowBandKind.Paired);
        var map = new PageMap(a.Size(), b.Size(), new(a.Width, dy), segments);
        var parameters = new ComparisonParameters();
        var build = RowComparisonSurface.Create(a, b, map, kinds, parameters);
        if (!build.Success) return new { status = "rejected", build.Detail, segments, kinds };
        var surface = build.Surface!;
        using var ca = surface.ContentMap.Render(a, PageSpace.A); using var cb = surface.ContentMap.Render(b, PageSpace.B);
        using var comparison = PageComparer.Compare(ca, cb, parameters);
        File.WriteAllBytes(Path.Combine(folder, $"p{page}-oracle-c-a.png"), ca.ImEncode(".png"));
        File.WriteAllBytes(Path.Combine(folder, $"p{page}-oracle-c-b.png"), cb.ImEncode(".png"));
        File.WriteAllBytes(Path.Combine(folder, $"p{page}-oracle-c-raw.png"), comparison.RawMask.ImEncode(".png"));
        return new
        {
            status = "built",
            oracle_only = true,
            comparison.RawPixels,
            clusters = comparison.Clusters.Count,
            content_identical = Cv2.Norm(ca, cb, NormTypes.INF) == 0,
            segments,
            kinds
        };
        void Add(int length, bool hasA, bool hasB, RowBandKind kind)
        {
            if (length < 0) throw new InvalidOperationException("正解写像の順序が逆です。");
            if (length == 0) return;
            segments.Add(new(dy, length, hasA ? ay : null, hasB ? by : null)); kinds.Add(kind);
            if (hasA) ay += length; if (hasB) by += length; dy += length;
        }
    }
}
