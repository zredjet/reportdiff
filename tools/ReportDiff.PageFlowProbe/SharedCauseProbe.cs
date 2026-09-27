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
internal static class SharedCauseProbe
{
    internal sealed record Case(string Id, string[][] A, string[][] B, string Mutation, bool Grouped, int? Aggregate,
        FlowLayout? Layout = null, IReadOnlySet<string>? FaintRows = null);
    internal static void Run(string output) => RunCases(output, Fixtures());
    internal static void RunCases(string output, IReadOnlyList<Case> cases, bool supportDiagnostics = false)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("出力先が空ではありません。");
        Directory.CreateDirectory(output);
        var records = new List<object>(); var manifests = new List<object>(); var supports = new List<object>();
        var config = Path.Combine(output, "carry.yaml"); File.WriteAllText(config, "rows: {enabled: true, carry_enabled: true}\nreport: {raw_overlay: true}\n");
        foreach (var scenario in cases)
        {
            var folder = Path.Combine(output, scenario.Id); Directory.CreateDirectory(folder);
            foreach (var (side, rows) in new[] { ("a", scenario.A), ("b", scenario.B) })
            {
                var bytes = FlowFixture.Create(new(scenario.Id, rows.Sum(r => r.Length), scenario.Mutation), side == "b",
                    scenario.Layout, explicitPages: rows, faintRows: scenario.FaintRows);
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
                var plan = productPlan;
                if (supportDiagnostics)
                {
                    supports.Add(SamePageSupportProbe.Describe(name, plan));
                    File.WriteAllText(Path.Combine(output, "support.json"), JsonSerializer.Serialize(supports, AggregationProbe.Json));
                }
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
                var legacy = PageFlowAggregation.Evaluate(input);
                using var stdout = new StringWriter(); using var stderr = new StringWriter();
                var cli = CliApplication.Run(["compare", pathA, pathB, "--out", Path.Combine(target, "cli"), "--config", config, "--quiet"], stdout, stderr);
                File.WriteAllText(Path.Combine(target, "cli.log"), stdout + stderr.ToString());
                Require(cli == 1, "CLIの相違終了コード: " + stderr);
                var report = JsonSerializer.Deserialize<ReportDocument>(File.ReadAllBytes(Path.Combine(target, "cli", "result.json")), ReportJson.Options)!;
                Require(input.GateReady
                    ? report.Summary.DifferenceCount == legacy.DifferenceCount && report.Summary.AggregatedDifferenceCount == legacy.AggregatedDifferenceCount
                    : report.PageFlow!.Status == "skipped" && report.Summary.DifferenceCount == report.Summary.AggregatedDifferenceCount,
                    "採用時は候補内訳と製品件数が一致し、未採用時は製品の基準比較を維持する");
                records.Add(new { run = name, scenario.Id, reverse, hypothesis = new { scenario.Grouped, scenario.Aggregate },
                    input, legacy, gate = plan.Decision, adoptions, projections,
                    proposals = plan.Inference.Proposals, links = plan.Links,
                    page_maps = plan.Pages.Select(p => new { p.Number, p.Built?.Status, p.Built?.Reason }),
                    original_comparisons = plan.Verifications.Sum(v => v.OriginalComparisons),
                    cli = new { code = cli, report.Summary, page_flow = report.PageFlow!.Status } });
                Console.WriteLine($"{name}: gate={input.GateReady}, candidate={legacy.DifferenceCount}→{legacy.AggregatedDifferenceCount}, "
                    + $"product={report.Summary.DifferenceCount}→{report.Summary.AggregatedDifferenceCount}, {legacy.Status}/{legacy.Reason}");
                File.WriteAllText(Path.Combine(output, "shared-causes.json"), JsonSerializer.Serialize(records, AggregationProbe.Json));
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
        Add("shared-two", 10, [(2, 1), (8, 1)], true, 2);
        Add("shared-tone", 10, [(2, 1), (8, 1)], true, 3, "paired");
        Add("shared-chain", 16, [(2, 1), (8, 1)], true, 2);
        Add("same-page-two", 10, [(2, 1), (4, 1)], true, 2);
        Add("shared-three-limit", 15, [(2, 1), (7, 1), (12, 1)], false, null);
        Add("shared-block-limit", 14, [(2, 2), (8, 2)], false, null);
        Add("adjacent-single", 10, [(2, 2)], true, 1);
        Add("merged-endpoint", 10, [(2, 1), (5, 1)], false, null);
        Add("shared-mixed", 10, [(2, 1)], false, null, remove: true);
        Add("shared-repeated", 10, [(2, 1), (8, 1)], false, null, repeated: true);
        Add("shared-band-change", 10, [(2, 1), (8, 1)], false, null, "pixels");
        var left = Build("FIRST", 10, [(2, 1), (8, 1)]);
        var right = Build("SECOND", 10, [(2, 1)]);
        cases.Add(new("shared-and-independent", left.A.Concat(right.A).ToArray(), left.B.Concat(right.B).ToArray(), "none", true, 3));
        cases.Add(new("independent-and-shared", right.A.Concat(left.A).ToArray(), right.B.Concat(left.B).ToArray(), "none", true, 3));
        return cases.ToArray();

        void Add(string id, int count, (int Before, int Count)[] changes, bool grouped, int? aggregate,
            string mutation = "none", bool remove = false, bool repeated = false)
        {
            var pair = Build("ROW", count, changes, remove, repeated);
            cases.Add(new(id, pair.A, pair.B, mutation, grouped, aggregate));
        }
        static (string[][] A, string[][] B) Build(string prefix, int count, (int Before, int Count)[] changes,
            bool remove = false, bool repeated = false)
        {
            var a = Enumerable.Range(0, count).Select(i => $"{prefix} ITEM {(char)('A' + i)}{(char)('A' + i)}").ToArray();
            if (repeated) a[7] = a[6];
            var b = new List<string>();
            for (var i = 0; i < count; i++)
            {
                foreach (var change in changes.Where(c => c.Before == i))
                    for (var n = 0; n < change.Count; n++) b.Add($"{prefix} ADDED {(char)('A' + i)} {(char)('A' + n)}");
                if (!remove || i != 8) b.Add(a[i]);
            }
            return (a.Chunk(6).ToArray(), b.Chunk(6).ToArray());
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
