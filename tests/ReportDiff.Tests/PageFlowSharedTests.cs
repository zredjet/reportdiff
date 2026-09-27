using System.Runtime.Versioning;
using System.Text.Json;
using ReportDiff.Core;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed partial class PageFlowCliTests
{
    public static IEnumerable<object[]> SharedCases()
    {
        using var data = SharedExpected();
        return data.RootElement.EnumerateArray().Select(r => new object[] { r.GetProperty("id").GetString()!, r.GetProperty("reverse").GetBoolean() }).ToArray();
    }

    [Theory]
    [MemberData(nameof(SharedCases))]
    public void Shared_causes_match_independent_evidence_without_changing_content(string id, bool reverse)
    {
        using var expected = SharedExpected();
        using var ambiguity = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-shared/ambiguity-expected.json")));
        var saved = expected.RootElement.EnumerateArray().Single(r => r.GetProperty("id").GetString() == id && r.GetProperty("reverse").GetBoolean() == reverse);
        if (id is "shared-chain" or "shared-and-independent")
            saved = ambiguity.RootElement.EnumerateArray().Single(r => r.GetProperty("id").GetString() == id && r.GetProperty("reverse").GetBoolean() == reverse);
        var decision = saved.GetProperty("expected");
        using var files = new Files("shared-" + id, reverse);
        Assert.Equal(1, files.Run("on", true).Code); Assert.Equal(1, files.Run("off", false).Code);
        var report = files.Report("on"); var off = files.Report("off"); var flow = report.PageFlow!;
        if (id == "same-page-two")
        {
            // 保存済みの旧診断はそのまま残し、A3の二原因経路を別の期待として検査する。
            Assert.Equal(10, decision.GetProperty("difference_count").GetInt32());
            Assert.Equal(6, report.Summary.DifferenceCount); Assert.Equal(2, report.Summary.AggregatedDifferenceCount);
            Assert.Equal("grouped", flow.Aggregation.Status); Assert.Equal("applied", flow.AnchoredContent!.Status);
            Assert.All(flow.Pages, p => Assert.True(p.Adoption!.Accepted)); Assert.Equal(0, report.Summary.Clusters);
        }
        else
        {
            Assert.Equal(decision.GetProperty("difference_count").GetInt32(), report.Summary.DifferenceCount);
            Assert.Equal(decision.GetProperty("aggregated_difference_count").GetInt32(), report.Summary.AggregatedDifferenceCount);
            Assert.Equal(decision.GetProperty("status").GetString(), flow.Aggregation.Status);
            Assert.Equal(saved.GetProperty("input").GetProperty("gate_ready").GetBoolean() ? "applied" : "skipped", flow.Status);
            Assert.Equal(JsonSerializer.Serialize(saved.GetProperty("adoptions").Deserialize<PageFlowAdoptionResult[]>(ReportJson.Options), ReportJson.Options),
                JsonSerializer.Serialize(flow.Pages.Where(p => p.Adoption is not null).Select(p => p.Adoption), ReportJson.Options));
        }
        var shared = flow.Aggregation.SharedComponents ?? [];
        var refs = flow.Aggregation.Groups.SelectMany(g => g.Structures).Concat(shared.SelectMany(c => c.Structures)).ToArray();
        Assert.Equal(refs.Length, refs.Distinct().Count());
        foreach (var c in shared)
        {
            Assert.Equal(id == "same-page-two" ? "original_top_left" : "globally_aligned", c.CoordinateSystem); Assert.Equal(2, c.Causes.Count);
            var combined = c.Movements.Where(m => m.Causes.Count == 2).ToArray();
            Assert.Equal(id == "shared-chain" ? 2 : 1, combined.Length);
            Assert.All(combined, movement => Assert.Equal(reverse ? -200 : 200, movement.Dy));
            Assert.All(c.Movements, m => Assert.Equal(m.Dy, c.Causes.Where(x => m.Causes.Contains(x.Reference)).Sum(x => x.DeltaPx)));
            Assert.All(c.Causes, x => Assert.Contains(x.Reference, c.Structures));
            Assert.All(c.Links, l => Assert.Equal("carried", flow.Links.Single(x => x.Id == l.Link).Status));
            Assert.All(c.Balance, b => Assert.Equal(b.RowsB - b.RowsA, b.CauseDelta + b.Incoming - b.Outgoing));
        }
        if (id == "shared-tone") { Assert.Equal(1614, report.Pages[0].RawPixels); Assert.Single(report.Pages[0].Clusters); }
        var html = File.ReadAllText(files.PathOf("on", "report.html"));
        if (shared.Count > 0) Assert.Contains("同じ送り連鎖の複数原因", html);
        else Assert.DoesNotContain("shared_components", File.ReadAllText(files.PathOf("on", "result.json")));
        foreach (var r in refs)
        {
            Assert.Contains(report.Pages.Single(p => p.Page == r.Page).RowAlignment.StructuralChanges, s => s.Id == r.StructuralChangeId);
            Assert.Contains($"href=\"#page-{r.Page}-structure-{r.StructuralChangeId}\"", html);
            Assert.Contains($"id=\"page-{r.Page}-structure-{r.StructuralChangeId}\"", html);
        }
        foreach (var (p, q) in report.Pages.Zip(off.Pages))
        foreach (var (x, y) in new[] { (p.RawEvidence!.A.Image, q.RawEvidence!.A.Image), (p.RawEvidence.B.Image, q.RawEvidence.B.Image), (p.RawEvidence.Overlay, q.RawEvidence.Overlay) })
            Assert.Equal(File.ReadAllBytes(files.PathOf("on", x)), File.ReadAllBytes(files.PathOf("off", y)));
        if (flow.Status == "skipped") Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
    }

    [Theory]
    [InlineData("1-2")] [InlineData("1")]
    public void Shared_explicit_selection_never_creates_partial_components(string pages)
    {
        using var files = new Files("shared-shared-two");
        Assert.Equal(1, files.Run("on", true, extraArgs: ["--pages", pages]).Code);
        var report = files.Report("on"); Assert.Empty(report.PageFlow!.Aggregation.Groups); Assert.Null(report.PageFlow.Aggregation.SharedComponents);
        Assert.Equal(report.Summary.DifferenceCount, report.Summary.AggregatedDifferenceCount); Assert.False(report.Summary.AggregatedDifferenceCountComplete);
    }

    [Theory]
    [InlineData("shared-two")] [InlineData("shared-chain")] [InlineData("shared-and-independent")]
    public void Shared_input_update_failure_preserves_previous_output(string scenario)
    {
        using var files = new Files("shared-" + scenario); Directory.CreateDirectory(files.PathOf("changed"));
        File.WriteAllText(files.PathOf("changed", "old.txt"), "old"); var mutation = new InputMutationAttempt(files.B);
        using var writer = new HookWriter(line =>
        { if (!mutation.Attempted && line.StartsWith("処理中 1 /", StringComparison.Ordinal)) mutation.AppendPdfComment(); });
        var result = files.Run("changed", true, extraArgs: ["--force"], writer: writer);
        mutation.AssertCliError(result.Code, result.Error); Assert.Equal("old", File.ReadAllText(files.PathOf("changed", "old.txt")));
        Assert.Empty(Directory.GetDirectories(files.Root, ".reportdiff-stage-*"));
    }

    private static JsonDocument SharedExpected() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-shared/expected.json")));

    [Theory]
    [InlineData("shared-number", false, 40, 1)] [InlineData("shared-number", true, 40, 1)]
    [InlineData("shared-fields", false, 80, 2)] [InlineData("shared-fields", true, 80, 2)]
    [InlineData("shared-anchor-change", false, 0, 0)] [InlineData("shared-anchor-change", true, 0, 0)]
    [InlineData("shared-carry-change", false, 0, 0)] [InlineData("shared-carry-change", true, 0, 0)]
    public void Shared_numeric_content_requires_original_anchors_and_exact_carry(string id, bool reverse, int raw, int clusters)
    {
        using var files = new Files("shared-" + id, reverse);
        Assert.Equal(1, files.Run("on", true).Code); Assert.Equal(1, files.Run("off", false).Code);
        var report = files.Report("on"); var off = files.Report("off"); var flow = report.PageFlow!;
        Assert.Equal(raw > 0 ? "applied" : "skipped", flow.Status);
        if (raw > 0)
        {
            Assert.Equal(7 + clusters, report.Summary.DifferenceCount); Assert.Equal(2 + clusters, report.Summary.AggregatedDifferenceCount);
            Assert.Equal(raw, report.Pages[1].RawPixels); Assert.Equal(clusters, report.Pages[1].Clusters.Count);
            var proof = Assert.Single(flow.NumericMatches!); Assert.False(proof.UsedAsExactSupport); Assert.False(proof.PixelEqualityProven);
            Assert.NotEqual(proof.A.Text, proof.B.Text); Assert.All(proof.Anchors, p => Assert.Equal(p.A.Text, p.B.Text));
            var component = Assert.Single(flow.Aggregation.SharedComponents!);
            Assert.Contains(component.Movements, m => m.Dy == (reverse ? -200 : 200) && m.Causes.Count == 2);
        }
        else
        {
            Assert.Null(flow.Aggregation.SharedComponents); Assert.Empty(flow.Aggregation.Groups);
            Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
        }
    }

    public static IEnumerable<object[]> SharedGlobalCases() =>
        new[] { "shared-down", "shared-up", "shared-variable", "shared-global-tone", "shared-global-multiple", "shared-horizontal", "shared-global-number", "shared-strong-number", "shared-strong-multiple" }
        .SelectMany(id => new[] { false, true }.Select(reverse => new object[] { id, reverse, id == "shared-strong-number" || id == "shared-strong-multiple" && !reverse }));

    [Theory]
    [MemberData(nameof(SharedGlobalCases))]
    public void Shared_global_alignment_preserves_low_score_rejection_and_original_coordinates(string id, bool reverse, bool applied)
    {
        using var files = new Files("shared-" + id, reverse);
        Assert.Equal(1, files.Run("on", true, extraYaml: "align: {enabled: true}").Code);
        Assert.Equal(1, files.Run("off", false, extraYaml: "align: {enabled: true}").Code);
        var report = files.Report("on"); var off = files.Report("off"); var flow = report.PageFlow!;
        Assert.Equal(applied ? "applied" : "skipped", flow.Status);
        if (applied)
        {
            Assert.Equal(3, report.Summary.AggregatedDifferenceCount);
            Assert.Single(flow.Aggregation.SharedComponents!);
            if (id == "shared-strong-number")
            {
                var proof = Assert.Single(flow.NumericMatches!); var page = report.Pages.Single(p => p.Page == proof.A.Original.Page);
                Assert.Equal(reverse ? -200 : 200, proof.B.Original.BoundsPx.Y + page.GlobalShiftPx!.Dy - proof.A.Original.BoundsPx.Y);
                Assert.Equal(40, page.RawPixels); Assert.Single(page.Clusters);
            }
        }
        else
        {
            Assert.Null(flow.Aggregation.SharedComponents); Assert.Empty(flow.Aggregation.Groups);
            Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
            Assert.Contains(id == "shared-horizontal" ? "global_alignment_applied" : "fixed_parts_support", flow.Reasons);
        }
        foreach (var (p, q) in report.Pages.Zip(off.Pages))
            Assert.Equal(File.ReadAllBytes(files.PathOf("on", p.RawEvidence!.Overlay)), File.ReadAllBytes(files.PathOf("off", q.RawEvidence!.Overlay)));
    }

    [Theory]
    [InlineData("min_support_ink_mm2: 1000", "")]
    [InlineData("min_improvement: 1", "")]
    [InlineData("", "exclude: [{page: 2, x: 0, y: 20, w: 85, h: 70}]")]
    [InlineData("", "regions: [{page: 2, name: anchor, mode: exclude, x: 0, y: 40, w: 85, h: 4}]")]
    [InlineData("", "text: {max_words_per_page: 1}")]
    public void Shared_failed_support_or_excluded_evidence_keeps_baseline(string rows, string yaml)
    {
        using var files = new Files("shared-shared-number");
        Assert.Equal(1, files.Run("on", true, rows, extraYaml: yaml).Code); Assert.Equal(1, files.Run("off", false, rows, extraYaml: yaml).Code);
        var report = files.Report("on"); Assert.Equal("skipped", report.PageFlow!.Status);
        Assert.Null(report.PageFlow.Aggregation.SharedComponents);
        Assert.Equal(JsonSerializer.Serialize(files.Report("off").Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Shared_compare_region_or_disabled_exclusion_keeps_numeric_difference(bool disable)
    {
        using var files = new Files("shared-shared-number");
        var yaml = disable ? "exclude: [{page: 2, x: 0, y: 20, w: 85, h: 70}]"
            : "regions: [{page: 2, name: numeric, mode: compare, x: 35, y: 76, w: 8, h: 3, diff: {max_shift_mm: 0, edge_tolerance: 0}}]";
        Assert.Equal(1, files.Run("on", true, extraArgs: disable ? ["--no-regions"] : [], extraYaml: yaml).Code);
        var report = files.Report("on"); Assert.Equal("applied", report.PageFlow!.Status); Assert.Single(report.PageFlow.Aggregation.SharedComponents!);
        Assert.True(report.Pages[1].RawPixels >= 40); Assert.NotEmpty(report.Pages[1].Clusters);
    }
}

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class PageFlowSharedCoreTests
{
    [Theory]
    [InlineData("shared-number")] [InlineData("independent-and-shared")]
    public void Shared_proof_uses_remaining_document_numeric_and_nonflow_budget(string scenario)
    {
        using var fixture = new PageFlowNumericCoreTests.NumericInput(scenario, group: "page-flow-shared");
        var document = fixture.Document(); var plan = PageFlowPlan.Prepare(document, fixture.Read, new(), new() { Enabled = true });
        Assert.True(plan.Decision.Ready); Assert.True(plan.NumericRows.DescriptorBytes + plan.Nonflow.DescriptorBytes > 0);
        var pages = new List<PageFlowAggregation.Page>();
        foreach (var p in plan.Pages)
        {
            using var a = fixture.Read(new(PageSpace.A, p.Number)); using var b = fixture.Read(new(PageSpace.B, p.Number));
            using var result = plan.Compare(p.Number, a, b); pages.Add(result.Describe());
        }
        var proof = plan.Aggregate(pages, false); Assert.NotNull(proof.SharedComponents);
        var added = plan.NumericRows.DescriptorBytes + plan.Nonflow.DescriptorBytes + proof.SharedDescriptorBytes;
        foreach (var excess in new[] { 0, 1 })
        {
            var limited = new PageFlowDocumentDescriptor(document.Pages.ToArray(), document.Usage with { DescriptorBytes = PageFlowLimits.MaximumDescriptorBytes - added + excess });
            var prepared = PageFlowPlan.Prepare(limited, fixture.Read, new(), new() { Enabled = true }); Assert.True(prepared.Decision.Ready);
            var actual = prepared.Aggregate(pages, false);
            if (excess == 0)
            {
                Assert.Equal(proof.AggregatedDifferenceCount, actual.AggregatedDifferenceCount);
                Assert.Equal(PageFlowLimits.MaximumDescriptorBytes, prepared.Usage.DescriptorBytes + actual.SharedDescriptorBytes);
            }
            else
            {
                Assert.Equal("shared_descriptor_limit", actual.Reason); Assert.Empty(actual.Groups); Assert.Null(actual.SharedComponents);
                Assert.Equal(actual.DifferenceCount, actual.AggregatedDifferenceCount);
            }
        }
    }

    [Fact]
    public void Shared_proof_has_exact_budget_boundary_and_discards_all_components_on_failure()
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-shared/expected.json")));
        foreach (var saved in data.RootElement.EnumerateArray())
        {
            var input = saved.GetProperty("input").Deserialize<PageFlowAggregation.Input>(ReportJson.Options)!;
            var result = PageFlowAggregation.Evaluate(input);
            Assert.Equal(saved.GetProperty("expected").GetProperty("aggregated_difference_count").GetInt32(), result.AggregatedDifferenceCount);
            if (result.SharedComponents is null) continue;
            Assert.True(result.SharedDescriptorBytes > 0);
            var exact = PageFlowAggregation.Evaluate(input, descriptorBytesRemaining: result.SharedDescriptorBytes);
            Assert.Equal(JsonSerializer.Serialize(result, ReportJson.Options), JsonSerializer.Serialize(exact, ReportJson.Options));
            var over = PageFlowAggregation.Evaluate(input, descriptorBytesRemaining: result.SharedDescriptorBytes - 1);
            Assert.Equal("shared_descriptor_limit", over.Reason); Assert.Equal("skipped", over.Status);
            Assert.Equal(result.DifferenceCount, over.AggregatedDifferenceCount); Assert.Empty(over.Groups); Assert.Null(over.SharedComponents);
            Assert.Equal(0, over.SharedDescriptorBytes);
            var disabled = PageFlowAggregation.Evaluate(input, false); Assert.Null(disabled.SharedComponents); Assert.Null(disabled.AggregatedDifferenceCount);
            foreach (var page in input.Pages)
            foreach (var s in page.Structures)
            {
                var changed = input with { Pages = input.Pages.Select(p => p == page ? p with { Structures = p.Structures.Where(x => x != s).ToArray(), DifferenceCount = p.DifferenceCount - 1 } : p).ToArray() };
                var rejected = PageFlowAggregation.Evaluate(changed);
                Assert.Equal("skipped", rejected.Status); Assert.Empty(rejected.Groups); Assert.Null(rejected.SharedComponents);
                Assert.Equal(rejected.DifferenceCount, rejected.AggregatedDifferenceCount);
            }
        }
    }
}
