using System.Runtime.Versioning;
using System.Security.Cryptography;
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
internal static class MultipleCauseProbe
{
    private sealed record Case(string Id, string[][] A, string[][] B, string Mutation, bool Grouped, int? Aggregate);
    internal static void Run(string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("出力先が空ではありません。");
        Directory.CreateDirectory(output);
        var cases = Fixtures(); var records = new List<object>(); var manifests = new List<object>();
        var config = Path.Combine(output, "carry.yaml"); File.WriteAllText(config, "rows: {enabled: true, carry_enabled: true}\nreport: {raw_overlay: true}\n");
        foreach (var scenario in cases)
        {
            var folder = Path.Combine(output, scenario.Id); Directory.CreateDirectory(folder);
            foreach (var (side, rows) in new[] { ("a", scenario.A), ("b", scenario.B) })
            {
                var bytes = FlowFixture.Create(new(scenario.Id, rows.Sum(r => r.Length), scenario.Mutation), side == "b", explicitPages: rows);
                File.WriteAllBytes(Path.Combine(folder, side + ".pdf"), bytes);
                manifests.Add(new { scenario.Id, side, sha256 = Hash(bytes), pages = rows.Length, expected_rows = rows });
            }
            foreach (var reverse in new[] { false, true })
            {
                var name = scenario.Id + (reverse ? "-ba" : "-ab"); var target = Path.Combine(output, name); Directory.CreateDirectory(target);
                var pathA = Path.Combine(folder, reverse ? "b.pdf" : "a.pdf"); var pathB = Path.Combine(folder, reverse ? "a.pdf" : "b.pdf");
                using var pdfA = PdfReader.Open(pathA); using var pdfB = PdfReader.Open(pathB);
                using var textA = new PdfTextReader(pathA); using var textB = new PdfTextReader(pathB);
                var keys = Enumerable.Range(1, pdfA.PageCount).Select(p => new PageFlowPageKey(PageSpace.A, p))
                    .Concat(Enumerable.Range(1, pdfB.PageCount).Select(p => new PageFlowPageKey(PageSpace.B, p))).ToArray();
                var collector = new PageFlowCollector(keys);
                foreach (var key in keys)
                {
                    using var image = Read(key); Save(image, $"p{key.Page}-O-{key.Side}.png");
                    Require(collector.Add(key, image, Text(key, image), .5), "記述収集");
                }
                var document = collector.Complete() ?? throw new InvalidOperationException(collector.FailureReason);
                var productPlan = PageFlowPlan.Prepare(document, Read, new(), new() { Enabled = true });
                var nonflow = IndependentBoundary.Find(productPlan);
                var plan = IndependentBoundary.Recheck(productPlan, document, nonflow);
                var pages = new List<Page>(); var projections = new List<object>(); var adoptions = new List<PageFlowAdoptionResult>();
                foreach (var page in plan.Pages)
                {
                    Require(keys.Contains(new(PageSpace.A, page.Number)) && keys.Contains(new(PageSpace.B, page.Number)), "対応ページ入力");
                    using var a = Read(new(PageSpace.A, page.Number)); using var b = Read(new(PageSpace.B, page.Number));
                    if (plan.Decision.Ready)
                    {
                        var adoption = PageFlowAdoption.Evaluate(plan, page.Number, a, b, new(), new() { Enabled = true },
                            Text(new(PageSpace.A, page.Number), a), Text(new(PageSpace.B, page.Number), b));
                        adoptions.Add(adoption);
                        using var comparison = plan.Compare(page.Number, a, b); pages.Add(comparison.Describe());
                        Save(comparison.ContentA, $"p{page.Number}-C-A.png"); Save(comparison.ContentB, $"p{page.Number}-C-B.png");
                        Save(comparison.Content.RawMask, $"p{page.Number}-C-raw.png"); Save(comparison.Display.Comparison.RawMask, $"p{page.Number}-D-raw.png");
                        var surface = page.Built!.Surface!;
                        using var da = surface.DisplayMap.Render(a, PageSpace.A); using var db = surface.DisplayMap.Render(b, PageSpace.B);
                        Save(da, $"p{page.Number}-D-A.png"); Save(db, $"p{page.Number}-D-B.png");
                        projections.Add(new { page = page.Number, comparison.Content.RawPixels, content_clusters = comparison.Content.Clusters,
                            display_clusters = comparison.Display.Comparison.Clusters, structures = comparison.Display.StructuralChanges,
                            content_map = surface.ContentMap.Segments, display_map = surface.DisplayMap.Segments, surface.Pieces });
                    }
                    else
                    {
                        using var baseline = RowComparer.Compare(a, b, new(), new() { Enabled = true },
                            () => (Text(new(PageSpace.A, page.Number), a), Text(new(PageSpace.B, page.Number), b)));
                        pages.Add(new(page.Number, true, baseline.Comparison.Clusters.Count, baseline.DifferenceCount,
                            baseline.Display?.DifferenceCountComplete ?? baseline.Status != "too_different", false, []));
                    }
                }
                var rows = plan.Inference.Layouts.A.Concat(plan.Inference.Layouts.B).SelectMany(l => l.Body.Select((line, i) =>
                    new Row(l.Page.Key.Side, l.Page.Key.Page, l.BodyStart + i * l.Pitch, l.Pitch, line.Text))).ToArray();
                var input = new Input(plan.Decision.Ready && adoptions.All(a => a.Accepted), false, rows, plan.Links.Where(l => l.Status == "band_verified").ToArray(), pages);
                var legacy = PageFlowAggregation.Evaluate(input); var candidate = IndependentCauseAggregation.Evaluate(input);
                using var stdout = new StringWriter(); using var stderr = new StringWriter();
                var cli = CliApplication.Run(["compare", pathA, pathB, "--out", Path.Combine(target, "cli"), "--config", config, "--quiet"], stdout, stderr);
                File.WriteAllText(Path.Combine(target, "cli.log"), stdout + stderr.ToString());
                Require(cli == 1, "CLIの相違終了コード: " + stderr);
                var report = JsonSerializer.Deserialize<ReportDocument>(File.ReadAllBytes(Path.Combine(target, "cli", "result.json")), ReportJson.Options)!;
                if (nonflow.Count == 0 || productPlan.Decision.Ready)
                    Require(report.Summary.DifferenceCount == legacy.DifferenceCount && report.Summary.AggregatedDifferenceCount == legacy.AggregatedDifferenceCount,
                        "独立検証前の製品件数との一致");
                else
                {
                    Require(!productPlan.Decision.Ready && report.PageFlow!.Status == "skipped", "現製品は未検証候補を見送り");
                    var baselineCount = 0;
                    foreach (var page in productPlan.Pages)
                    {
                        using var a = Read(new(PageSpace.A, page.Number)); using var b = Read(new(PageSpace.B, page.Number));
                        using var baseline = RowComparer.Compare(a, b, new(), new() { Enabled = true },
                            () => (Text(new(PageSpace.A, page.Number), a), Text(new(PageSpace.B, page.Number), b)));
                        baselineCount += baseline.DifferenceCount;
                    }
                    Require(report.Summary.DifferenceCount == baselineCount && report.Summary.AggregatedDifferenceCount == baselineCount, "現製品の基準比較を維持");
                }
                var matched = (candidate.Status == "grouped") == scenario.Grouped
                    && (!scenario.Grouped || candidate.AggregatedDifferenceCount == scenario.Aggregate);
                records.Add(new { run = name, scenario.Id, reverse, expected = new { scenario.Grouped, scenario.Aggregate }, matched,
                    input, legacy, candidate, product_gate = productPlan.Decision, nonflow, gate = plan.Decision, adoptions, projections, original_comparisons = plan.Verifications.Sum(v => v.OriginalComparisons),
                    cli = new { code = cli, report.Summary, page_flow = report.PageFlow!.Status },
                    audit = MultipleCauseAudit.Run(input), boundary_audit = MultipleBoundaryAudit.Run(productPlan, rows, nonflow) });
                Console.WriteLine($"{name}: gate={input.GateReady}, {legacy.DifferenceCount}→{candidate.AggregatedDifferenceCount}, {candidate.Status}/{candidate.Reason}, 期待一致={matched}");
                File.WriteAllText(Path.Combine(output, "multiple-causes.json"), JsonSerializer.Serialize(records, AggregationProbe.Json));
                Mat Read(PageFlowPageKey key) { using var image = (key.Side == PageSpace.A ? pdfA : pdfB).ReadPage(key.Page, 300); return image.Pixels.Clone(); }
                RowTextResult Text(PageFlowPageKey key, Mat image) => (key.Side == PageSpace.A ? textA : textB).ReadRowWords(key.Page, image.Size(), 300);
                void Save(Mat image, string path) => File.WriteAllBytes(Path.Combine(target, path), image.ImEncode(".png"));
                void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(name + ": " + reason); }
            }
        }
        File.WriteAllText(Path.Combine(output, "fixtures.json"), JsonSerializer.Serialize(manifests, AggregationProbe.Json));
    }

    private static Case[] Fixtures()
    {
        var cases = new List<Case>();
        Add("independent-inserts", [10, 10], [false, false], true, 2);
        Add("independent-opposite", [10, 10], [false, true], true, 2);
        Add("independent-chain", [16, 10], [false, false], true, 2);
        Add("independent-three", [10, 10, 10], [false, false, true], true, 3);
        Add("neutral-between", [10, 10], [false, false], true, 2, neutral: true);
        Add("independent-tone", [10, 10], [false, false], true, 3, "paired");
        Add("nonflow-terminal-tone", [10, 10], [false, false], true, 3, "terminal-tone");
        Add("nonflow-terminal-number", [10, 10], [false, false], false, null, firstNumber: true);
        Add("independent-band-change", [10, 10], [false, false], false, null, "pixels");
        Add("independent-number-change", [10, 10], [false, false], false, null, number: true);
        Add("repeated-across-components", [10, 10], [false, false], false, null, repeated: true);
        Add("one-independent-one-shared", [10, 10], [false, false], false, null, extra: true);
        Add("page-local-second-cause", [10, 4], [false, false], false, null);
        var prior = new FlowCase("shared-two-inserts", 10);
        var shared = FlowFixture.Rows(prior, true).ToList(); shared.Insert(9, "SECOND ADDED");
        cases.Add(new(prior.Id, FlowFixture.Rows(prior, false).Chunk(6).ToArray(), shared.Chunk(6).ToArray(), "none", false, null));
        return cases.ToArray();

        void Add(string id, int[] lengths, bool[] deletes, bool grouped, int? aggregate, string mutation = "none", bool neutral = false,
            bool number = false, bool repeated = false, bool extra = false, bool firstNumber = false)
        {
            var a = new List<string[]>(); var b = new List<string[]>();
            for (var s = 0; s < lengths.Length; s++)
            {
                var prefix = repeated ? "SAME" : $"SECTION {(char)('A' + s)}";
                var original = Enumerable.Range(0, lengths[s]).Select(i => $"{prefix} ITEM {(char)('A' + i)}{(char)('A' + i)}").ToList();
                original[^1] = $"{prefix} TOTAL 555";
                var revised = original.ToList(); revised.Insert(2, $"{prefix} NEW ADDED");
                if (number && s == lengths.Length - 1 || firstNumber && s == 0) revised[^1] = $"{prefix} TOTAL 556";
                if (extra && s == lengths.Length - 1) revised.Insert(9, $"{prefix} SECOND ADDED");
                a.AddRange((deletes[s] ? revised : original).Chunk(6)); b.AddRange((deletes[s] ? original : revised).Chunk(6));
                if (neutral && s == 0) { string[] idle = ["NEUTRAL FIRST", "NEUTRAL NEXT", "NEUTRAL LAST"]; a.Add(idle); b.Add(idle); }
            }
            cases.Add(new(id, a.ToArray(), b.ToArray(), mutation, grouped, aggregate));
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
