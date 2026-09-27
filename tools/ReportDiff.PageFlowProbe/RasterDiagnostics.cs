using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using PDFtoImage;
using ReportDiff.Core;
using ReportDiff.Pdf;
using SkiaSharp;

// 固定PDFを読むだけの診断。描画対照と採用前のC/Dは製品出力へ使用しない。
internal static class RasterDiagnostics
{
    internal static void Run(string inputs, string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new ArgumentException("診断の出力先は空にしてください。");
        Directory.CreateDirectory(output);
        var records = new List<object>();
        foreach (var (id, folder, name) in new[] {
            ("skia", "inputs", "a4-2"), ("dense", "inputs", "a4-dense-2"), ("inset", "inputs-control", "a4-2"),
            ("fractional", "inputs-grid", "a4-2"), ("integer", "inputs-integer", "a4-2"),
            ("sparse", "inputs-sparse", "a4-sparse-2") })
        {
            var directory = Path.Combine(output, id); Directory.CreateDirectory(directory);
            var aPath = Path.Combine(inputs, folder, name, "a.pdf");
            var bPath = Path.Combine(inputs, folder, name, "b.pdf");
            using var pdfA = PdfReader.Open(aPath); using var pdfB = PdfReader.Open(bPath);
            using var textA = new PdfTextReader(aPath); using var textB = new PdfTextReader(bPath);
            var keys = new[] { PageSpace.A, PageSpace.B }.SelectMany(side => Enumerable.Range(1, 2)
                .Select(page => new PageFlowPageKey(side, page))).ToArray();
            var collector = new PageFlowCollector(keys); var renderRecords = new List<object>();
            foreach (var key in keys)
            {
                using var image = Read(key);
                Require(collector.Add(key, image, Text(key, image), .5), "記述収集");
                Save($"{key.Side}-{key.Page}-original.png", image);
                using var stream = File.OpenRead(key.Side == PageSpace.A ? aPath : bPath);
                var size = Conversion.GetPageSize(stream, page: key.Page - 1, leaveOpen: true);
                var scaledWidth = size.Width * (300f / 72f); var scaledHeight = size.Height * (300f / 72f);
                foreach (var mode in new[] { "default", "ceil", "floor", "no-path-aa" })
                {
                    var options = new RenderOptions(Dpi: 300, WithAnnotations: true, WithFormFill: true,
                        AntiAliasing: mode == "no-path-aa" ? PdfAntiAliasing.Text | PdfAntiAliasing.Images : PdfAntiAliasing.All,
                        BackgroundColor: SKColors.White,
                        Width: mode is "ceil" or "floor" ? (int)(mode == "ceil" ? Math.Ceiling(scaledWidth) : Math.Floor(scaledWidth)) : null,
                        Height: mode is "ceil" or "floor" ? (int)(mode == "ceil" ? Math.Ceiling(scaledHeight) : Math.Floor(scaledHeight)) : null);
                    using var bitmap = Conversion.ToImage(stream, page: key.Page - 1, leaveOpen: true, options: options);
                    using var bgra = Mat.FromPixelData(bitmap.Height, bitmap.Width, MatType.CV_8UC4, bitmap.GetPixels(), bitmap.RowBytes);
                    using var bgr = new Mat(); Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
                    if (mode == "default") Require(bgr.Size() == image.Size() && Cv2.Norm(bgr, image, NormTypes.INF) == 0, "直接APIと製品の元画素");
                    else Save($"{key.Side}-{key.Page}-{mode}.png", bgr);
                    renderRecords.Add(new { key.Side, key.Page, mode, bitmap.Width, bitmap.Height,
                        media_width = size.Width, media_height = size.Height, scaled_width_float = scaledWidth, scaled_height_float = scaledHeight });
                }
            }
            var document = collector.Complete() ?? throw new InvalidOperationException(collector.FailureReason);
            var parameters = new ComparisonParameters(); var optionsRows = new RowOptions { Enabled = true };
            var plan = PageFlowPlan.Prepare(document, Read, parameters, optionsRows);
            var pages = new List<object>();
            foreach (var page in plan.Pages)
            {
                using var a = Read(new(PageSpace.A, page.Number)); using var b = Read(new(PageSpace.B, page.Number));
                object? comparison = null;
                if (plan.Decision.Ready)
                {
                    using var baseline = PageComparer.Compare(a, b, parameters);
                    using var candidate = plan.Compare(page.Number, a, b);
                    var adoption = PageFlowAdoption.Evaluate(plan, page.Number, a, b, parameters, optionsRows,
                        Text(new(PageSpace.A, page.Number), a), Text(new(PageSpace.B, page.Number), b));
                    Save($"C-{page.Number}-A.png", candidate.ContentA); Save($"C-{page.Number}-B.png", candidate.ContentB);
                    Save($"C-{page.Number}-raw.png", candidate.Content.RawMask);
                    Save($"D-{page.Number}-raw.png", candidate.Display.Comparison.RawMask);
                    Save($"baseline-{page.Number}-raw.png", baseline.RawMask);
                    comparison = new { adoption, baseline_raw = baseline.RawPixels, candidate_raw = candidate.Content.RawPixels,
                        content_clusters = candidate.Content.Clusters, display_clusters = candidate.Display.Comparison.Clusters,
                        candidate.Display.OmissionAudit, candidate.Display.StructuralChanges };
                }
                var surface = page.Built?.Surface;
                pages.Add(new { page.Number, page.Built?.Status, page.Built?.Reason,
                    pieces = surface?.Pieces, display_segments = surface?.DisplayMap.Segments,
                    omitted_bands = surface?.OmittedBands, surface?.NeighborhoodRadius, comparison });
            }
            records.Add(new { id, files = new[] { aPath, bPath }, sha256 = new[] { Hash(aPath), Hash(bPath) },
                render_records = renderRecords, plan.Decision, plan.Links,
                layouts = plan.Inference.Layouts.A.Concat(plan.Inference.Layouts.B).Select(l => new {
                    l.Page.Key, l.Regular, l.BodyStart, l.BodyEnd, l.Pitch, l.Body }), pages });
            Console.WriteLine($"{id}: range_ready={plan.Decision.Ready}");
            Mat Read(PageFlowPageKey key) { using var p = (key.Side == PageSpace.A ? pdfA : pdfB).ReadPage(key.Page); return p.Pixels.Clone(); }
            RowTextResult Text(PageFlowPageKey key, Mat image) => (key.Side == PageSpace.A ? textA : textB).ReadRowWords(key.Page, image.Size(), 300);
            void Save(string file, Mat image) => File.WriteAllBytes(Path.Combine(directory, file), image.ImEncode(".png"));
        }
        File.WriteAllText(Path.Combine(output, "diagnostics.json"), JsonSerializer.Serialize(new {
            diagnostic_only = true, product_adoption_rules_unchanged = true, records
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
