using System.Runtime.Versioning;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using Xunit;
using static ReportDiff.Core.PageFlowAggregation;

namespace ReportDiff.Tests;

public sealed partial class PageFlowCliTests
{
    public static IEnumerable<object[]> TerminalCases()
    {
        using var records = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-unpaired/expectations.json")));
        foreach (var record in records.RootElement.EnumerateArray())
            yield return [record.GetProperty("id").GetString()!, record.GetProperty("reverse").GetBoolean(),
                record.GetProperty("candidate").GetProperty("difference_count").GetInt32(),
                record.GetProperty("candidate").GetProperty("aggregated_difference_count").GetInt32(),
                record.GetProperty("candidate").GetProperty("status").GetString() == "grouped"];
    }

    [Theory]
    [MemberData(nameof(TerminalCases))]
    public void Terminal_pdf_cases_preserve_content_unpaired_state_and_original_evidence(string scenario, bool reverse, int total, int aggregate, bool grouped)
    {
        using var files = new Files("unpaired-" + scenario, reverse);
        var on = files.Run("on", true); Assert.Equal(1, on.Code); Assert.Empty(on.Error);
        var report = files.Report("on"); var flow = report.PageFlow!;
        Assert.Equal(total, report.Summary.DifferenceCount); Assert.Equal(aggregate, report.Summary.AggregatedDifferenceCount);
        Assert.Equal(grouped, flow.Aggregation.Status == "grouped");
        Assert.False(report.Summary.DifferenceCountComplete); Assert.False(report.Summary.AggregatedDifferenceCountComplete);
        Assert.All(report.Pages.Where(p => p.Status.StartsWith("only_in_", StringComparison.Ordinal)), p =>
        { Assert.Empty(p.Clusters); Assert.Empty(p.RowAlignment.StructuralChanges); Assert.False(p.DifferenceCountComplete); });
        if (grouped && scenario is not ("single-r11" or "single-chain"))
        {
            Assert.Equal("applied", flow.Status); Assert.Equal(2, flow.Aggregation.Groups.Count);
            Assert.Single(flow.Aggregation.Groups.SelectMany(g => g.AuxiliaryBands));
            Assert.Equal(11, flow.Aggregation.Groups.Sum(g => g.Structures.Count));
            var proof = Assert.Single(flow.Links, l => l.Nonflow is not null);
            using var fixedCases = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-unpaired/expectations.json")));
            var expectedId = fixedCases.RootElement.EnumerateArray().Single(r => r.GetProperty("id").GetString() == scenario
                && r.GetProperty("reverse").GetBoolean() == reverse).GetProperty("nonflow_ids")[0].GetInt32();
            Assert.Equal(expectedId, proof.Id); Assert.Equal("skipped", proof.Status); Assert.Equal("not_performed", proof.ImageStatus);
            Assert.False(proof.Nonflow!.PixelEqualityProven); Assert.Equal(0, proof.OriginalComparisons);
            Assert.All(proof.Nonflow.SourceRows.Concat(proof.Nonflow.TargetRows), r => Assert.Equal(r.Row.Page, r.Counterpart.Page));
            Assert.All(flow.Aggregation.Groups.SelectMany(g => g.Structures), r =>
                Assert.Contains(report.Pages.Single(p => p.Page == r.Page).RowAlignment.StructuralChanges, s => s.Id == r.StructuralChangeId));
            Assert.All(flow.Aggregation.Groups.SelectMany(g => g.AuxiliaryBands), r =>
            { Assert.Empty(r.Structures); Assert.NotNull(r.Image); Assert.True(File.Exists(files.PathOf("on", r.Image))); });
            if (scenario == "independent-tone")
            { Assert.Equal(2462, report.Pages.Sum(p => p.RawPixels)); Assert.Equal(1, report.Summary.Clusters); }
            else Assert.Equal(0, report.Summary.Clusters);
        }
        Assert.Equal(1, files.Run("off", false).Code); var off = files.Report("off");
        foreach (var (p, q) in report.Pages.Zip(off.Pages))
        foreach (var (current, baseline) in new[] { (p.RawEvidence!.A.Image, q.RawEvidence!.A.Image),
            (p.RawEvidence.B.Image, q.RawEvidence.B.Image), (p.RawEvidence.Overlay, q.RawEvidence.Overlay) })
            Assert.Equal(File.ReadAllBytes(files.PathOf("off", baseline)), File.ReadAllBytes(files.PathOf("on", current)));
        if (flow.Status == "skipped") Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
    }

    [Theory]
    [InlineData("", "", "--profile", "strict")]
    [InlineData("", "", "--profile", "loose")]
    [InlineData("", "", "--dpi", "150")]
    [InlineData("", "", "--pages", "1-5")]
    [InlineData("", "", "--pages", "1,3,5")]
    [InlineData("", "align: {enabled: true}", "", "")]
    [InlineData("", "exclude: [{page: 1, x: 1, y: 1, w: 1, h: 1}]", "", "")]
    [InlineData("", "regions: [{page: 1, name: test, mode: compare, x: 1, y: 1, w: 1, h: 1}]", "", "")]
    [InlineData("min_support_bands: 3", "", "", "")]
    [InlineData("max_segments: 1", "", "", "")]
    [InlineData("", "text: {max_words_per_page: 1}", "", "")]
    public void Terminal_unsupported_settings_and_selection_keep_all_baseline_pages(string rows, string yaml, string flag, string value)
    {
        using var files = new Files("unpaired-independent-terminal"); string[] args = flag.Length == 0 ? [] : [flag, value];
        Assert.Equal(1, files.Run("on", true, rows, args, extraYaml: yaml).Code);
        Assert.Equal(1, files.Run("off", false, rows, args, extraYaml: yaml).Code);
        var on = files.Report("on"); var off = files.Report("off");
        Assert.Equal("skipped", on.PageFlow!.Status); Assert.Empty(on.PageFlow.Aggregation.Groups);
        Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(on.Pages, ReportJson.Options));
        Assert.Equal(on.Summary.DifferenceCount, on.Summary.AggregatedDifferenceCount);
        Assert.False(on.Summary.AggregatedDifferenceCountComplete);
    }

    [Fact]
    public void Terminal_equivalent_values_and_disabled_regions_remain_supported()
    {
        using var files = new Files("unpaired-independent-terminal");
        Assert.Equal(1, files.Run("explicit", true, extraYaml: "diff: {max_shift_mm: 0.15, color_threshold: 3, edge_tolerance: 0.3}\nink: {background_radius_mm: 1.5, contrast_threshold: 25}").Code);
        Assert.Equal(2, files.Report("explicit").Summary.AggregatedDifferenceCount);
        Assert.Equal(1, files.Run("disabled-region", true, extraArgs: ["--no-regions"],
            extraYaml: "regions: [{page: 1, name: ignored, mode: compare, x: 1, y: 1, w: 1, h: 1}]").Code);
        Assert.Equal(2, files.Report("disabled-region").Summary.AggregatedDifferenceCount);
    }

    [Fact]
    public void Terminal_content_rejects_stronger_improvement_without_losing_baseline()
    {
        using var files = new Files("unpaired-independent-tone");
        Assert.Equal(1, files.Run("on", true, "min_improvement: 1").Code);
        Assert.Equal(1, files.Run("off", false, "min_improvement: 1").Code);
        var on = files.Report("on"); Assert.Equal("skipped", on.PageFlow!.Status);
        Assert.Contains(on.PageFlow.Pages, p => p.Adoption?.Reason == "low_improvement");
        Assert.Equal(JsonSerializer.Serialize(files.Report("off").Pages, ReportJson.Options), JsonSerializer.Serialize(on.Pages, ReportJson.Options));
    }

    [Fact]
    public void Terminal_input_change_keeps_previous_report()
    {
        using var files = new Files("unpaired-independent-terminal");
        Directory.CreateDirectory(files.PathOf("changed")); File.WriteAllText(files.PathOf("changed", "old.txt"), "previous");
        var changed = false;
        using var writer = new HookWriter(line =>
        { if (!changed && line.StartsWith("処理中 1 /", StringComparison.Ordinal)) { File.AppendAllText(files.B, "\n% changed\n"); changed = true; } });
        var result = files.Run("changed", true, extraArgs: ["--force"], writer: writer);
        Assert.True(changed); Assert.Equal(2, result.Code); Assert.Contains("入力ファイルが変わりました", result.Error);
        Assert.Equal("previous", File.ReadAllText(files.PathOf("changed", "old.txt")));
        Assert.False(File.Exists(files.PathOf("changed", "result.json"))); Assert.Empty(Directory.GetDirectories(files.Root, ".reportdiff-stage-*"));
    }
}

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class PageFlowTerminalCoreTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Terminal_nonflow_budget_and_aggregation_budget_are_shared_and_exact(bool reverse)
    {
        using var fixture = new Fixture("independent-terminal", reverse);
        var plan = fixture.Prepare(); Assert.True(plan.Decision.Ready); Assert.Single(plan.Nonflow.Proofs);
        var input = fixture.Input(); var grouped = plan.Aggregate(input.Pages, false);
        Assert.Equal("grouped", grouped.Status); Assert.Equal(2, grouped.AggregatedDifferenceCount);
        Assert.True(grouped.TerminalDescriptorBytes > 0); Assert.False(grouped.AggregatedDifferenceCountComplete);
        Assert.Equal("skipped", PageFlowAggregation.Evaluate(input).Status);
        var workspace = PageFlowTerminalScope.NonflowWorkspace(plan.Inference);
        Assert.Equal(workspace + 1280, plan.Nonflow.DescriptorBytes);
        foreach (var excess in new[] { 0, 1 })
        {
            var doc = new PageFlowDocumentDescriptor(fixture.Document.Pages.ToArray(), fixture.Document.Usage with
                { DescriptorBytes = PageFlowLimits.MaximumDescriptorBytes - plan.Nonflow.DescriptorBytes + excess });
            var prepared = fixture.Prepare(doc);
            Assert.Equal(excess == 0, prepared.Decision.Ready);
            if (excess == 1) { Assert.Empty(prepared.Nonflow.Proofs); Assert.Equal(0, prepared.Nonflow.DescriptorBytes); Assert.Contains("flow_descriptor_limit", prepared.Decision.Reasons); }
            else Assert.Equal(PageFlowLimits.MaximumDescriptorBytes, prepared.Usage.DescriptorBytes);
        }
        var needed = plan.Nonflow.DescriptorBytes + grouped.TerminalDescriptorBytes;
        foreach (var excess in new[] { 0, 1 })
        {
            var doc = new PageFlowDocumentDescriptor(fixture.Document.Pages.ToArray(), fixture.Document.Usage with
                { DescriptorBytes = PageFlowLimits.MaximumDescriptorBytes - needed + excess });
            var prepared = fixture.Prepare(doc); Assert.True(prepared.Decision.Ready);
            for (var repetition = 0; repetition < 2; repetition++)
            {
                var result = prepared.Aggregate(input.Pages, false);
                Assert.Equal(excess == 0 ? "grouped" : "skipped", result.Status);
                if (excess == 0) Assert.Equal(PageFlowLimits.MaximumDescriptorBytes, prepared.Usage.DescriptorBytes + result.TerminalDescriptorBytes);
                else { Assert.Equal("terminal_descriptor_limit", result.Reason); Assert.Empty(result.Groups); Assert.Equal(11, result.AggregatedDifferenceCount); Assert.Equal(0, result.TerminalDescriptorBytes); }
            }
        }
        var insufficient = new PageFlowDocumentDescriptor(fixture.Document.Pages.ToArray(), fixture.Document.Usage with
            { DescriptorBytes = PageFlowLimits.MaximumDescriptorBytes - workspace + 1 });
        Assert.False(fixture.Prepare(insufficient).Decision.Ready);
    }

    [Fact]
    public void Terminal_scope_cannot_be_reused_for_other_documents_candidates_or_settings()
    {
        using var fixture = new Fixture("independent-terminal", false);
        var plan = fixture.Prepare(); var parameters = plan.Pages.ToDictionary(p => p.Number, _ => new ComparisonParameters());
        var inference = plan.Inference; var options = new RowOptions { Enabled = true, CarryEnabled = true };
        bool Eligible(PageFlowDocumentDescriptor doc, PageFlowInference.Inference candidate, IReadOnlyDictionary<int, ComparisonParameters>? settings = null,
            bool selection = false, bool alignment = false) => PageFlowTerminalScope.Eligible(doc, candidate, settings ?? parameters, options, selection, alignment);
        Assert.True(Eligible(fixture.Document, inference));
        var other = new PageFlowDocumentDescriptor(fixture.Document.Pages.ToArray(), fixture.Document.Usage);
        Assert.False(Eligible(other, inference)); Assert.False(Eligible(fixture.Document, inference with { Proposals = inference.Proposals.ToArray() }));
        Assert.False(Eligible(fixture.Document, inference with { Layouts = inference.Layouts with { } }));
        Assert.False(Eligible(fixture.Document, inference, selection: true)); Assert.False(Eligible(fixture.Document, inference, alignment: true));
        foreach (var modified in new[] { new ComparisonParameters { Dpi = 600 }, new() { Diff = new() { MaxShiftMm = 0 } },
            new() { Exclude = [new(1, 1, 1, 1)] }, new() { Cluster = new() { MaxClustersPerPage = 499 } }, new() { Move = new() { MinScore = .99 } } })
            Assert.False(Eligible(fixture.Document, inference, parameters.ToDictionary(p => p.Key, _ => modified)));
        var reserved = PageFlowTerminalScope.AggregationWorkspace(fixture.Input());
        var scope = PageFlowTerminalScope.CreateReserved(fixture.Document, inference, reserved, reserved);
        scope.VerifyBinding(fixture.Document, inference);
        Assert.Throws<ArgumentException>(() => scope.VerifyBinding(other, inference));
        Assert.Throws<ArgumentException>(() => scope.VerifyBinding(fixture.Document, inference with { }));
        Assert.Throws<ArgumentException>(() => PageFlowTerminalScope.CreateReserved(fixture.Document, inference, reserved, reserved - 1));
        var candidates = plan.Links.Select(l => new PageFlowCandidate(l.Source, l.Target, l.Status == "band_verified", l.Reason)).ToArray();
        var endpoints = plan.Links.Where(l => l.Status == "band_verified").SelectMany(l => new[] { l.Source!, l.Target! }).ToArray();
        var proofs = plan.Pages.Select(p => new PageFlowPageProof(p.Number, p.Built?.Status == "built", p.Built is null
            ? endpoints.Where(b => b.Page.Page == p.Number).ToArray() : p.Built.Removed.Where(r => r.Proof == "verified_carry_range").Select(r => r.Band).ToArray())).ToArray();
        Assert.False(PageFlowRangeGate.Evaluate(fixture.Document, true, null, candidates, proofs).Ready);
        Assert.False(PageFlowRangeGate.Evaluate(other, true, null, candidates, proofs, plan.Nonflow.Proofs).Ready);
    }

    [Theory]
    [InlineData("independent-terminal", false)] [InlineData("independent-terminal", true)]
    [InlineData("independent-opposite", false)] [InlineData("independent-opposite", true)]
    [InlineData("neutral-between", false)] [InlineData("neutral-between", true)]
    [InlineData("independent-tone", false)] [InlineData("independent-tone", true)]
    public void Terminal_membership_is_all_or_nothing_and_never_completes_the_unpaired_page(string name, bool reverse)
    {
        using var fixture = new Fixture(name, reverse); var plan = fixture.Prepare(); var input = fixture.Input();
        var result = plan.Aggregate(input.Pages, false); Assert.Equal("grouped", result.Status);
        Assert.Equal(JsonSerializer.Serialize(result, ReportJson.Options), JsonSerializer.Serialize(plan.Aggregate(input.Pages.Reverse().ToArray(), false), ReportJson.Options));
        Assert.Empty(plan.Aggregate(input.Pages, true).Groups);
        Assert.False(plan.Aggregate(input.Pages.Select(p => p with { Complete = true, UnpairedCovered = false }).ToArray(), false).AggregatedDifferenceCountComplete);
        foreach (var page in input.Pages.Where(p => p.Paired))
        {
            var preserved = plan.Aggregate(Patch(page with { Clusters = page.Clusters + 1, DifferenceCount = page.DifferenceCount + 1 }), false);
            Assert.Equal(result.AggregatedDifferenceCount + 1, preserved.AggregatedDifferenceCount);
            foreach (var structure in page.Structures)
            {
                Reject(Patch(page with { Structures = page.Structures.Where(s => s != structure).ToArray(), DifferenceCount = page.DifferenceCount - 1 }));
                Reject(Patch(page with { Structures = page.Structures.Append(structure).ToArray(), DifferenceCount = page.DifferenceCount + 1 }));
            }
        }
        Assert.Throws<ArgumentException>(() => plan.Aggregate(input.Pages.Skip(1).ToArray(), false));
        var scope = PageFlowTerminalScope.CreateReserved(fixture.Document, plan.Inference, result.TerminalDescriptorBytes, result.TerminalDescriptorBytes);
        foreach (var row in input.Rows)
        {
            Assert.False(scope.Matches(input with { Rows = input.Rows.Append(row).ToArray() }));
            Assert.False(scope.Matches(input with { Rows = input.Rows.Select(r => r == row ? r with { Text = "REPLACED" } : r).ToArray() }));
        }
        var unpaired = input.Pages.Single(p => !p.Paired);
        var uncovered = input with { Pages = Patch(unpaired with { UnpairedCovered = false }) };
        Assert.Empty(PageFlowComponents.Evaluate(uncovered, PageFlowAggregation.Evaluate(uncovered), scope, result.TerminalDescriptorBytes).Groups);
        Page[] Patch(Page replacement) => input.Pages.Select(p => p.Number == replacement.Number ? replacement : p).ToArray();
        void Reject(Page[] pages)
        { var actual = plan.Aggregate(pages, false); Assert.Equal("skipped", actual.Status); Assert.Empty(actual.Groups); Assert.Equal(actual.DifferenceCount, actual.AggregatedDifferenceCount); }
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly PdfReader a, b;
        private readonly PdfTextReader ta, tb;
        private readonly string name; private readonly bool reverse; private readonly string group;
        internal PageFlowDocumentDescriptor Document { get; }
        internal Fixture(string name, bool reverse, string group = "page-flow-unpaired")
        {
            this.name = name; this.reverse = reverse; this.group = group;
            var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", group, name);
            var pathA = Path.Combine(root, reverse ? "b.pdf" : "a.pdf"); var pathB = Path.Combine(root, reverse ? "a.pdf" : "b.pdf");
            a = PdfReader.Open(pathA); b = PdfReader.Open(pathB); ta = new(pathA); tb = new(pathB);
            var keys = Enumerable.Range(1, a.PageCount).Select(p => new PageFlowPageKey(PageSpace.A, p))
                .Concat(Enumerable.Range(1, b.PageCount).Select(p => new PageFlowPageKey(PageSpace.B, p))).ToArray();
            var collector = new PageFlowCollector(keys);
            foreach (var key in keys)
            {
                using var image = Read(key);
                Assert.True(collector.Add(key, image, (key.Side == PageSpace.A ? ta : tb).ReadRowWords(key.Page, image.Size(), 300), .5));
            }
            Document = collector.Complete()!;
        }
        internal PageFlowPlan Prepare(PageFlowDocumentDescriptor? document = null) => PageFlowPlan.Prepare(document ?? Document, Read, new(), new() { Enabled = true, CarryEnabled = true });
        internal Input Input()
        {
            using var records = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", group, "expectations.json")));
            return records.RootElement.EnumerateArray().Single(r => r.GetProperty("id").GetString() == name && r.GetProperty("reverse").GetBoolean() == reverse)
                .GetProperty("input").Deserialize<Input>(ReportJson.Options)!;
        }
        private Mat Read(PageFlowPageKey key) { using var image = (key.Side == PageSpace.A ? a : b).ReadPage(key.Page, 300); return image.TakePixels(); }
        public void Dispose() { ta.Dispose(); tb.Dispose(); a.Dispose(); b.Dispose(); }
    }
}
