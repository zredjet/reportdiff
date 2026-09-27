using System.Text.Json;
using System.Security.Cryptography;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using ReportDiff.Tests;

internal static class WhiteBandPdf
{
    internal static void Run(string folder)
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() || OperatingSystem.IsWindows())) return;
        Directory.CreateDirectory(folder);
        var outcomes = new List<object>();
        var cases = new[] { ("unchanged", false, (int?)null), ("number", true, (int?)null),
            ("boundary-0", false, (int?)0), ("boundary-1", false, (int?)1), ("boundary-2", false, (int?)2),
            ("boundary-4", false, (int?)4), ("boundary-8", false, (int?)8) };
        foreach (var (name, changed, toneDistance) in cases)
        {
            var aPath = Path.Combine(folder, name + "-a.pdf");
            var baselineBPath = Path.Combine(folder, name + "-baseline-b.pdf");
            var bPath = Path.Combine(folder, name + "-b.pdf");
            File.WriteAllBytes(aPath, PdfFixture.RowProbePage(false, false, 1.44));
            File.WriteAllBytes(baselineBPath, PdfFixture.RowProbePage(false, changed, 1.44, toneDistance));
            File.WriteAllBytes(bPath, PdfFixture.RowProbePage(true, changed, 1.44, toneDistance));
            using var aPdf = PdfReader.Open(aPath); using var bPdf = PdfReader.Open(bPath); using var baselineBPdf = PdfReader.Open(baselineBPath);
            using var aImage = aPdf.ReadPage(1, 300); using var bImage = bPdf.ReadPage(1, 300); using var baselineBImage = baselineBPdf.ReadPage(1, 300);
            var sourceAHash = Hash(aImage.Pixels); var sourceBHash = Hash(bImage.Pixels);
            using var originalRaw = RawOverlay.Create(aImage.Pixels, bImage.Pixels);
            var rawHash = Hash(originalRaw);
            const int top = 400, band = 100;
            var size = aImage.Pixels.Size();
            var map = WhiteBandProbe.Map(size, top, band);
            using var a = map.Render(aImage.Pixels, PageSpace.A); using var b = map.Render(bImage.Pixels, PageSpace.B);
            using var baselineBMapped = map.Render(baselineBImage.Pixels, PageSpace.A);
            var displayAHash = Hash(a); var displayBHash = Hash(b);
            var bands = new[] { new Rect(0, top, size.Width, band), new Rect(0, size.Height, size.Width, band) };
            using var allowed = new Mat(a.Size(), MatType.CV_8UC1, Scalar.All(255));
            foreach (var gap in bands) { using var r = new Mat(allowed, gap); r.SetTo(0); }
            if (Cv2.Norm(b, baselineBMapped, NormTypes.INF, allowed) != 0)
                throw new InvalidOperationException("挿入前 B と整列後 B の比較対象画素が一致しません。");
            using var preparedA = WhiteBandProbe.WhiteBands(a, bands);
            using var preparedB = WhiteBandProbe.WhiteBands(b, bands);
            if (Cv2.Norm(a, preparedA, NormTypes.INF, allowed) != 0 || Cv2.Norm(b, preparedB, NormTypes.INF, allowed) != 0)
                throw new InvalidOperationException("白い帯の外側の画素を書き換えています。");
            Save(name + "-source-a.png", aImage.Pixels); Save(name + "-baseline-b.png", baselineBImage.Pixels);
            Save(name + "-source-b.png", bImage.Pixels); Save(name + "-display-a.png", a); Save(name + "-display-b.png", b);
            Save(name + "-prepared-a.png", preparedA); Save(name + "-prepared-b.png", preparedB); Save(name + "-raw-evidence.png", originalRaw);
            foreach (var profile in new[] { "normal", "strict", "loose" })
            foreach (var reverse in new[] { false, true })
            {
                var parameters = WhiteBandProbe.Profile(profile);
                var comparedParameters = parameters with { Exclude = bands.Select(r => ProbeMasks.Band(r.Y, r.Width, r.Height, a.Size())).ToArray() };
                using var baseline = PageComparer.Compare(reverse ? baselineBImage.Pixels : aImage.Pixels, reverse ? aImage.Pixels : baselineBImage.Pixels, parameters);
                using var previous = PageComparer.Compare(reverse ? b : a, reverse ? a : b, comparedParameters);
                using var candidate = PageComparer.Compare(reverse ? preparedB : preparedA, reverse ? preparedA : preparedB, comparedParameters);
                var id = name + "-" + profile + (reverse ? "-reverse" : "-forward");
                Save(id + "-baseline-raw.png", baseline.RawMask); Save(id + "-previous-raw.png", previous.RawMask); Save(id + "-candidate-raw.png", candidate.RawMask);
                outcomes.Add(new { id, name, profile, reverse, number_changed = changed, tone_distance_px = toneDistance,
                    baseline = WhiteBandProbe.Snapshot(baseline), previous = WhiteBandProbe.Snapshot(previous), candidate = WhiteBandProbe.Snapshot(candidate),
                    detection_lost = baseline.Clusters.Count > 0 && candidate.Clusters.Count == 0,
                    paired_b_matches_baseline_b = true, changed_pixels_outside_band = 0,
                    source_a_sha256 = sourceAHash, source_b_sha256 = sourceBHash, raw_evidence_sha256 = rawHash,
                    source_size_px = new { width = size.Width, height = size.Height },
                    excluded_bands_px = bands.Select(r => new { y = r.Y, h = r.Height }).ToArray() });
                Console.WriteLine($"{id}: 基準={baseline.RawPixels}px/{baseline.Clusters.Count}件 従来={previous.RawPixels}px/{previous.Clusters.Count}件 候補={candidate.RawPixels}px/{candidate.Clusters.Count}件");
            }
            using var afterRaw = RawOverlay.Create(aImage.Pixels, bImage.Pixels);
            if (Hash(aImage.Pixels) != sourceAHash || Hash(bImage.Pixels) != sourceBHash || Hash(a) != displayAHash || Hash(b) != displayBHash || Hash(afterRaw) != rawHash)
                throw new InvalidOperationException("元画像・表示画像・確認用オーバーレイを変更しています。");
        }
        File.WriteAllText(Path.Combine(folder, "white-band-pdf.json"), JsonSerializer.Serialize(outcomes, new JsonSerializerOptions { WriteIndented = true }));
        void Save(string name, Mat image) => File.WriteAllBytes(Path.Combine(folder, name), image.ImEncode(".png"));
    }

    private static string Hash(Mat image) => Convert.ToHexStringLower(SHA256.HashData(image.ImEncode(".png")));
}
