using System.Reflection;
using OpenCvSharp;
using ReportDiff.Core;
using static AnchoredContentSurface;

/// <summary>診断用のC→元座標→D。画素IDを保持し、表示上の重複と件数の所属を分ける。</summary>
internal static class AnchoredProjection
{
    internal sealed record Display(int Page, PageMap Map, RowBandKind[] Kinds);
    internal sealed record Part(int ContentPage, int ContentId, int DisplayPage, string[] Sides,
        int[] ContentBounds, int[] DisplayBounds, int Pixels);
    internal sealed record Cluster(int OwnerPage, int ContentId, int Pixels, string? Kind, string? AnnotationReason, Part[] Parts);
    internal sealed record Projection(Cluster[] Clusters, int[][] Runs, Dictionary<int, int> DisplayPixels);
    internal static Projection SmallFragmentAudit(Canvas canvas, IReadOnlyList<Display> displays, int width, Action<int, Mat> save)
    {
        // 画素検出の正例ではなく、確定済みIDとノイズを転写する幾何学の対照。
        var height = canvas.Pieces.Sum(p => p.Length); var ids = new int[width * height]; var bytes = new byte[ids.Length];
        foreach (var (x, y) in new[] { (620, 699), (621, 699), (620, 700), (621, 700) })
        { ids[y * width + x] = 1; bytes[y * width + x] = 255; }
        var raw = new Mat(height, width, MatType.CV_8UC1); var labels = new Mat(height, width, MatType.CV_8UC1);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, labels.Data, bytes.Length); bytes[700 * width + 650] = 255;
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, raw.Data, bytes.Length);
        using var comparison = new PageComparison("different", [new(1, new(620, 699, 2, 2), 4) { Kind = "changed" }],
            5, 1, 0, 2, raw, labels, []);
        var asm = typeof(PageComparison).Assembly; var movement = asm.GetType("ReportDiff.Core.MovementProjectionProof")!;
        var dict = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(int), movement));
        var data = Activator.CreateInstance(asm.GetType("ReportDiff.Core.ClusterProjectionData")!, [new Size(width, height), ids, dict]);
        typeof(PageComparison).GetProperty("ProjectionData", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(comparison, data);
        return Project(comparison, canvas, displays, width, save);
    }
    internal static PageComparison Compare(Mat a, Mat b, ComparisonParameters parameters) => (PageComparison)typeof(PageComparer)
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Single(m => m.Name == "Compare")
        .Invoke(null, [a, b, parameters, true, null, true, true, null])!;

    internal static Projection Project(PageComparison content, Canvas canvas, IReadOnlyList<Display> displays, int width,
        Action<int, Mat> save)
    {
        var data = typeof(PageComparison).GetProperty("ProjectionData", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(content);
        var ids = data is null ? new int[content.RawMask.Height * width] : (int[])data.GetType().GetProperty("RawClusterIds")!.GetValue(data)!;
        var runs = new List<int[]>(); var contentHeight = content.RawMask.Height;
        for (var y = 0; y < contentHeight; y++)
        for (var x = 0; x < width;)
        {
            var id = ids[y * width + x]; if (id == 0) { x++; continue; }
            var end = x + 1; while (end < width && ids[y * width + end] == id) end++;
            runs.Add([y, x, end - x, id]); x = end;
        }
        var masks = displays.ToDictionary(d => d.Page, d => new byte[d.Map.CanvasSize.Width * d.Map.CanvasSize.Height]);
        var raw = new byte[content.RawMask.Height * width];
        System.Runtime.InteropServices.Marshal.Copy(content.RawMask.Data, raw, 0, raw.Length);
        // ノイズとしてクラスタ採用されなかった生差分も表示マスクには保持する。
        foreach (var piece in canvas.Pieces)
        foreach (var (o, side) in new[] { (piece.A, "A"), (piece.B, "B") })
        {
            if (o is null) continue;
            var display = displays.Single(d => d.Page == int.Parse(o.Key[1..]));
            foreach (var segment in display.Map.Segments)
            {
                var start = side == "A" ? segment.AStart : segment.BStart; if (start is null) continue;
                for (var y = Math.Max(o.Top, start.Value); y < Math.Min(o.Top + piece.Length, start.Value + segment.Length); y++)
                for (var x = 0; x < width; x++)
                    if (raw[(piece.Top + y - o.Top) * width + x] != 0)
                        masks[display.Page][(segment.CanvasStart + y - start.Value) * width + x] = 255;
            }
        }
        var all = new List<Cluster>();
        foreach (var cluster in content.Clusters)
        {
            var parts = new List<Part>(); var anchorCount = 0;
            foreach (var piece in canvas.Pieces)
            {
                // 同じD位置へ戻るA/Bを合流させる。異なるページを外接矩形でまとめない。
                var destinations = new Dictionary<(int Page, int Offset, int Top, int End), HashSet<string>>();
                foreach (var (o, side) in new[] { (piece.A, "A"), (piece.B, "B") })
                {
                    if (o is null) continue;
                    var display = displays.Single(d => d.Page == int.Parse(o.Key[1..]));
                    foreach (var segment in display.Map.Segments)
                    {
                        var start = side == "A" ? segment.AStart : segment.BStart; if (start is null) continue;
                        var lo = Math.Max(o.Top, start.Value); var hi = Math.Min(o.Top + piece.Length, start.Value + segment.Length);
                        if (lo >= hi) continue;
                        var top = piece.Top + lo - o.Top; var end = piece.Top + hi - o.Top;
                        var offset = segment.CanvasStart - start.Value + o.Top - piece.Top;
                        var key = (display.Page, offset, top, end);
                        if (!destinations.TryGetValue(key, out var sides)) destinations.Add(key, sides = []);
                        sides.Add(side);
                    }
                }
                foreach (var (key, sides) in destinations.OrderBy(p => p.Key.Page).ThenBy(p => p.Key.Top))
                {
                    var pixels = 0; var left = width; var right = -1; var top = int.MaxValue; var bottom = -1;
                    for (var y = key.Top; y < key.End; y++)
                    for (var x = 0; x < width; x++)
                    {
                        if (ids[y * width + x] != cluster.Id) continue;
                        pixels++; left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    }
                    if (pixels == 0) continue;
                    if (key.Page == canvas.Page && sides.Contains(canvas.Anchor[..1])) anchorCount += pixels;
                    parts.Add(new(canvas.Page, cluster.Id, key.Page, sides.Order().ToArray(),
                        [left, top, right + 1, bottom + 1], [left, top + key.Offset, right + 1, bottom + 1 + key.Offset], pixels));
                }
            }
            if (anchorCount != cluster.Pixels) throw new InvalidOperationException("保持側の所属画素数が変わりました。");
            // 移動の表示用変位はこの診断では証明しない。内容差分とIDを保持する。
            all.Add(new(canvas.Page, cluster.Id, cluster.Pixels, cluster.Kind == "moved" ? "changed" : cluster.Kind,
                cluster.Kind == "moved" ? "display_movement_not_proven" : null, parts.ToArray()));
        }
        foreach (var (page, bytes) in masks)
        {
            var size = displays.Single(d => d.Page == page).Map.CanvasSize;
            using var image = new Mat(size, MatType.CV_8UC1); System.Runtime.InteropServices.Marshal.Copy(bytes, 0, image.Data, bytes.Length); save(page, image);
        }
        return new(all.ToArray(), runs.ToArray(), masks.ToDictionary(k => k.Key, k => k.Value.Count(b => b != 0)));
    }

    internal static (PageFlowAggregation.Structure[] Structures, object[] Audit) Structures(Display d, IReadOnlyDictionary<string, Mat> originals,
        Omission[] causes)
    {
        var groups = new List<(string Kind, List<PageSegment> Bands)>();
        for (var i = 0; i < d.Map.Segments.Count; i++)
        {
            var s = d.Map.Segments[i]; var kind = d.Kinds[i] == RowBandKind.Structural ? s.AStart is null ? "inserted" : "deleted"
                : s.Dy is { } dy && dy != 0 ? "block_moved" : null;
            if (kind is null) continue;
            var last = groups.LastOrDefault();
            if (last.Bands is not null && last.Kind == kind && last.Bands[^1].CanvasStart + last.Bands[^1].Length == s.CanvasStart
                && (kind != "block_moved" || last.Bands[^1].Dy == s.Dy)) last.Bands.Add(s);
            else groups.Add((kind, [s]));
        }
        var structures = new List<PageFlowAggregation.Structure>(); var audit = new List<object>();
        foreach (var (kind, bands) in groups)
        {
            var a = Band(true); var b = Band(false); var role = kind == "block_moved" ? "movement" :
                causes.Any(c => c.Key == (a is null ? "B" : "A") + d.Page && c.Top == (a ?? b)!.Top && c.Length == (a ?? b)!.Height)
                ? "cause" : "carry";
            structures.Add(new(new(d.Page, structures.Count + 1), kind, a, b, kind == "block_moved" ? checked((int)-bands[0].Dy!.Value) : null, false));
            audit.Add(new { page = d.Page, id = structures.Count, role, compared_in_content = role != "cause" });
            PageFlowBand? Band(bool isA)
            {
                var present = bands.Where(s => (isA ? s.AStart : s.BStart) is not null).ToArray(); if (present.Length == 0) return null;
                var start = (isA ? present[0].AStart : present[0].BStart)!.Value;
                var height = present.Sum(s => s.Length);
                using var pixels = new Mat(originals[(isA ? "A" : "B") + d.Page], new Rect(0, start, d.Map.CanvasSize.Width, height));
                using var channels = pixels.Reshape(1); Cv2.MinMaxLoc(channels, out double min, out double _);
                if (min == 255) throw new InvalidOperationException("純白の構造を作ろうとしました。");
                return new(new(isA ? PageSpace.A : PageSpace.B, d.Page), start, height);
            }
        }
        return (structures.ToArray(), audit.ToArray());
    }
}
