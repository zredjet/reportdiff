using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Tests;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

internal static class PdfSeams
{
    internal static void Run(string folder)
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() || OperatingSystem.IsWindows())) return;
        Directory.CreateDirectory(folder);
        var outcomes = new List<object>();
        foreach (var ruleWidth in new[] { 0.0, 0.24, 0.48, 0.72, 0.96, 1.44 })
        foreach (var changed in new[] { false, true })
        {
            var prefix = $"pdf-rule-{ruleWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{(changed ? "changed" : "inserted")}";
            var aPath = Path.Combine(folder, prefix + "-a.pdf");
            var bPath = Path.Combine(folder, prefix + "-b.pdf");
            File.WriteAllBytes(aPath, PdfFixture.RowProbePage(false, false, ruleWidth));
            File.WriteAllBytes(bPath, PdfFixture.RowProbePage(true, changed, ruleWidth));
            using var aPdf = PdfReader.Open(aPath); using var bPdf = PdfReader.Open(bPath);
            using var aImage = aPdf.ReadPage(1, 300); using var bImage = bPdf.ReadPage(1, 300);
            using var text = PdfDocument.Open(File.ReadAllBytes(bPath));
            var words = text.GetPage(1).GetWords(new NearestNeighbourWordExtractor(new() { MaxDegreeOfParallelism = 1 })).ToArray();
            var baselines = words.Select(w => new { w.Text, baseline = w.Letters.First().StartBaseLine.Y }).ToArray();
            const int top = 400, band = 100;
            var size = aImage.Pixels.Size();
            var map = new PageMap(size, size, new(size.Width, size.Height + band),
                [new(0, top, 0, 0), new(top, band, null, top), new(top + band, size.Height - top - band, top, top + band),
                 new(size.Height, band, size.Height - band, null)]);
            using var a = map.Render(aImage.Pixels, PageSpace.A); using var b = map.Render(bImage.Pixels, PageSpace.B);
            using var direct = new Mat(); Cv2.Absdiff(a, b, direct);
            using var allowed = new Mat(a.Size(), MatType.CV_8UC1, Scalar.All(255));
            foreach (var y in new[] { top, size.Height })
            {
                using var gap = new Mat(allowed, new Rect(0, y, size.Width, band)); gap.SetTo(0);
            }
            var pairedNorm = Cv2.Norm(direct, NormTypes.INF, allowed);
            Save(prefix + "-aligned-a.png", a); Save(prefix + "-aligned-b.png", b);
            foreach (var strict in new[] { false, true })
            {
                var parameters = new ComparisonParameters
                {
                    Diff = strict ? new() { MaxShiftMm = 0, EdgeTolerance = 0 } : new(),
                    Exclude = [ProbeMasks.Band(top, size.Width, band, a.Size()),
                        ProbeMasks.Band(size.Height, size.Width, band, a.Size())]
                };
                using var comparison = PageComparer.Compare(a, b, parameters);
                Save(prefix + (strict ? "-strict" : "-normal") + "-raw.png", comparison.RawMask);
                outcomes.Add(new { prefix, strict, ruleWidth, changed, pairedNorm,
                    sourceSize = new { width = size.Width, height = size.Height },
                    canvasSize = new { width = a.Width, height = a.Height },
                    map.Segments, comparison.Status, comparison.RawPixels,
                    clusters = comparison.Clusters.Select(c => new { x = c.Bounds.X, y = c.Bounds.Y, w = c.Bounds.Width, h = c.Bounds.Height, c.Pixels }).ToArray(), baselines });
                Console.WriteLine($"{prefix} {(strict ? "strict" : "normal")}: pairedNorm={pairedNorm} raw={comparison.RawPixels} clusters={comparison.Clusters.Count}");
            }
        }
        File.WriteAllText(Path.Combine(folder, "pdf-seams.json"), JsonSerializer.Serialize(outcomes, new JsonSerializerOptions { WriteIndented = true }));
        void Save(string name, Mat image) => File.WriteAllBytes(Path.Combine(folder, name), image.ImEncode(".png"));
    }
}
