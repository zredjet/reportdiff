using OpenCvSharp;

/// <summary>診断専用。片側の物理ページ全体を保持する。製品の2画像用PageMapとは区別する。</summary>
internal static class AnchoredContentSurface
{
    internal sealed record Origin(string Key, int Top);
    internal sealed record Piece(int Top, int Length, Origin? A, Origin? B);
    internal sealed record Canvas(int Page, string Anchor, Piece[] Pieces);
    internal sealed record Omission(string Key, int Top, int Length);

    // 意味上の行対応と原因の証拠は呼出側の別検証。ここでは物理的な被覆・連続性・白を調べる。
    internal static string? Validate(IReadOnlyDictionary<string, Mat> originals, Canvas[] canvases, Omission[] omitted)
    {
        if (!originals.Keys.Order().SequenceEqual(new[] { "A1", "A2", "B1", "B2" }) || canvases.Length != 2
            || !canvases.Select(c => c.Page).Order().SequenceEqual(new[] { 1, 2 })
            || canvases.Any(c => c.Anchor.Length != 2 || c.Anchor[0] is not ('A' or 'B'))
            || canvases.Select(c => c.Anchor[0]).Distinct().Count() != 1)
            return "document_shape";
        var first = originals.Values.First();
        if (originals.Values.Any(m => m.Empty() || m.Type() != MatType.CV_8UC3 || m.Size() != first.Size())) return "image_shape";
        var covered = originals.ToDictionary(kv => kv.Key, kv => new int[kv.Value.Height]);
        foreach (var c in canvases)
        {
            if (!originals.TryGetValue(c.Anchor, out var anchor) || c.Anchor != c.Anchor[..1] + c.Page) return "anchor_page";
            var next = 0;
            foreach (var p in c.Pieces)
            {
                if (p.Top != next || p.Length <= 0 || (long)next + p.Length > anchor.Height) return "canvas_coverage";
                foreach (var (origin, side) in new[] { (p.A, "A"), (p.B, "B") })
                {
                    if (origin is null) continue;
                    if (!originals.TryGetValue(origin.Key, out var image) || !origin.Key.StartsWith(side, StringComparison.Ordinal)
                        || origin.Top < 0 || (long)origin.Top + p.Length > image.Height) return "source_range";
                    for (var y = origin.Top; y < origin.Top + p.Length; y++) covered[origin.Key][y]++;
                }
                var kept = c.Anchor[0] == 'A' ? p.A : p.B;
                if (kept is null || kept.Key != c.Anchor || kept.Top != p.Top) return "anchor_discontinuity";
                if ((p.A is null || p.B is null) && !White(anchor, p.Top, p.Length)) return "nonwhite_space";
                next += p.Length;
            }
            if (next != anchor.Height) return "canvas_coverage";
        }
        foreach (var o in omitted)
        {
            if (!originals.TryGetValue(o.Key, out var image) || o.Top < 0 || o.Length <= 0
                || (long)o.Top + o.Length > image.Height) return "omission_range";
            if (White(image, o.Top, o.Length)) return "white_cause";
            for (var y = o.Top; y < o.Top + o.Length; y++) covered[o.Key][y]++;
        }
        return covered.Values.Any(rows => rows.Any(n => n != 1)) ? "source_coverage" : null;
    }

    internal static Mat Render(IReadOnlyDictionary<string, Mat> originals, Canvas canvas, bool a)
    {
        var result = new Mat(originals[canvas.Anchor].Size(), MatType.CV_8UC3, Scalar.White);
        try
        {
            foreach (var p in canvas.Pieces)
            {
                if ((a ? p.A : p.B) is not { } origin) continue;
                using var source = new Mat(originals[origin.Key], new Rect(0, origin.Top, result.Width, p.Length));
                using var target = new Mat(result, new Rect(0, p.Top, result.Width, p.Length)); source.CopyTo(target);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static bool White(Mat image, int top, int length)
    {
        using var band = new Mat(image, new Rect(0, top, image.Width, length)); using var channels = band.Reshape(1);
        Cv2.MinMaxLoc(channels, out double minimum, out double _); return minimum == 255;
    }
}
