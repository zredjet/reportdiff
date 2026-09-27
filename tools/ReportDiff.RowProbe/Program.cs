using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;

if (args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && a is not ("--pdf" or "--white-band" or "--white-band-pdf" or "--mapped-features" or "--mapped-identity" or "--common-surface")))
    throw new ArgumentException("未対応の試行モードです。");
var folder = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "out/t3-1b-probe/results");
Directory.CreateDirectory(folder);
if (args.Contains("--common-surface")) { CommonSurfaceProbe.Run(Path.Combine(folder, "common-surface")); return; }
if (args.Contains("--mapped-features")) { MappedFeatureProbe.PdfCases(Path.Combine(folder, "mapped-features")); return; }
if (args.Contains("--mapped-identity")) { MappedFeatureProbe.Identity(Path.Combine(folder, "mapped-features")); return; }
if (args.Contains("--white-band-pdf")) { WhiteBandPdf.Run(Path.Combine(folder, "white-band-pdf")); return; }
if (args.Contains("--white-band")) { WhiteBandProbe.Run(Path.Combine(folder, "white-band")); return; }
if (args.Contains("--pdf")) { PdfSeams.Run(Path.Combine(folder, "pdf")); return; }
var rows = new List<object>();
foreach (var ruleWidth in new[] { 0, 1, 2, 3, 4, 6 })
foreach (var strict in new[] { false, true })
{
    const int width = 420, height = 700, top = 200, band = 60;
    using var a = new Mat(height, width, MatType.CV_8UC3, Scalar.All(255));
    using var b = a.Clone();
    for (var row = 0; row < 7; row++)
    {
        var ya = 90 + row * band;
        var yb = ya + (ya >= top ? band : 0);
        foreach (var (image, y) in new[] { (a, ya), (b, yb) })
        {
            Cv2.Rectangle(image, new Rect(65, y, 5, 24), Scalar.All(0), -1);
            Cv2.Rectangle(image, new Rect(65, y + 19, 20 + row, 5), Scalar.All(0), -1);
            Cv2.Rectangle(image, new Rect(120, y + 5, 11 + row, 8), Scalar.All(0), -1);
        }
    }
    Cv2.Rectangle(b, new Rect(65, 215, 45, 12), Scalar.All(0), -1);
    if (ruleWidth > 0)
    {
        Cv2.Rectangle(a, new Rect(40, 70, ruleWidth, 430), Scalar.All(0), -1);
        Cv2.Rectangle(b, new Rect(40, 70, ruleWidth, 490), Scalar.All(0), -1);
    }
    var map = new PageMap(a.Size(), b.Size(), new(width, height + band),
        [new(0, top, 0, 0), new(top, band, null, top), new(top + band, height - top - band, top, top + band),
         new(height, band, height - band, null)]);
    using var aa = map.Render(a, PageSpace.A);
    using var bb = map.Render(b, PageSpace.B);
    var options = new ComparisonParameters { Diff = strict ? new() { MaxShiftMm = 0, EdgeTolerance = 0 } : new(),
        Exclude = [ProbeMasks.Band(top, width, band, aa.Size()),
            ProbeMasks.Band(height, width, band, aa.Size())] };
    using var result = PageComparer.Compare(aa, bb, options);
    var name = $"rule-{ruleWidth}-{(strict ? "strict" : "normal")}";
    Save($"{name}-a.png", aa); Save($"{name}-b.png", bb); Save($"{name}-raw.png", result.RawMask);
    rows.Add(new { name, result.Status, result.RawPixels, clusters = result.Clusters.Select(c => c.Bounds).ToArray() });
}
var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(folder, "seams.json"), json);
Console.WriteLine(json);
void Save(string name, Mat image) => File.WriteAllBytes(Path.Combine(folder, name), image.ImEncode(".png"));
