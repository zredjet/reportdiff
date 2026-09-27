using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using ReportDiff.Tests;

internal static class MappedFeatureProbe
{
    internal static void Identity(string folder)
    {
        Directory.CreateDirectory(folder);
        using var golden = JsonDocument.Parse(File.ReadAllText("reference/golden/expected.json"));
        var results = new List<object>();
        foreach (var item in golden.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            using var a = Read($"reference/golden/{id}_a.png"); using var b = Read($"reference/golden/{id}_b.png");
            var j = item.GetProperty("params");
            double V(string key) => j.GetProperty(key).GetDouble();
            var p = new ComparisonParameters
            {
                Dpi = (int)V("dpi"),
                Diff = new() { MaxShiftMm = V("max_shift_mm"), ColorThreshold = V("color_threshold"), EdgeTolerance = V("edge_tolerance") },
                Ink = new() { BackgroundRadiusMm = V("ink_background_radius_mm"), ContrastThreshold = V("ink_contrast_threshold") },
                Cluster = new() { MergeXMm = V("merge_x_mm"), MergeYMm = V("merge_y_mm"), MinPixels = (int)V("min_pixels"),
                    MaxDiffRatio = V("max_diff_ratio"), ReadingBandMm = V("reading_band_mm") },
                Exclude = j.GetProperty("exclude_mm").EnumerateArray().Select(r => new RectMm(r[0].GetDouble(), r[1].GetDouble(), r[2].GetDouble(), r[3].GetDouble())).ToArray()
            };
            using var baseline = PageComparer.Compare(a, b, p);
            using var raw = MappedFeatures.Calculate(a, b, p, PageMap.Unaligned(a.Size(), b.Size()), false);
            using var candidate = PageComparer.FromRawDifference(raw, p, a, b);
            var actual = JsonSerializer.Serialize(WhiteBandProbe.Snapshot(candidate));
            if (Cv2.Norm(candidate.RawMask, baseline.RawMask, NormTypes.INF) != 0 || actual != JsonSerializer.Serialize(WhiteBandProbe.Snapshot(baseline)))
                throw new InvalidOperationException($"恒等写像が製品と不一致: {id}");
            results.Add(new { id, product_equal = true, result = WhiteBandProbe.Snapshot(candidate) });
            Console.WriteLine($"恒等写像 {id}: 製品と全マスク・状態・クラスタ・分類一致");
        }
        Write(folder, "identity-csharp.json", results);
    }

    internal static void PdfCases(string folder)
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() || OperatingSystem.IsWindows())) return;
        Directory.CreateDirectory(folder);
        var outcomes = new List<object>();
        foreach (var (name, changed, patch) in new (string, bool, int?)[]
                 { ("gray-unchanged", false, null), ("gray-tone", true, null), ("patch-0", true, 0),
                   ("patch-1", true, 1), ("patch-3", true, 3), ("patch-20", true, 20) })
        {
            var aPath = Path.Combine(folder, name + "-a.pdf"); var bPath = Path.Combine(folder, name + "-b.pdf");
            var baselinePath = Path.Combine(folder, name + "-baseline-b.pdf");
            File.WriteAllBytes(aPath, PdfFixture.RowProbePage(false, false, 0, mappedTone: new(false)));
            File.WriteAllBytes(baselinePath, PdfFixture.RowProbePage(false, false, 0, mappedTone: new(changed)));
            File.WriteAllBytes(bPath, PdfFixture.RowProbePage(true, false, 0, mappedTone: new(changed, patch)));
            using var aPdf = PdfReader.Open(aPath); using var bPdf = PdfReader.Open(bPath); using var baselinePdf = PdfReader.Open(baselinePath);
            using var aa = aPdf.ReadPage(1, 300); using var bb = bPdf.ReadPage(1, 300); using var bc = baselinePdf.ReadPage(1, 300);
            var a = aa.Pixels; var b = bb.Pixels; var baselineB = bc.Pixels;
            var aHash = Hash(a); var bHash = Hash(b);
            using var overlay = RawOverlay.Create(a, b); var overlayHash = Hash(overlay);
            var map = WhiteBandProbe.Map(a.Size(), 400, 100);
            using var displayA = map.Render(a, PageSpace.A); using var displayB = map.Render(b, PageSpace.B);
            using var mappedBaseline = map.Render(baselineB, PageSpace.A);
            var displayAHash = Hash(displayA); var displayBHash = Hash(displayB);
            using var valid = new Mat(map.CanvasSize, MatType.CV_8UC1, Scalar.All(255));
            var bands = map.Segments.Where(s => s.AStart is null || s.BStart is null).ToArray();
            foreach (var band in bands) { using var roi = new Mat(valid, new Rect(0, band.CanvasStart, valid.Width, band.Length)); roi.SetTo(0); }
            if (Cv2.Norm(displayB, mappedBaseline, NormTypes.INF, valid) != 0) throw new InvalidOperationException("比較対象の B 画素が基準と異なります。");
            Save(name + "-source-a.png", a); Save(name + "-source-b.png", b); Save(name + "-baseline-b.png", baselineB);
            Save(name + "-display-a.png", displayA); Save(name + "-display-b.png", displayB); Save(name + "-raw-evidence.png", overlay);
            foreach (var profile in new[] { "normal", "strict", "loose" })
            foreach (var reverse in new[] { false, true })
            {
                var p = WhiteBandProbe.Profile(profile);
                var excluded = p with { Exclude = bands.Select(s => ProbeMasks.Band(s.CanvasStart, a.Width, s.Length, map.CanvasSize)).ToArray() };
                using var baseline = PageComparer.Compare(reverse ? baselineB : a, reverse ? a : baselineB, p);
                var trace = new MappedFeatures.Trace();
                using var raw = MappedFeatures.Calculate(a, b, p, map, reverse, trace);
                using var result = PageComparer.FromRawDifference(raw, excluded);
                var initialPaired = 0;
                var validData = valid.AsSpan<byte>();
                for (var i = 0; i < trace.Initial.Length; i++) if (validData[i] != 0 && trace.Initial[i] != 0) initialPaired++;
                var id = name + "-" + profile + (reverse ? "-reverse" : "-forward");
                Save(id + "-baseline-raw.png", baseline.RawMask); Save(id + "-csharp-raw.png", result.RawMask);
                outcomes.Add(new { id, name, profile, reverse, patch_distance_px = patch, changed,
                    baseline = WhiteBandProbe.Snapshot(baseline), candidate = WhiteBandProbe.Snapshot(result),
                    initial_paired_pixels = initialPaired, groups = trace.Groups,
                    detection_lost = baseline.Clusters.Count > 0 && result.Clusters.Count == 0,
                    paired_b_matches_baseline_b = true, source_a_sha256 = aHash, source_b_sha256 = bHash, raw_evidence_sha256 = overlayHash });
                Console.WriteLine($"{id}: 基準={baseline.RawPixels}/{baseline.Clusters.Count} 初期候補={initialPaired} 最終={result.RawPixels}/{result.Clusters.Count}");
            }
            using var afterOverlay = RawOverlay.Create(a, b);
            if (aHash != Hash(a) || bHash != Hash(b) || displayAHash != Hash(displayA) || displayBHash != Hash(displayB) || overlayHash != Hash(afterOverlay))
                throw new InvalidOperationException("元画像・表示画像・raw evidence が変更されています。");
        }
        Write(folder, "new-pdf-csharp.json", outcomes);
        void Save(string name, Mat image) => File.WriteAllBytes(Path.Combine(folder, name), image.ImEncode(".png"));
    }

    private static Mat Read(string path) => Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
    private static string Hash(Mat image) => Convert.ToHexStringLower(SHA256.HashData(image.ImEncode(".png")));
    private static void Write(string folder, string name, object value) => File.WriteAllText(Path.Combine(folder, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
}
