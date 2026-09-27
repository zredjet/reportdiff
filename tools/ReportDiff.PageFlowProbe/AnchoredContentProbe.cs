using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using static AnchoredContentSurface;

/// <summary>内容面の機械的安全性と比較コアだけを検証する。送りの最終採用・集約は行わない。</summary>
internal static class AnchoredContentProbe
{
    internal sealed record Input(string Name, Dictionary<string, string> Originals, Canvas[] Canvases,
        Omission[] Omitted, double MaxShiftMm, string? ExpectedRejection, Carry Carry);
    internal sealed record Carry(Origin Source, Origin Target, int Length);

    internal static void Run(string input, string output)
    {
        if (Directory.Exists(output)) throw new ArgumentException("出力先が既に存在します。");
        Directory.CreateDirectory(output);
        var cases = JsonSerializer.Deserialize<Input[]>(File.ReadAllText(input), AggregationProbe.Json)!;
        var records = new List<object>();
        foreach (var c in cases)
        {
            var originals = new Dictionary<string, Mat>(); var projected = new Dictionary<string, Mat>();
            try
            {
                foreach (var (key, path) in c.Originals)
                {
                    originals.Add(key, Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color));
                    projected.Add(key, Mat.Zeros(originals[key].Size(), MatType.CV_8UC1).ToMat());
                }
                var reason = Validate(originals, c.Canvases, c.Omitted);
                if (reason is null)
                {
                    using var source = new Mat(originals[c.Carry.Source.Key], new Rect(0, c.Carry.Source.Top,
                        originals[c.Carry.Source.Key].Width, c.Carry.Length));
                    using var target = new Mat(originals[c.Carry.Target.Key], new Rect(0, c.Carry.Target.Top,
                        originals[c.Carry.Target.Key].Width, c.Carry.Length));
                    if (Cv2.Norm(source, target, NormTypes.L1) != 0) reason = "carry_pixels";
                }
                if ((reason is null) != (c.ExpectedRejection is null)) throw new InvalidOperationException(c.Name + ": " + reason);
                if (c.ExpectedRejection is not null && c.ExpectedRejection != reason) throw new InvalidOperationException(c.Name + ": " + reason);
                var pages = new List<object>();
                if (reason is null)
                {
                    var folder = Path.Combine(output, c.Name); Directory.CreateDirectory(folder);
                    var parameters = new ComparisonParameters { Diff = new() { MaxShiftMm = c.MaxShiftMm } };
                    foreach (var canvas in c.Canvases.OrderBy(p => p.Page))
                    {
                        using var a = Render(originals, canvas, true); using var b = Render(originals, canvas, false);
                        using var result = PageComparer.Compare(a, b, parameters);
                        Save(a, $"p{canvas.Page}-C-A.png"); Save(b, $"p{canvas.Page}-C-B.png");
                        Save(result.RawMask, $"p{canvas.Page}-C-raw.png"); Save(result.LabelMask, $"p{canvas.Page}-C-label.png");
                        var fragments = new List<object>();
                        foreach (var piece in canvas.Pieces)
                        {
                            using var mask = new Mat(result.RawMask, new Rect(0, piece.Top, a.Width, piece.Length));
                            var count = Cv2.CountNonZero(mask);
                            foreach (var origin in new[] { piece.A, piece.B })
                            {
                                if (origin is null) continue;
                                using var target = new Mat(projected[origin.Key], new Rect(0, origin.Top, a.Width, piece.Length));
                                mask.CopyTo(target);
                                if (count > 0) fragments.Add(new { content_top = piece.Top, piece.Length, origin, pixels = count });
                            }
                        }
                        pages.Add(new { canvas.Page, canvas.Anchor, result.Status, result.RawPixels, result.NoiseDropped,
                            result.AbsorbedGroups, result.MaxShiftPx, result.Clusters, fragments });
                    }
                    foreach (var (key, mask) in projected) Save(mask, key + "-projected.png");
                    void Save(Mat image, string name)
                    {
                        Cv2.ImEncode(".png", image, out var bytes); File.WriteAllBytes(Path.Combine(folder, name), bytes);
                    }
                }
                records.Add(new { c.Name, rejection = reason, pages,
                    projected_pixels = projected.ToDictionary(kv => kv.Key, kv => Cv2.CountNonZero(kv.Value)),
                    adoption = "not_evaluated", aggregation = "not_evaluated" });
                Console.WriteLine(c.Name + ": " + (reason ?? "content_compared"));
            }
            finally { foreach (var m in originals.Values) m.Dispose(); foreach (var m in projected.Values) m.Dispose(); }
        }
        File.WriteAllText(Path.Combine(output, "content.json"), JsonSerializer.Serialize(records, AggregationProbe.Json));
    }
}
