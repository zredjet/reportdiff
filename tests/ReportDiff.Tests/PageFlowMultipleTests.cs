using System.Runtime.Versioning;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed partial class PageFlowCliTests
{
    public static IEnumerable<object[]> MultipleCases()
    {
        var cases = new[] { ("independent-inserts",10,2), ("independent-opposite",10,2), ("independent-chain",13,2),
            ("independent-three",15,3), ("neutral-between",10,2), ("independent-tone",11,3), ("nonflow-terminal-tone",11,3),
            ("nonflow-terminal-number",11,3), ("independent-band-change",18,18), ("independent-number-change",11,3),
            ("repeated-across-components",18,18), ("one-independent-one-shared",12,3), ("page-local-second-cause",7,7), ("shared-two-inserts",7,2) };
        return cases.SelectMany(c => new[] { false, true }.Select(reverse => new object[] { c.Item1, reverse, c.Item2, c.Item3 }));
    }

    [Theory]
    [MemberData(nameof(MultipleCases))]
    public void Multiple_causes_use_fixed_independent_counts_and_keep_raw_evidence(string scenario, bool reverse, int count, int aggregate)
    {
        using var files = new Files("multiple-" + scenario, reverse);
        var run = files.Run("on", true); Assert.Equal(1, run.Code); Assert.Empty(run.Error);
        var report = files.Report("on"); var flow = report.PageFlow!;
        Assert.Equal(count, report.Summary.DifferenceCount); Assert.Equal(aggregate, report.Summary.AggregatedDifferenceCount);
        Assert.True(report.Summary.AggregatedDifferenceCountComplete);
        Assert.Equal(count != 18 ? "applied" : "skipped", flow.Status);
        Assert.Equal(count != aggregate ? "grouped" : "skipped", flow.Aggregation.Status);
        Assert.Equal(flow.Links.Select(l => l.Id).Distinct().Count(), flow.Links.Count);
        var assigned = flow.Aggregation.Groups.SelectMany(g => g.Structures).ToArray();
        Assert.Equal(assigned.Length, assigned.Distinct().Count());
        foreach (var group in flow.Aggregation.Groups)
        {
            Assert.All(group.Links, id => Assert.Equal("carried", flow.Links.Single(l => l.Id == id).Status));
            Assert.All(group.Structures, r => Assert.Contains(report.Pages.Single(p => p.Page == r.Page).RowAlignment.StructuralChanges, s => s.Id == r.StructuralChangeId));
        }
        foreach (var link in flow.Links.Where(l => l.Nonflow is not null))
        {
            Assert.Equal("skipped", link.Status); Assert.Equal("not_performed", link.ImageStatus);
            Assert.Equal(PageFlowNonflow.Reason, link.Reason); Assert.Equal(0, link.OriginalComparisons);
            Assert.True(link.Nonflow!.DocumentTextUniqueAndOrdered); Assert.True(link.Nonflow.NoCommonRowCrossesBoundary);
            Assert.False(link.Nonflow.PixelEqualityProven);
            foreach (var pair in link.Nonflow.SourceRows.Concat(link.Nonflow.TargetRows))
            {
                Assert.Equal(pair.Row.Page, pair.Counterpart.Page); Assert.NotEqual(pair.Row.Side, pair.Counterpart.Side);
                Assert.Equal(pair.Row.BoundsPx.H, pair.Counterpart.BoundsPx.H);
                Assert.Equal("original_top_left", pair.Counterpart.CoordinateSystem);
            }
        }
        if (scenario is "independent-inserts" or "nonflow-terminal-tone") Assert.Contains(flow.Links, l => l.Nonflow is not null);
        if (scenario == "nonflow-terminal-tone") { Assert.Equal(2850, report.Pages[1].RawPixels); Assert.Single(report.Pages[1].Clusters); }
        var html = File.ReadAllText(files.PathOf("on", "report.html"));
        if (flow.Links.Any(l => l.Nonflow is not null)) Assert.Contains("この帯の内容比較は続けています", html);
        Assert.Equal(1, files.Run("off", false).Code); var off = files.Report("off");
        foreach (var (p, q) in report.Pages.Zip(off.Pages))
        foreach (var (onPath, offPath) in new[] { (p.RawEvidence!.A.Image, q.RawEvidence!.A.Image), (p.RawEvidence.B.Image, q.RawEvidence.B.Image), (p.RawEvidence.Overlay, q.RawEvidence.Overlay) })
            Assert.Equal(File.ReadAllBytes(files.PathOf("on", onPath)), File.ReadAllBytes(files.PathOf("off", offPath)));
        if (flow.Status == "skipped") Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
    }

    [Theory]
    [InlineData("1-4")] [InlineData("1-3")] [InlineData("1,3,4")]
    public void Multiple_nonflow_proof_is_disabled_for_explicit_page_selection(string selection)
    {
        using var files = new Files("multiple-independent-inserts");
        Assert.Equal(1, files.Run("selected", true, extraArgs: ["--pages", selection]).Code);
        var report = files.Report("selected"); Assert.Empty(report.PageFlow!.Aggregation.Groups);
        Assert.All(report.PageFlow.Links, l => Assert.Null(l.Nonflow));
        Assert.False(report.Summary.AggregatedDifferenceCountComplete);
    }

    [Fact]
    public void Multiple_input_mutation_keeps_old_output()
    {
        using var files = new Files("multiple-independent-inserts"); Directory.CreateDirectory(files.PathOf("changed"));
        File.WriteAllText(files.PathOf("changed", "old.txt"), "old"); var changed = false;
        using var writer = new HookWriter(line =>
        { if (!changed && line.StartsWith("処理中 1 /", StringComparison.Ordinal)) { File.AppendAllText(files.B, "\n% changed\n"); changed = true; } });
        var result = files.Run("changed", true, extraArgs: ["--force"], writer: writer);
        Assert.True(changed); Assert.Equal(2, result.Code); Assert.Contains("入力ファイルが変わりました", result.Error);
        Assert.Equal("old", File.ReadAllText(files.PathOf("changed", "old.txt")));
        Assert.False(File.Exists(files.PathOf("changed", "result.json"))); Assert.Empty(Directory.GetDirectories(files.Root, ".reportdiff-stage-*"));
    }

    [Theory]
    [InlineData("multiple-variable", false, 2)] [InlineData("multiple-down", true, 2)]
    [InlineData("multiple-opposite", false, 2)] [InlineData("multiple-up", true, 2)]
    [InlineData("multiple-terminal-tone", false, 3)] [InlineData("multiple-down", false, 2)]
    [InlineData("multiple-up", false, 2)]
    public void Multiple_global_flow_reports_same_page_counterparts_in_original_coordinates(string scenario, bool reverse, int aggregate)
    {
        using var files = new Files("multiple-" + scenario, reverse);
        Assert.Equal(1, files.Run("on", true, extraYaml: "align: {enabled: true}").Code);
        var report = files.Report("on"); var flow = report.PageFlow!;
        Assert.Equal("applied", flow.Status); Assert.Equal(aggregate, report.Summary.AggregatedDifferenceCount);
        Assert.Equal(aggregate == 3 ? 11 : 10, report.Summary.DifferenceCount); Assert.Equal(2, flow.Aggregation.Groups.Count);
        Assert.Contains(report.Pages, p => p.Alignment.Status == "applied");
        foreach (var link in flow.Links.Where(l => l.Nonflow is not null))
        foreach (var pair in link.Nonflow!.SourceRows.Concat(link.Nonflow.TargetRows))
        {
            var page = report.Pages.Single(p => p.Page == pair.Row.Page);
            Assert.Equal(pair.Row.Page, pair.Counterpart.Page);
            var a = pair.Row.Side == "a" ? pair.Row : pair.Counterpart;
            var b = pair.Row.Side == "b" ? pair.Row : pair.Counterpart;
            var relativeDy = b.BoundsPx.Y + (page.GlobalShiftPx?.Dy ?? 0) - a.BoundsPx.Y;
            Assert.Contains(relativeDy, new[] { -100, 0, 100 });
            Assert.Equal("original_top_left", b.CoordinateSystem);
        }
        Assert.Equal(1, files.Run("off", false, extraYaml: "align: {enabled: true}").Code);
        var off = files.Report("off");
        foreach (var (p, q) in report.Pages.Zip(off.Pages))
            Assert.Equal(File.ReadAllBytes(files.PathOf("on", p.RawEvidence!.Overlay)), File.ReadAllBytes(files.PathOf("off", q.RawEvidence!.Overlay)));
    }

    [Theory]
    [InlineData("", false, 3)]
    [InlineData("exclude: [{page: 2, x: 13, y: 54, w: 41, h: 3}]", true, 3)]
    [InlineData("regions: [{page: 2, name: terminal, mode: compare, x: 13, y: 54, w: 41, h: 3, diff: {max_shift_mm: 0, edge_tolerance: 0}}]", false, 3)]
    public void Multiple_nonflow_band_content_obeys_A_coordinate_settings(string yaml, bool disabled, int aggregate)
    {
        using var files = new Files("multiple-nonflow-terminal-tone");
        Assert.Equal(1, files.Run("on", true, extraArgs: disabled ? ["--no-regions"] : [], extraYaml: yaml).Code);
        var report = files.Report("on"); Assert.Equal("applied", report.PageFlow!.Status);
        Assert.Equal(aggregate, report.Summary.AggregatedDifferenceCount); Assert.Equal(aggregate - 2, report.Summary.Clusters);
        Assert.Contains(report.PageFlow.Links, l => l.Nonflow is not null);
    }

    [Theory]
    [InlineData("min_support_ink_mm2: 1000", "")]
    [InlineData("", "text: {max_words_per_page: 1}")]
    [InlineData("", "exclude: [{page: 1, x: 0, y: 0, w: 100, h: 110}]")]
    public void Multiple_failed_adoption_or_excluded_structure_never_partially_groups(string rows, string yaml)
    {
        using var files = new Files("multiple-independent-inserts");
        Assert.Equal(1, files.Run("on", true, rows, extraYaml: yaml).Code);
        var report = files.Report("on"); Assert.Empty(report.PageFlow!.Aggregation.Groups);
        Assert.Equal(report.Summary.DifferenceCount, report.Summary.AggregatedDifferenceCount);
    }

    [Theory]
    [InlineData("multiple-variable", true, "align: {enabled: true}", "fixed_parts_support")]
    [InlineData("multiple-opposite", true, "align: {enabled: true}", "fixed_parts_support")]
    [InlineData("multiple-terminal-tone", true, "align: {enabled: true}", "fixed_parts_support")]
    [InlineData("multiple-horizontal", false, "align: {enabled: true}", "global_alignment_applied")]
    [InlineData("nonflow-terminal-tone", false, "exclude: [{page: 2, x: 13, y: 54, w: 41, h: 3}]", "adoption_not_ready")]
    [InlineData("nonflow-terminal-tone", false, "exclude: [{page: 2, x: 14, y: 54, w: 2, h: 3}]", "adoption_not_ready")]
    public void Multiple_unsupported_combinations_keep_full_baseline(string scenario, bool reverse, string yaml, string reason)
    {
        using var files = new Files("multiple-" + scenario, reverse);
        Assert.Equal(1, files.Run("on", true, extraYaml: yaml).Code); Assert.Equal(1, files.Run("off", false, extraYaml: yaml).Code);
        var report = files.Report("on"); var off = files.Report("off");
        Assert.Contains(reason, report.PageFlow!.Reasons); Assert.Equal("skipped", report.PageFlow.Status);
        Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
        Assert.Equal(report.Summary.DifferenceCount, report.Summary.AggregatedDifferenceCount); Assert.Empty(report.PageFlow.Aggregation.Groups);
    }
}

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class PageFlowMultipleCoreTests
{
    [Fact]
    public void Fixed_independent_inputs_and_adversarial_membership_preserve_all_or_nothing()
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-multiple/aggregation.json")));
        foreach (var record in data.RootElement.EnumerateArray())
        {
            var input = record.GetProperty("input").Deserialize<PageFlowAggregation.Input>(ReportJson.Options)!;
            var expected = record.GetProperty("expected").Deserialize<PageFlowAggregation.Decision>(ReportJson.Options)!;
            var result = PageFlowAggregation.Evaluate(input);
            var shared = record.GetProperty("run").GetString()!.StartsWith("shared-two-inserts", StringComparison.Ordinal)
                || record.GetProperty("run").GetString()!.StartsWith("one-independent-one-shared", StringComparison.Ordinal);
            Assert.Equal(shared ? "grouped" : expected.Status, result.Status);
            Assert.Equal(shared ? (input.Pages.Count == 2 ? 2 : 3) : expected.AggregatedDifferenceCount, result.AggregatedDifferenceCount);
            if (result.Status != "grouped") continue;
            if (!shared) Assert.Equal(JsonSerializer.Serialize(expected.Groups, ReportJson.Options), JsonSerializer.Serialize(result.Groups, ReportJson.Options));
            var reversed = input with { Rows = input.Rows.Reverse().ToArray(), Links = input.Links.Reverse().ToArray(), Pages = input.Pages.Reverse().ToArray() };
            Assert.Equal(JsonSerializer.Serialize(result, ReportJson.Options), JsonSerializer.Serialize(PageFlowAggregation.Evaluate(reversed), ReportJson.Options));
            Reject(input with { GateReady = false }); Reject(input with { SelectionLimited = true });
            foreach (var link in input.Links) Reject(input with { Links = input.Links.Where(l => l != link).ToArray() });
            foreach (var page in input.Pages)
            {
                Reject(Patch(page with { Paired = false, UnpairedCovered = true }));
                var incomplete = PageFlowAggregation.Evaluate(Patch(page with { Complete = false }));
                Assert.False(incomplete.AggregatedDifferenceCountComplete); Assert.Equal(result.AggregatedDifferenceCount, incomplete.AggregatedDifferenceCount);
                var content = PageFlowAggregation.Evaluate(Patch(page with { Clusters = page.Clusters + 1, DifferenceCount = page.DifferenceCount + 1 }));
                Assert.Equal(result.AggregatedDifferenceCount + 1, content.AggregatedDifferenceCount);
                foreach (var structure in page.Structures)
                {
                    Reject(Patch(page with { Structures = page.Structures.Where(s => s != structure).ToArray(), DifferenceCount = page.DifferenceCount - 1 }));
                    Reject(Patch(page with { Structures = page.Structures.Append(structure).ToArray(), DifferenceCount = page.DifferenceCount + 1 }));
                    Reject(Patch(page with { Structures = page.Structures.Select(s => s == structure ? s with { Excluded = true } : s).ToArray(), DifferenceCount = page.DifferenceCount - 1 }));
                }
                PageFlowAggregation.Input Patch(PageFlowAggregation.Page p) => input with { Pages = input.Pages.Select(x => x.Number == p.Number ? p : x).ToArray() };
            }
        }
        static void Reject(PageFlowAggregation.Input input)
        {
            var result = PageFlowAggregation.Evaluate(input);
            Assert.Equal("skipped", result.Status); Assert.Empty(result.Groups); Assert.Equal(result.DifferenceCount, result.AggregatedDifferenceCount);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(-6)]
    public void Nonflow_proof_keeps_O_G_coordinates_and_respects_metadata_budget(int dy)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-multiple/independent-inserts");
        using var a = PdfReader.Open(Path.Combine(fixture, "a.pdf")); using var b = PdfReader.Open(Path.Combine(fixture, "b.pdf"));
        using var ta = new PdfTextReader(Path.Combine(fixture, "a.pdf")); using var tb = new PdfTextReader(Path.Combine(fixture, "b.pdf"));
        var keys = Enumerable.Range(1, 4).SelectMany(n => new[] { new PageFlowPageKey(PageSpace.A, n), new PageFlowPageKey(PageSpace.B, n) }).ToArray();
        var collector = new PageFlowCollector(keys);
        foreach (var key in keys)
        {
            using var image = Read(key); var text = (key.Side == PageSpace.A ? ta : tb).ReadRowWords(key.Page, image.Size(), 300);
            Assert.True(key.Side == PageSpace.B && dy != 0 ? collector.AddAligned(key, image, new(0, -dy), text, .5) : collector.Add(key, image, text, .5));
        }
        var document = collector.Complete()!; var plan = PageFlowPlan.Prepare(document, Read, new(), new() { Enabled = true });
        Assert.True(plan.Decision.Ready); var proof = Assert.Single(plan.Nonflow.Proofs);
        Assert.Equal(plan.Inference.Proposals.Count, plan.Links.Count); Assert.Equal(PageFlowNonflow.Reason, plan.Links[proof.CandidateIndex].Reason);
        Assert.Equal(document.Usage.DescriptorBytes + plan.Nonflow.DescriptorBytes, plan.Usage.DescriptorBytes);
        foreach (var row in proof.SourceRows.Concat(proof.TargetRows))
        {
            Assert.Equal(row.Row.Top + (row.Row.Page.Side == PageSpace.B ? dy : 0), row.OriginalRow.Top);
            Assert.Equal(row.Counterpart.Top + (row.Counterpart.Page.Side == PageSpace.B ? dy : 0), row.OriginalCounterpart.Top);
        }
        var exact = new PageFlowDocumentDescriptor(document.Pages.ToArray(), document.Usage with { DescriptorBytes = PageFlowLimits.MaximumDescriptorBytes - plan.Nonflow.DescriptorBytes });
        Assert.NotEmpty(PageFlowNonflow.Find(exact, plan.Inference, false).Proofs);
        var exceeded = new PageFlowDocumentDescriptor(document.Pages.ToArray(), exact.Usage with { DescriptorBytes = exact.Usage.DescriptorBytes + 1 });
        var failure = PageFlowPlan.Prepare(exceeded, Read, new(), new() { Enabled = true });
        Assert.False(failure.Decision.Ready); Assert.Contains("flow_descriptor_limit", failure.Decision.Reasons); Assert.Empty(failure.Nonflow.Proofs);
        Assert.Empty(PageFlowNonflow.Find(document, plan.Inference, true).Proofs);
        // 対応先の文字変更・欠落・反復は、画像や除外設定で救済しない。
        foreach (var match in proof.SourceRows.Concat(proof.TargetRows))
        foreach (var band in new[] { match.Row, match.Counterpart })
        foreach (var fault in new[] { "changed", "missing", "duplicate" })
        {
            PageFlowInference.Layout Change(PageFlowInference.Layout layout)
            {
                if (layout.Page.Key != band.Page) return layout;
                var index = (band.Top - layout.BodyStart) / layout.Pitch;
                var lines = layout.Body.ToList();
                if (fault == "changed") lines[index] = lines[index] with { Text = "UNMATCHED MODIFIED ROW" };
                if (fault == "missing") lines.RemoveAt(index);
                if (fault == "duplicate") lines.Add(lines[index]);
                return layout with { Body = lines };
            }
            var altered = plan.Inference with { Layouts = plan.Inference.Layouts with {
                A = plan.Inference.Layouts.A.Select(Change).ToArray(), B = plan.Inference.Layouts.B.Select(Change).ToArray() } };
            Assert.Empty(PageFlowNonflow.Find(document, altered, false).Proofs);
        }
        foreach (var proposal in plan.Links.Where(l => l.Status == "band_verified"))
            Assert.Empty(PageFlowNonflow.Find(document, plan.Inference with { Proposals = [proposal with { Status = "skipped", Reason = "text_mismatch" }] }, false).Proofs);
        var pageProofs = plan.Pages.Select(p => new PageFlowPageProof(p.Number, p.Built!.Status == "built", p.Built.Removed.Where(r => r.Proof == "verified_carry_range").Select(r => r.Band).ToArray())).ToArray();
        var candidates = plan.Links.Select(l => new PageFlowCandidate(l.Source, l.Target, l.Status == "band_verified", l.Reason)).ToArray();
        Assert.False(PageFlowRangeGate.Evaluate(document, true, null, candidates, pageProofs).Ready);
        Assert.False(PageFlowRangeGate.Evaluate(document, true, null, candidates, pageProofs, [proof, proof]).Ready);
        Assert.False(PageFlowRangeGate.Evaluate(new(document.Pages.ToArray(), document.Usage), true, null, candidates, pageProofs, [proof]).Ready);
        candidates[proof.CandidateIndex] = candidates[proof.CandidateIndex] with { Reason = "text_mismatch" };
        Assert.False(PageFlowRangeGate.Evaluate(document, true, null, candidates, pageProofs, [proof]).Ready);

        Mat Read(PageFlowPageKey key)
        {
            using var page = (key.Side == PageSpace.A ? a : b).ReadPage(key.Page, 300);
            return key.Side == PageSpace.B && dy != 0 ? PageMap.Global(page.Pixels.Size(), page.Pixels.Size(), page.Pixels.Size(), new(0, dy)).Render(page.Pixels, PageSpace.B) : page.Pixels.Clone();
        }
    }
}
