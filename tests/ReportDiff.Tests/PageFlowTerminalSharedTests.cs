using System.Runtime.Versioning;
using System.Text.Json;
using ReportDiff.Core;
using ReportDiff.Report;
using Xunit;
using static ReportDiff.Core.PageFlowAggregation;

namespace ReportDiff.Tests;

public sealed partial class PageFlowCliTests
{
    public static IEnumerable<object[]> TerminalSharedCases()
    {
        using var data = TerminalSharedExpected();
        return data.RootElement.EnumerateArray().Select(r => new object[] { r.GetProperty("id").GetString()!, r.GetProperty("reverse").GetBoolean() }).ToArray();
    }
    private static JsonDocument TerminalSharedExpected() => JsonDocument.Parse(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-terminal-shared/expectations.json")));

    [Theory]
    [MemberData(nameof(TerminalSharedCases))]
    public void Terminal_shared_fixed_cases_match_independent_diagnosis_and_preserve_raw_evidence(string id, bool reverse)
    {
        using var data = TerminalSharedExpected();
        var expected = data.RootElement.EnumerateArray().Single(r => r.GetProperty("id").GetString() == id && r.GetProperty("reverse").GetBoolean() == reverse);
        var candidate = expected.GetProperty("candidate");
        using var files = new Files("terminal-shared-" + id, reverse);
        var result = files.Run("on", true); Assert.Equal(1, result.Code); Assert.Empty(result.Error);
        Assert.Equal(1, files.Run("off", false).Code);
        var report = files.Report("on"); var off = files.Report("off"); var flow = report.PageFlow!;
        Assert.Equal(candidate.GetProperty("difference_count").GetInt32(), report.Summary.DifferenceCount);
        Assert.Equal(candidate.GetProperty("aggregated_difference_count").GetInt32(), report.Summary.AggregatedDifferenceCount);
        Assert.Equal(candidate.GetProperty("status").GetString(), flow.Aggregation.Status);
        Assert.False(report.Summary.DifferenceCountComplete); Assert.False(report.Summary.AggregatedDifferenceCountComplete);
        var last = report.Pages[^1]; Assert.StartsWith("only_in_", last.Status);
        Assert.Empty(last.Clusters); Assert.Empty(last.RowAlignment.StructuralChanges); Assert.False(last.DifferenceCountComplete);
        if (id is "before8" or "chain" or "tone")
        {
            Assert.Equal("applied", flow.Status); Assert.Empty(flow.Aggregation.Groups);
            var component = Assert.Single(flow.Aggregation.SharedComponents!); Assert.Equal(2, component.Causes.Count);
            Assert.Equal(Enumerable.Range(1, report.Pages.Count), component.Pages);
            var auxiliary = Assert.Single(component.AuxiliaryBands!);
            Assert.Equal(last.Page, auxiliary.Page); Assert.Equal(reverse ? "a" : "b", auxiliary.Side);
            Assert.Equal("original_top_left", auxiliary.CoordinateSystem); Assert.Equal(300, auxiliary.BoundsPx.Y); Assert.Equal(200, auxiliary.BoundsPx.H);
            Assert.Empty(auxiliary.Structures); Assert.NotNull(auxiliary.Image); Assert.True(File.Exists(files.PathOf("on", auxiliary.Image)));
            var end = component.Links[^1]; Assert.Equal(JsonSerializer.Serialize(auxiliary, ReportJson.Options), JsonSerializer.Serialize(Assert.Single(end.AuxiliaryBands!), ReportJson.Options));
            Assert.Single(end.Structures); Assert.Equal(JsonSerializer.Serialize(auxiliary, ReportJson.Options), JsonSerializer.Serialize(flow.Links.Single(l => l.Id == end.Link).Target, ReportJson.Options));
            Assert.All(component.Links.SkipLast(1), l => Assert.Null(l.AuxiliaryBands));
            Assert.Equal(component.Structures.Count, component.Structures.Distinct().Count());
            Assert.Equal(component.Structures.OrderBy(r => r.Page).ThenBy(r => r.StructuralChangeId),
                component.Causes.Select(c => c.Reference).Concat(component.Movements.Select(m => m.Structure))
                    .Concat(component.Links.SelectMany(l => l.Structures)).OrderBy(r => r.Page).ThenBy(r => r.StructuralChangeId));
            Assert.All(component.Structures, r => Assert.Contains(report.Pages.Single(p => p.Page == r.Page).RowAlignment.StructuralChanges, s => s.Id == r.StructuralChangeId));
            Assert.All(component.Movements, m => Assert.Equal(m.Dy, component.Causes.Where(c => m.Causes.Contains(c.Reference)).Sum(c => c.DeltaPx)));
            Assert.All(component.Balance, b => Assert.Equal(b.RowsB - b.RowsA, b.CauseDelta + b.Incoming - b.Outgoing));
            var boundary = Assert.Single(flow.Links, l => l.Ambiguity is not null); Assert.Equal(2, boundary.Id);
            Assert.Equal(200, boundary.Ambiguity!.SelectedDy); Assert.Equal(2, boundary.Ambiguity.CrossingRows.Count);
            Assert.Equal(new[] { 100, 200 }, boundary.Ambiguity.Shifts.Select(s => s.Dy)); Assert.Empty(boundary.Ambiguity.Alternatives);
            Assert.All(flow.Links, l => { Assert.Equal("carried", l.Status); Assert.Equal("verified", l.ImageStatus); Assert.True(l.OriginalComparisons > 0); });
            var expectedAdoptions = expected.GetProperty("adoptions").EnumerateArray().Select(r => r.GetProperty("adoption").Deserialize<PageFlowAdoptionResult>(ReportJson.Options));
            Assert.Equal(JsonSerializer.Serialize(expectedAdoptions, ReportJson.Options), JsonSerializer.Serialize(flow.Pages.Where(p => p.Adoption is not null).Select(p => p.Adoption), ReportJson.Options));
            Assert.True(flow.Pages[^1].OnlyVerifiedBandsAndFixedParts);
            Assert.Equal(id == "tone" ? 1390 : 0, report.Pages.Sum(p => p.RawPixels)); Assert.Equal(id == "tone" ? 1 : 0, report.Summary.Clusters);
            var html = File.ReadAllText(files.PathOf("on", "report.html")); Assert.Contains("片側ページの補助帯", html); Assert.Contains("未比較の状態を維持", html);
            foreach (var reference in component.Structures) Assert.Contains($"href=\"#page-{reference.Page}-structure-{reference.StructuralChangeId}\"", html);
            Assert.Contains($"src=\"{auxiliary.Image}\"", System.Net.WebUtility.HtmlDecode(html));
        }
        else Assert.Null(flow.Aggregation.SharedComponents);
        if (flow.Status == "skipped") Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
        foreach (var (p, q) in report.Pages.Zip(off.Pages))
        foreach (var (current, baseline) in new[] { (p.RawEvidence!.A.Image, q.RawEvidence!.A.Image), (p.RawEvidence.B.Image, q.RawEvidence.B.Image), (p.RawEvidence.Overlay, q.RawEvidence.Overlay) })
            Assert.Equal(File.ReadAllBytes(files.PathOf("off", baseline)), File.ReadAllBytes(files.PathOf("on", current)));
    }

    [Theory]
    [InlineData("", "", "--profile", "strict")] [InlineData("", "", "--profile", "loose")]
    [InlineData("", "", "--dpi", "150")] [InlineData("", "", "--pages", "1-3")] [InlineData("", "", "--pages", "1,3")]
    [InlineData("", "align: {enabled: true}", "", "")]
    [InlineData("", "exclude: [{page: 1, x: 1, y: 1, w: 1, h: 1}]", "", "")]
    [InlineData("", "regions: [{page: 1, name: test, mode: compare, x: 1, y: 1, w: 1, h: 1}]", "", "")]
    [InlineData("min_support_bands: 3", "", "", "")] [InlineData("max_segments: 1", "", "", "")]
    [InlineData("", "text: {max_words_per_page: 1}", "", "")]
    public void Terminal_shared_unsupported_settings_never_keep_partial_groups(string rows, string yaml, string flag, string value)
    {
        using var files = new Files("terminal-shared-before8"); string[] args = flag.Length == 0 ? [] : [flag, value];
        Assert.Equal(1, files.Run("on", true, rows, args, extraYaml: yaml).Code); Assert.Equal(1, files.Run("off", false, rows, args, extraYaml: yaml).Code);
        var report = files.Report("on"); Assert.Equal("skipped", report.PageFlow!.Status);
        Assert.Empty(report.PageFlow.Aggregation.Groups); Assert.Null(report.PageFlow.Aggregation.SharedComponents);
        Assert.Equal(JsonSerializer.Serialize(files.Report("off").Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
    }

    [Fact]
    public void Terminal_shared_rejected_improvement_preserves_content()
    {
        using var files = new Files("terminal-shared-tone");
        Assert.Equal(1, files.Run("on", true, "min_improvement: 1").Code); Assert.Equal(1, files.Run("off", false, "min_improvement: 1").Code);
        var on = files.Report("on"); Assert.Equal("skipped", on.PageFlow!.Status); Assert.Null(on.PageFlow.Aggregation.SharedComponents);
        Assert.Contains(on.PageFlow.Pages, p => p.Adoption?.Reason == "low_improvement");
        Assert.Equal(JsonSerializer.Serialize(files.Report("off").Pages, ReportJson.Options), JsonSerializer.Serialize(on.Pages, ReportJson.Options));
    }

    [Fact]
    public void Terminal_shared_input_update_failure_preserves_previous_output()
    {
        using var files = new Files("terminal-shared-before8");
        Directory.CreateDirectory(files.PathOf("changed")); File.WriteAllText(files.PathOf("changed", "old.txt"), "previous");
        var mutation = new InputMutationAttempt(files.B);
        using var writer = new HookWriter(line =>
        { if (!mutation.Attempted && line.StartsWith("処理中 1 /", StringComparison.Ordinal)) mutation.AppendPdfComment(); });
        var result = files.Run("changed", true, extraArgs: ["--force"], writer: writer);
        mutation.AssertCliError(result.Code, result.Error);
        Assert.Equal("previous", File.ReadAllText(files.PathOf("changed", "old.txt"))); Assert.False(File.Exists(files.PathOf("changed", "result.json")));
        Assert.Empty(Directory.GetDirectories(files.Root, ".reportdiff-stage-*"));
    }
}

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class PageFlowTerminalSharedCoreTests
{
    private static PageFlowTerminalCoreTests.Fixture Fixture(string id, bool reverse = false) => new(id, reverse, "page-flow-terminal-shared");

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Terminal_shared_candidate_and_result_reservations_are_exact_and_retry_does_not_accumulate(bool reverse)
    {
        using var fixture = Fixture("before8", reverse); var plan = fixture.Prepare(); var input = fixture.Input();
        Assert.True(plan.Decision.Ready); Assert.Empty(plan.Nonflow.Proofs); Assert.Empty(plan.NumericRows.Proofs);
        var proof = Assert.Single(plan.Ambiguity.Proofs); Assert.Equal(1, proof.CandidateIndex); Assert.Equal(200, proof.SelectedDy);
        Assert.Equal(PageFlowTerminalScope.NonflowWorkspace(plan.Inference) + 3072, plan.Ambiguity.DescriptorBytes);
        Assert.Empty(PageFlowAmbiguity.Find(fixture.Document, plan.Inference, new() { Enabled = true, CarryEnabled = true }, 300, false, long.MaxValue).Proofs);
        var grouped = plan.Aggregate(input.Pages, false); Assert.Equal("grouped", grouped.Status);
        Assert.True(grouped.SharedDescriptorBytes > 256); Assert.True(grouped.TerminalDescriptorBytes > 0);
        Assert.Equal("skipped", PageFlowAggregation.Evaluate(input).Status);
        foreach (var excess in new[] { 0, 1 })
        {
            var doc = new PageFlowDocumentDescriptor(fixture.Document.Pages.ToArray(), fixture.Document.Usage with
                { DescriptorBytes = PageFlowLimits.MaximumDescriptorBytes - plan.Ambiguity.DescriptorBytes + excess });
            var prepared = fixture.Prepare(doc); Assert.Equal(excess == 0, prepared.Decision.Ready);
            if (excess == 0) Assert.Equal(PageFlowLimits.MaximumDescriptorBytes, prepared.Usage.DescriptorBytes);
            else { Assert.Empty(prepared.Ambiguity.Proofs); Assert.Equal(0, prepared.Ambiguity.DescriptorBytes); Assert.Contains("flow_descriptor_limit", prepared.Decision.Reasons); }
        }
        var needed = plan.Ambiguity.DescriptorBytes + grouped.TerminalDescriptorBytes + grouped.SharedDescriptorBytes;
        foreach (var excess in new[] { 0, 1 })
        {
            var doc = new PageFlowDocumentDescriptor(fixture.Document.Pages.ToArray(), fixture.Document.Usage with
                { DescriptorBytes = PageFlowLimits.MaximumDescriptorBytes - needed + excess });
            var prepared = fixture.Prepare(doc); Assert.True(prepared.Decision.Ready);
            for (var i = 0; i < 2; i++)
            {
                var actual = prepared.Aggregate(input.Pages, false);
                Assert.Equal(excess == 0 ? "grouped" : "skipped", actual.Status);
                if (excess == 0) Assert.Equal(PageFlowLimits.MaximumDescriptorBytes, prepared.Usage.DescriptorBytes + actual.SharedDescriptorBytes + actual.TerminalDescriptorBytes);
                else { Assert.Equal("terminal_descriptor_limit", actual.Reason); Assert.Null(actual.SharedComponents); Assert.Empty(actual.Groups);
                    Assert.Equal(actual.DifferenceCount, actual.AggregatedDifferenceCount); Assert.Equal(0, actual.TerminalDescriptorBytes + actual.SharedDescriptorBytes); }
            }
        }
    }

    [Fact]
    public void Terminal_shared_resolution_is_bound_to_original_document_candidates_and_effective_settings()
    {
        using var fixture = Fixture("before8"); var plan = fixture.Prepare(); var doc = fixture.Document;
        var settings = plan.Pages.ToDictionary(p => p.Number, _ => new ComparisonParameters()); var options = new RowOptions { Enabled = true, CarryEnabled = true };
        PageFlowAmbiguity.Result Find(PageFlowDocumentDescriptor document, PageFlowInference.Inference inference,
            RowOptions? rows = null, IReadOnlyDictionary<int, ComparisonParameters>? parameters = null, bool selected = false, bool aligned = false)
            => PageFlowAmbiguity.FindTerminal(document, inference, parameters ?? settings, rows ?? options, selected, aligned, long.MaxValue);
        Assert.Single(Find(doc, plan.Inference).Proofs);
        Assert.Empty(Find(new(doc.Pages.ToArray(), doc.Usage), plan.Inference).Proofs);
        Assert.Empty(Find(doc, plan.Inference with { Proposals = plan.Inference.Proposals.ToArray() }).Proofs);
        Assert.Empty(Find(doc, plan.Inference with { Layouts = plan.Inference.Layouts with { } }).Proofs);
        Assert.Empty(Find(doc, plan.Inference, options with { MinSupportBands = 3 }).Proofs);
        Assert.Empty(Find(doc, plan.Inference, options with { MaxShiftMm = 10 }).Proofs);
        Assert.Empty(Find(doc, plan.Inference, parameters: settings.ToDictionary(p => p.Key, _ => new ComparisonParameters { Diff = new() { ColorThreshold = 4 } })).Proofs);
        Assert.Empty(Find(doc, plan.Inference, selected: true).Proofs); Assert.Empty(Find(doc, plan.Inference, aligned: true).Proofs);
        var changed = plan.Inference.Layouts.A[1];
        Assert.Empty(Find(doc, plan.Inference with { Layouts = plan.Inference.Layouts with { A = plan.Inference.Layouts.A.Select(l => l == changed
            ? l with { Body = l.Body.Select(r => r with { Text = r.Text + " changed" }).ToArray() } : l).ToArray() } }).Proofs);
    }

    [Theory]
    [InlineData("before8", false)] [InlineData("before8", true)] [InlineData("chain", false)] [InlineData("chain", true)] [InlineData("tone", false)] [InlineData("tone", true)]
    public void Terminal_shared_membership_is_all_or_nothing_and_content_and_incomplete_state_survive(string id, bool reverse)
    {
        using var fixture = Fixture(id, reverse); var plan = fixture.Prepare(); var input = fixture.Input();
        var result = plan.Aggregate(input.Pages, false); Assert.Equal("grouped", result.Status);
        Assert.Equal(JsonSerializer.Serialize(result, ReportJson.Options), JsonSerializer.Serialize(plan.Aggregate(input.Pages.Reverse().ToArray(), false), ReportJson.Options));
        Assert.False(plan.Aggregate(input.Pages.Select(p => p with { Complete = true }).ToArray(), false).AggregatedDifferenceCountComplete);
        Assert.Null(plan.Aggregate(input.Pages, true).SharedComponents); Assert.Null(plan.Aggregate(input.Pages, false, false).SharedComponents);
        foreach (var page in input.Pages.Where(p => p.Paired))
        {
            var added = plan.Aggregate(Patch(page with { Clusters = page.Clusters + 1, DifferenceCount = page.DifferenceCount + 1 }), false);
            Assert.Equal(result.AggregatedDifferenceCount + 1, added.AggregatedDifferenceCount);
            foreach (var structure in page.Structures)
            {
                Reject(Patch(page with { Structures = page.Structures.Where(s => s != structure).ToArray(), DifferenceCount = page.DifferenceCount - 1 }));
                Reject(Patch(page with { Structures = page.Structures.Append(structure).ToArray(), DifferenceCount = page.DifferenceCount + 1 }));
                Reject(Patch(page with { Structures = page.Structures.Select(s => s == structure ? s with { Excluded = true } : s).ToArray() }));
            }
        }
        var reserved = result.TerminalDescriptorBytes + result.SharedDescriptorBytes;
        var scope = PageFlowTerminalScope.CreateReserved(fixture.Document, plan.Inference, reserved, reserved);
        var last = input.Pages[^1]; RejectInput(input with { Pages = Patch(last with { UnpairedCovered = false }) });
        RejectInput(input with { Pages = Patch(last with { Clusters = 1, DifferenceCount = 1 }) });
        foreach (var row in input.Rows) RejectInput(input with { Rows = input.Rows.Select(r => r == row ? r with { Text = r.Text + " replaced" } : r).ToArray() });
        foreach (var link in input.Links)
        {
            RejectInput(input with { Links = input.Links.Where(l => l != link).ToArray() });
            RejectInput(input with { Links = input.Links.Select(l => l == link ? l with { Target = new(l.Target!.Page, l.Target.Top, l.Target.Height - 1) } : l).ToArray() });
        }
        Page[] Patch(Page replacement) => input.Pages.Select(p => p.Number == replacement.Number ? replacement : p).ToArray();
        void Reject(Page[] pages) => Check(plan.Aggregate(pages, false));
        void RejectInput(Input mutated) => Check(PageFlowSharedCauses.Evaluate(mutated, PageFlowAggregation.Evaluate(mutated), reserved, scope));
        static void Check(Decision d) { Assert.Equal("skipped", d.Status); Assert.Empty(d.Groups); Assert.Null(d.SharedComponents); Assert.Equal(d.DifferenceCount, d.AggregatedDifferenceCount); }
    }
}
