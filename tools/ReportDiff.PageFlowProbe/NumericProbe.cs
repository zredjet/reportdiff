using System.Runtime.Versioning;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Cli;
using ReportDiff.Report;
using static ReportDiff.Core.PageFlowAggregation;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
internal static class NumericProbe
{
    internal static void Run(string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("出力先が空ではありません。");
        Directory.CreateDirectory(output); var cases = NumericFixtures.Create(output); var records = new List<object>();
        var config = Path.Combine(output, "carry.yaml"); File.WriteAllText(config, "rows: {enabled: true, carry_enabled: true}\nreport: {raw_overlay: true}\n");
        foreach (var scenario in cases)
        foreach (var reverse in new[] { false, true })
        {
            var name = scenario.Id + (reverse ? "-ba" : "-ab"); var target = Path.Combine(output, name); Directory.CreateDirectory(target);
            var pathA = reverse ? scenario.B : scenario.A; var pathB = reverse ? scenario.A : scenario.B;
            using var pdfA = PdfReader.Open(pathA); using var pdfB = PdfReader.Open(pathB);
            using var textA = new PdfTextReader(pathA); using var textB = new PdfTextReader(pathB);
            var keys = Enumerable.Range(1, pdfA.PageCount).Select(p => new PageFlowPageKey(PageSpace.A, p))
                .Concat(Enumerable.Range(1, pdfB.PageCount).Select(p => new PageFlowPageKey(PageSpace.B, p))).ToArray();
            var collector = new PageFlowCollector(keys);
            foreach (var key in keys)
            {
                using var image = Read(key); Save(image, $"p{key.Page}-O-{key.Side}.png");
                Require(collector.Add(key, image, Text(key, image), .5), "元記述収集");
            }
            var original = collector.Complete() ?? throw new InvalidOperationException(collector.FailureReason);
            var product = PageFlowPlan.Prepare(original, Read, new(), new() { Enabled = true });
            var numericInput = IndependentNumericRows.Describe(product); var numeric = IndependentNumericRows.Find(numericInput);
            var identifiedCollector = new PageFlowCollector(keys);
            foreach (var key in keys)
            {
                using var image = Read(key);
                Require(identifiedCollector.Add(key, image, IndependentNumericRows.Identified(key, product, numeric.Pairs), .5), "対応ID記述収集");
            }
            var identified = identifiedCollector.Complete() ?? throw new InvalidOperationException(identifiedCollector.FailureReason);
            var mapped = PageFlowPlan.Prepare(identified, Read, new(), new() { Enabled = true });
            var actualSupport = IndependentNumericRows.ForActualSupport(mapped, product, identified);
            var adoptions = new List<PageFlowAdoptionResult>(); var aliasAdoptions = new List<PageFlowAdoptionResult>();
            var pages = new List<Page>(); var projections = new List<object>(); var numericRanges = new List<object>();
            foreach (var page in mapped.Pages)
            {
                using var a = Read(new(PageSpace.A, page.Number)); using var b = Read(new(PageSpace.B, page.Number));
                if (!mapped.Decision.Ready)
                {
                    using var baseline = RowComparer.Compare(a, b, new(), new() { Enabled = true }, () =>
                        (Text(new(PageSpace.A, page.Number), a), Text(new(PageSpace.B, page.Number), b)));
                    pages.Add(new(page.Number, true, baseline.Comparison.Clusters.Count, baseline.DifferenceCount,
                        baseline.Display?.DifferenceCountComplete ?? baseline.Status != "too_different", false, []));
                    continue;
                }
                var ta = Text(new(PageSpace.A, page.Number), a); var tb = Text(new(PageSpace.B, page.Number), b);
                adoptions.Add(PageFlowAdoption.Evaluate(actualSupport, page.Number, a, b, new(), new() { Enabled = true }, ta, tb));
                aliasAdoptions.Add(PageFlowAdoption.Evaluate(mapped, page.Number, a, b, new(), new() { Enabled = true }, ta, tb));
                using var comparison = mapped.Compare(page.Number, a, b); pages.Add(comparison.Describe());
                var surface = page.Built!.Surface!;
                Save(comparison.ContentA, $"p{page.Number}-C-A.png"); Save(comparison.ContentB, $"p{page.Number}-C-B.png");
                Save(comparison.Content.RawMask, $"p{page.Number}-C-raw.png"); Save(comparison.Display.Comparison.RawMask, $"p{page.Number}-D-raw.png");
                using var da = surface.DisplayMap.Render(a, PageSpace.A); using var db = surface.DisplayMap.Render(b, PageSpace.B);
                Save(da, $"p{page.Number}-D-A.png"); Save(db, $"p{page.Number}-D-B.png");
                projections.Add(new { page = page.Number, comparison.Content.RawPixels, content_clusters = comparison.Content.Clusters,
                    structures = comparison.Display.StructuralChanges, content_map = surface.ContentMap.Segments, display_map = surface.DisplayMap.Segments,
                    surface.Pieces, omitted = surface.OmittedBands });
                foreach (var pair in numeric.Pairs.Where(p => p.A.Page == page.Number))
                {
                    var sa = surface.ContentMap.Segments.Single(s => s.AStart <= pair.A.Top && s.AStart + s.Length >= pair.A.Top + pair.A.Height);
                    var sb = surface.ContentMap.Segments.Single(s => s.BStart <= pair.B.Top && s.BStart + s.Length >= pair.B.Top + pair.B.Height);
                    var ca = sa.CanvasStart + pair.A.Top - sa.AStart!.Value; var cb = sb.CanvasStart + pair.B.Top - sb.BStart!.Value;
                    Require(ca == cb && pair.A.Height == pair.B.Height, "数値行の全幅帯がCへ残る");
                    using var roi = new Mat(comparison.Content.RawMask, new Rect(0, ca, comparison.Content.RawMask.Width, pair.A.Height));
                    numericRanges.Add(new { pair, content_top = ca, raw_pixels = Cv2.CountNonZero(roi),
                        clusters = comparison.Content.Clusters.Count(c => c.Bounds.Y < ca + pair.A.Height && c.Bounds.Bottom > ca) });
                }
            }
            var ready = mapped.Decision.Ready && adoptions.All(d => d.Accepted);
            var rows = Rows(mapped); var input = new Input(ready, false, rows, mapped.Links.Where(l => l.Status == "band_verified").ToArray(), pages);
            var candidate = PageFlowAggregation.Evaluate(input);
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var code = CliApplication.Run(["compare", pathA, pathB, "--out", Path.Combine(target, "cli"), "--config", config, "--quiet"], stdout, stderr);
            Require(code == 1, "製品CLI: " + stderr); File.WriteAllText(Path.Combine(target, "cli.log"), stdout + stderr.ToString());
            var report = JsonSerializer.Deserialize<ReportDocument>(File.ReadAllBytes(Path.Combine(target, "cli/result.json")), ReportJson.Options)!;
            var matched = numeric.Pairs.Count == scenario.Pairs && mapped.Decision.Ready == scenario.Gate && ready == scenario.Adopt
                && (candidate.Status == "grouped") == scenario.Grouped
                && (scenario.AliasAdopt is null || (mapped.Decision.Ready && aliasAdoptions.All(d => d.Accepted)) == scenario.AliasAdopt);
            records.Add(new { run = name, scenario.Id, reverse, expected = new { scenario.Pairs, scenario.Gate, scenario.Adopt, scenario.Grouped, scenario.AliasAdopt }, matched,
                numeric_input = numericInput, numeric, product_gate = product.Decision, mapping_gate = mapped.Decision, adoptions, alias_adoptions = aliasAdoptions,
                input, actual_rows = Rows(product), candidate, projections, numeric_ranges = numericRanges, nonflow = mapped.Nonflow.Proofs.Select(p => new { p.CandidateIndex, p.Source, p.Target }),
                original_comparisons = mapped.Verifications.Sum(v => v.OriginalComparisons), cli = new { code, report.Summary, page_flow = report.PageFlow!.Status },
                audit = NumericAudit.Run(numericInput, numeric) });
            File.WriteAllText(Path.Combine(output, "numeric.json"), JsonSerializer.Serialize(records, AggregationProbe.Json));
            Console.WriteLine($"{name}: pairs={numeric.Pairs.Count}, gate={mapped.Decision.Ready}, adopt={ready}, {candidate.DifferenceCount}→{candidate.AggregatedDifferenceCount}, {candidate.Status}/{candidate.Reason}, expected={matched}");
            Mat Read(PageFlowPageKey key) { using var image = (key.Side == PageSpace.A ? pdfA : pdfB).ReadPage(key.Page, 300); return image.Pixels.Clone(); }
            RowTextResult Text(PageFlowPageKey key, Mat image) => (key.Side == PageSpace.A ? textA : textB).ReadRowWords(key.Page, image.Size(), 300);
            void Save(Mat image, string path) => File.WriteAllBytes(Path.Combine(target, path), image.ImEncode(".png"));
            void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(name + ": " + message); }
        }
    }
    private static Row[] Rows(PageFlowPlan plan) => plan.Inference.Layouts.A.Concat(plan.Inference.Layouts.B).SelectMany(l => l.Body.Select((line, i) =>
        new Row(l.Page.Key.Side, l.Page.Key.Page, l.BodyStart + i * l.Pitch, l.Pitch, line.Text))).ToArray();
}
