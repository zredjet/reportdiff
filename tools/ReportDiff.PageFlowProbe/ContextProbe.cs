using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;

// 製品とは独立した試行。候補の位置は正解データから与え、自動採用には使わない。
internal static class ContextProbe
{
    private static readonly (string Name, DiffOptions Diff)[] Profiles =
    [
        ("normal", new()),
        ("strict", new() { MaxShiftMm = 0, EdgeTolerance = 0 }),
        ("loose", new() { MaxShiftMm = 0.30 })
    ];

    internal static void Run(string fixtureFolder, string outputFolder)
    {
        if (Directory.Exists(outputFolder) && Directory.EnumerateFileSystemEntries(outputFolder).Any())
            throw new ArgumentException("出力先が空ではありません。");
        Directory.CreateDirectory(outputFolder);
        using var observations = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtureFolder, "observations.json")));
        var records = new List<object>();
        foreach (var item in observations.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("test").GetProperty("id").GetString()!;
            var aPath = Path.Combine(fixtureFolder, id, "a.pdf");
            var bPath = Path.Combine(fixtureFolder, id, "b.pdf");
            Require(Hash(File.ReadAllBytes(aPath)) == item.GetProperty("a_sha256").GetString(), "入力PDF Aのハッシュ不一致");
            Require(Hash(File.ReadAllBytes(bPath)) == item.GetProperty("b_sha256").GetString(), "入力PDF Bのハッシュ不一致");
            using var pdfA = PdfReader.Open(aPath);
            using var pdfB = PdfReader.Open(bPath);
            foreach (var link in item.GetProperty("links").EnumerateArray())
            {
                var known = link.GetProperty("link");
                var pageA = known.GetProperty("a_page").GetInt32();
                var pageB = known.GetProperty("b_page").GetInt32();
                using var a = pdfA.ReadPage(pageA);
                using var b = pdfB.ReadPage(pageB);
                var rectA = Rectangle(link.GetProperty("band_a"));
                var rectB = Rectangle(link.GetProperty("band_b"));
                records.Add(Check($"pdf-{id}-{pageA}", a.Pixels, b.Pixels, rectA, rectB,
                    id is not ("text-change" or "same-text-pixels" or "band-edge-tone"),
                    new { source = "fixed_pdf", fixture = id, page_a = pageA, page_b = pageB,
                        a_sha256 = item.GetProperty("a_sha256").GetString(), b_sha256 = item.GetProperty("b_sha256").GetString(),
                        text_equal = link.GetProperty("text_equal").GetBoolean() }, outputFolder));
            }
        }

        const int width = 384, height = 480, bandTop = 120, bandHeight = 200;
        var band = new Rect(0, bandTop, width, bandHeight);
        foreach (var kind in new[] { "gray", "pale", "color" })
        foreach (var offset in new[] { -4, -2, 0, 1, 196, 198, 200 })
        {
            using var a = new Mat(height, width, MatType.CV_8UC3, Scalar.All(255));
            if (kind == "gray") Fill(a, new(120, 0, 6, height), Scalar.All(200));
            if (kind == "color") Fill(a, new(120, 0, 6, height), new(160, 0, 0));
            using var b = a.Clone();
            var patch = kind == "pale" ? new Rect(210, bandTop + offset, 24, 4) : new Rect(120, bandTop + offset, 6, 4);
            Fill(b, patch, kind switch { "gray" => Scalar.All(80), "pale" => Scalar.All(242), _ => new(0, 0, 160) });
            records.Add(Check($"boundary-{kind}-{offset}", a, b, band, band, offset is -4 or 200,
                new { source = "raster_boundary", mutation = kind, offset, patch }, outputFolder));
        }

        using (var a = new Mat(height, width, MatType.CV_8UC3, Scalar.All(255)))
        using (var b = a.Clone())
        {
            Fill(a, new(120, 0, 6, height), Scalar.All(0));
            Fill(a, new(50, 20, 146, 6), Scalar.All(0));
            Fill(a, new(50, 450, 146, 6), Scalar.All(0));
            Fill(b, new(122, 0, 6, height), Scalar.All(0));
            Fill(b, new(52, 20, 146, 6), Scalar.All(0));
            Fill(b, new(52, 450, 146, 6), Scalar.All(0));
            records.Add(Check("long-connected-rule", a, b, band, band, false,
                new { source = "raster_connected", group_extends_beyond_guard_40 = true }, outputFolder));
        }
        using (var a = new Mat(height, width, MatType.CV_8UC3, Scalar.All(255)))
        using (var b = a.Clone())
        {
            Fill(a, new(120, 185, 6, 45), Scalar.All(0));
            Fill(b, new(122, 185, 6, 45), Scalar.All(0));
            records.Add(Check("isolated-shift", a, b, band, band, false,
                new { source = "raster_shift", intended_core_normal = "ignore", intended_core_strict = "detect" }, outputFolder));
        }
        using (var a = new Mat(height, width, MatType.CV_8UC3, Scalar.All(255)))
        using (var b = a.Clone())
        {
            Fill(b, new(220, bandTop, 1, 1), Scalar.All(0));
            records.Add(Check("single-pixel-at-cut", a, b, band, band, false,
                new { source = "raster_noise", strict_raw_required = 1, strict_clusters_expected = 0 }, outputFolder));
        }
        File.WriteAllText(Path.Combine(outputFolder, "context.json"), JsonSerializer.Serialize(records,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
    }

    internal static object Check(string id, Mat a, Mat b, Rect bandA, Rect bandB, bool expectedExactEvidence, object input, string root)
    {
        Require(bandA.Size == bandB.Size && bandA.Width == a.Width && bandB.Width == b.Width, "帯の寸法が対応していません。");
        var folder = Path.Combine(root, id);
        Directory.CreateDirectory(folder);
        var beforeA = Hash(a.ImEncode(".png"));
        var beforeB = Hash(b.ImEncode(".png"));
        Save("a.png", a); Save("b.png", b);
        using var cropA = new Mat(a, bandA);
        using var cropB = new Mat(b, bandB);
        Save("crop-a.png", cropA); Save("crop-b.png", cropB);
        var exact = Cv2.Norm(cropA, cropB, NormTypes.INF) == 0;
        Require(exact == expectedExactEvidence, $"{id}: 固定した帯の完全一致期待と異なります。");
        using var replacementA = a.Clone();
        using var replacementB = b.Clone();
        using (var target = new Mat(replacementA, bandA)) cropB.CopyTo(target);
        using (var target = new Mat(replacementB, bandB)) cropA.CopyTo(target);
        Validate(a, replacementA, bandA, cropB);
        Validate(b, replacementB, bandB, cropA);
        Save("a-replaced.png", replacementA); Save("b-replaced.png", replacementB);
        var measurements = new List<Measurement>();
        foreach (var (profile, diff) in Profiles)
        {
            foreach (var reverse in new[] { false, true })
            {
                Compare("endpoint-a", "a.png", "a-replaced.png", a, replacementA, reverse);
                Compare("endpoint-b", "b.png", "b-replaced.png", b, replacementB, reverse);
                Compare("crop-control", "crop-a.png", "crop-b.png", cropA, cropB, reverse);
                if (id.StartsWith("boundary-pale-", StringComparison.Ordinal))
                {
                    // 二分した差が両側で許容内になる反例。製品のCの代替には使わない。
                    Compare("whole-change-control", "a.png", "b.png", a, b, reverse);
                    Compare("outside-only-control", "a.png", "b-replaced.png", a, replacementB, reverse);
                }
            }

            void Compare(string role, string fileA, string fileB, Mat aa, Mat bb, bool reverse)
            {
                using var result = PageComparer.Compare(reverse ? bb : aa, reverse ? aa : bb, new() { Diff = diff });
                var mask = $"{role}-{profile}-{(reverse ? "reverse" : "forward")}-raw.png";
                Save(mask, result.RawMask);
                measurements.Add(new(role, profile, reverse, reverse ? fileB : fileA, reverse ? fileA : fileB, mask,
                    result.RawPixels, result.Status, result.Clusters.Count, result.NoiseDropped, result.AbsorbedGroups, result.MaxShiftPx));
            }
        }
        Require(beforeA == Hash(a.ImEncode(".png")) && beforeB == Hash(b.ImEncode(".png")), "元画像が変更されました。");
        var decisions = Profiles.Select(p =>
        {
            var endpoints = measurements.Where(m => m.Profile == p.Name && m.Role.StartsWith("endpoint-", StringComparison.Ordinal)).ToArray();
            var allZero = endpoints.All(m => m.Raw == 0);
            Require(endpoints.Length == 4, "両端・両順序の比較が不足しています。");
            Require(!exact || allZero, $"{id}: 同一画素の帯に差分が生じました。");
            return new { profile = p.Name, all_endpoint_raw_zero = allZero, exact_band_evidence = exact && allZero,
                expected_exact_band_evidence = expectedExactEvidence,
                reason = exact ? "exact_band_pixels_and_full_context" : "nonidentical_band_not_proven" };
        }).ToArray();
        Console.WriteLine($"{id}: 帯の完全一致={exact}");
        return new { id, input, oracle_only = true, automatic_carry_adopted = false,
            band_a = new { x = bandA.X, y = bandA.Y, w = bandA.Width, h = bandA.Height },
            band_b = new { x = bandB.X, y = bandB.Y, w = bandB.Width, h = bandB.Height },
            original_a_sha256 = beforeA, original_b_sha256 = beforeB,
            exact_band_pixels = exact, original_images_unchanged = true, replacement_outside_unchanged = true,
            decisions, measurements };
        void Save(string name, Mat image) => File.WriteAllBytes(Path.Combine(folder, name), image.ImEncode(".png"));
    }

    private static void Validate(Mat original, Mat replacement, Rect band, Mat donor)
    {
        using var inside = new Mat(replacement, band);
        Require(Cv2.Norm(inside, donor, NormTypes.INF) == 0, "置換帯に元画素以外の値が入りました。");
        foreach (var (start, end) in new[] { (0, band.Top), (band.Bottom, original.Height) })
        {
            if (start == end) continue;
            using var a = original.RowRange(start, end);
            using var b = replacement.RowRange(start, end);
            Require(Cv2.Norm(a, b, NormTypes.INF) == 0, "置換帯の外側が変更されました。");
        }
    }

    private static void Fill(Mat image, Rect rect, Scalar color) { using var roi = new Mat(image, rect); roi.SetTo(color); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static Rect Rectangle(JsonElement rect) => new(rect.GetProperty("x").GetInt32(), rect.GetProperty("y").GetInt32(),
        rect.GetProperty("w").GetInt32(), rect.GetProperty("h").GetInt32());
    private sealed record Measurement(string Role, string Profile, bool Reverse, string InputA, string InputB, string Mask,
        int Raw, string Status, int Clusters, int NoiseDropped, int AbsorbedGroups, int MaxShiftPx);
}
