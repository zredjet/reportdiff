using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using static ReportDiff.Core.PageFlowInference;

/// <summary>固定PDFとメモリ上の反例を使う事前検証。製品CLIへの接続は行わない。</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
internal static class CrossPageSupportProbe
{
    private sealed record Case(string Id, string Variant, bool Reverse, bool Expected);
    internal static void Run(string root, string output)
    {
        if (Directory.Exists(output)) throw new ArgumentException("出力先が既に存在します。");
        Directory.CreateDirectory(output);
        var fixtures = Path.Combine(root, "tests/ReportDiff.Tests/Fixtures/page-flow-same-page-support");
        using var geometry = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures, "geometry.json")));
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(fixtures, "sha256.json")))!;
        var cases = geometry.RootElement.EnumerateArray().SelectMany(g => new[] { false, true }.Select(reverse =>
            new Case(g.GetProperty("id").GetString()!, "original", reverse, g.GetProperty("grouped").GetBoolean()))).ToList();
        foreach (var variant in new[] { "content-tone", "carry-tone", "thin-next", "one-ink-next", "flat-next", "selection", "max-shift" })
        foreach (var reverse in new[] { false, true }) cases.Add(new("same-page-two", variant, reverse, variant == "content-tone"));
        var records = new List<object>();
        foreach (var c in cases)
        {
            var name = c.Id + (c.Variant == "original" ? "" : "-" + c.Variant) + (c.Reverse ? "-ba" : "-ab");
            var folder = Path.Combine(output, name); Directory.CreateDirectory(folder);
            var pathA = Path.Combine(fixtures, c.Id, c.Reverse ? "b.pdf" : "a.pdf");
            var pathB = Path.Combine(fixtures, c.Id, c.Reverse ? "a.pdf" : "b.pdf");
            foreach (var path in new[] { pathA, pathB }) Require(Hash(File.ReadAllBytes(path)) == hashes[Path.GetRelativePath(fixtures, path)], "固定PDFハッシュ");
            using var pdfA = PdfReader.Open(pathA); using var pdfB = PdfReader.Open(pathB);
            using var textA = new PdfTextReader(pathA); using var textB = new PdfTextReader(pathB);
            var keys = Enumerable.Range(1, 2).SelectMany(p => new[] { new PageFlowPageKey(PageSpace.A, p), new(PageSpace.B, p) }).ToArray();
            var collector = new PageFlowCollector(keys);
            foreach (var key in keys)
            {
                using var image = Read(key); Save(image, $"p{key.Page}-O-{key.Side}.png");
                Require(collector.Add(key, image, Text(key, image), .5), "記述収集");
            }
            var document = collector.Complete()!; var parameters = new ComparisonParameters();
            var options = new RowOptions { Enabled = true, MaxShiftMm = c.Variant == "max-shift" ? 10 : 20 };
            var limited = c.Variant == "selection";
            var product = PageFlowPlan.Prepare(document, Read, parameters, options, limited);
            var layouts = product.Inference.Layouts.A.Concat(product.Inference.Layouts.B).ToArray();
            var evidence = product.Decision.Ready ? new CrossPageSupportEvidence.Result("not_needed", null)
                : CrossPageSupportEvidence.Find(layouts, options, 300, limited);
            var plan = product; object? support = null; var attempts = new List<object>();
            if (evidence.Evidence is { } proof)
            {
                Require(CrossPageSupportEvidence.Matches(layouts, options, proof), "証拠の全フィールドの再計算");
                // 支持の画素評価は送り帯の外だけ。既存検証器を変更せず診断内の反射で呼ぶ。
                var supportA = proof.SourceSupport[0].Page.Side == PageSpace.A ? proof.SourceSupport : proof.TargetSupport;
                var supportB = proof.SourceSupport[0].Page.Side == PageSpace.A ? proof.TargetSupport : proof.SourceSupport;
                var linesA = SupportLines(supportA); var linesB = SupportLines(supportB);
                using (var a = Read(new(PageSpace.A, 2)))
                using (var b = Read(new(PageSpace.B, 2)))
                {
                    var validator = typeof(RowOptions).Assembly.GetType("ReportDiff.Core.RowGroupValidator")!.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)!;
                    var evaluated = validator.Invoke(null, [a, b, linesA, linesB, Enumerable.Range(0, linesA.Length).Select(i => new RowMatch(i, i)).ToArray(), parameters, options])!;
                    support = new { lines_a = linesA, lines_b = linesB, result = JsonSerializer.SerializeToElement(evaluated, evaluated.GetType(), AggregationProbe.Json) };
                }
                var proposal = proof.Candidate;
                PageFlowBandVerifier.Verification verification;
                using (var a = Read(proposal.Source!.Page))
                using (var b = Read(proposal.Target!.Page))
                    verification = PageFlowBandVerifier.Verify(proposal, document.Pages.Single(p => p.Key == proposal.Source.Page),
                        document.Pages.Single(p => p.Key == proposal.Target.Page), a, b, parameters);
                var link = verification.Proposal; var pages = new List<PageFlowPlan.Page>();
                foreach (var number in new[] { 1, 2 })
                {
                    using var a = Read(new(PageSpace.A, number)); using var b = Read(new(PageSpace.B, number));
                    var built = CrossPageSupportSurface.Create(layouts.Single(l => l.Page.Key == new PageFlowPageKey(PageSpace.A, number)),
                        layouts.Single(l => l.Page.Key == new PageFlowPageKey(PageSpace.B, number)), a, b, [link], parameters,
                        link.Status == "band_verified" ? [proof.Cause] : [],
                        (map, kinds) => attempts.Add(new { page = number, segments = map.Segments, kinds }));
                    pages.Add(new(number, built, false));
                }
                var gate = PageFlowRangeGate.Evaluate(document, true, null, [new(link.Source, link.Target, link.Status == "band_verified", link.Reason)],
                    pages.Select(p => new PageFlowPageProof(p.Number, p.Built!.Status == "built",
                        p.Built.Removed.Where(r => r.Proof == "verified_carry_range").Select(r => r.Band).ToArray())).ToArray());
                var constructor = typeof(PageFlowPlan).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
                plan = (PageFlowPlan)constructor.Invoke([document, new[] { 1, 2 }.ToDictionary(n => n, _ => parameters),
                    product.Inference with { Proposals = new[] { proposal } }, new[] { verification }, new[] { link }, pages.ToArray(), gate]);
            }
            var adoptions = new List<PageFlowAdoptionResult>(); var described = new List<PageFlowAggregation.Page>(); var projections = new List<object>();
            foreach (var p in plan.Pages)
            {
                using var a = Read(new(PageSpace.A, p.Number)); using var b = Read(new(PageSpace.B, p.Number));
                if (!plan.Decision.Ready)
                {
                    using var baseline = RowComparer.Compare(a, b, parameters, options, () => (Text(new(PageSpace.A, p.Number), a), Text(new(PageSpace.B, p.Number), b)));
                    described.Add(new(p.Number, true, baseline.Comparison.Clusters.Count, baseline.DifferenceCount,
                        baseline.Display?.DifferenceCountComplete ?? baseline.Status != "too_different", false, [])); continue;
                }
                adoptions.Add(PageFlowAdoption.Evaluate(plan, p.Number, a, b, parameters, options, Text(new(PageSpace.A, p.Number), a), Text(new(PageSpace.B, p.Number), b)));
                using var compared = plan.Compare(p.Number, a, b); described.Add(compared.Describe());
                Save(compared.ContentA, $"p{p.Number}-C-A.png"); Save(compared.ContentB, $"p{p.Number}-C-B.png");
                Save(compared.Content.RawMask, $"p{p.Number}-C-raw.png"); Save(compared.Display.Comparison.RawMask, $"p{p.Number}-D-raw.png");
                var surface = p.Built!.Surface!;
                using var da = surface.DisplayMap.Render(a, PageSpace.A); using var db = surface.DisplayMap.Render(b, PageSpace.B);
                Save(da, $"p{p.Number}-D-A.png"); Save(db, $"p{p.Number}-D-B.png");
                projections.Add(new { page = p.Number, compared.Content.RawPixels, content_clusters = compared.Content.Clusters,
                    structures = compared.Display.StructuralChanges, content_map = surface.ContentMap.Segments,
                    display_map = surface.DisplayMap.Segments, surface.Pieces, p.Built.Removed });
            }
            var rows = CrossPageSupportEvidence.Rows(layouts);
            var input = new PageFlowAggregation.Input(plan.Decision.Ready && adoptions.All(a => a.Accepted), limited,
                rows.Select(r => new PageFlowAggregation.Row(r.Page.Side, r.Page.Page, r.Top, r.Height, r.Text)).ToArray(),
                plan.Links.Where(l => l.Status == "band_verified").ToArray(), described);
            var decision = PageFlowAggregation.Evaluate(input);
            records.Add(new { run = name, c.Id, c.Variant, c.Reverse, expected = c.Expected, options, rows,
                layouts = layouts.Select(l => new { l.Page.Key, l.Pitch, l.BodyStart, l.BodyEnd, l.Regular, l.Body }),
                product = new { product.Decision, product.Inference.Proposals, product.Links }, evidence, support,
                gate = plan.Decision, adoptions, input, decision, projections, links = plan.Links,
                maps = plan.Pages.Select(p => new { p.Number, p.Built?.Status, p.Built?.Reason }), attempts,
                original_comparisons = plan.Verifications.Sum(v => v.OriginalComparisons),
                audit = CrossPageSupportAudit.Run(layouts, options, evidence) });
            File.WriteAllText(Path.Combine(output, "cross-page-support.json"), JsonSerializer.Serialize(records, AggregationProbe.Json));
            Console.WriteLine($"{name}: 証拠={evidence.Status}/{evidence.Reason}, 範囲={plan.Decision.Ready}, "
                + $"診断={decision.DifferenceCount}→{decision.AggregatedDifferenceCount}, {decision.Status}/{decision.Reason}, "
                + $"採用={string.Join(',', adoptions.Select(a => a.Accepted ? "可" : a.Reason + "/" + a.Detail))}");

            Mat Read(PageFlowPageKey key)
            {
                using var rendered = (key.Side == PageSpace.A ? pdfA : pdfB).ReadPage(key.Page, 300);
                var image = rendered.Pixels.Clone(); var edited = key.Side == (c.Reverse ? PageSpace.A : PageSpace.B);
                if (c.Variant == "content-tone" && edited && key.Page == 1) Tone(3, 166);
                if (c.Variant == "carry-tone" && edited && key.Page == 2) Tone(0, 166);
                if (key.Page == 2 && c.Variant is "thin-next" or "one-ink-next" or "flat-next")
                {
                    var start = edited ? 2 : 0;
                    for (var i = 0; i < 4; i++)
                    {
                        if (c.Variant == "one-ink-next" && i == 0) continue;
                        if (c.Variant == "flat-next") { using var band = new Mat(image, new Rect(0, 300 + (start + i) * 100, image.Width, 100)); band.SetTo(Scalar.Black); }
                        else Tone(start + i, 248);
                    }
                }
                return image;
                void Tone(int slot, int value)
                { using var band = new Mat(image, new Rect(0, 300 + slot * 100, image.Width, 100)); band.ConvertTo(band, MatType.CV_8UC3, (255 - value) / 255.0, value); }
            }
            RowTextResult Text(PageFlowPageKey key, Mat image) => (key.Side == PageSpace.A ? textA : textB).ReadRowWords(key.Page, image.Size(), 300);
            RowLine[] SupportLines(IReadOnlyList<SharedInferenceDiagnosis.Line> selected) => selected.Select(r =>
            {
                var line = layouts.Single(l => l.Page.Key == r.Page).Body.Single(l => l.Text == r.Text);
                return new RowLine([new(line.Text, line.Bounds, [line.Baseline])], line.Bounds, line.Baseline);
            }).ToArray();
            void Save(Mat image, string file) => File.WriteAllBytes(Path.Combine(folder, file), image.ImEncode(".png"));
            void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(name + ": " + label); }
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
