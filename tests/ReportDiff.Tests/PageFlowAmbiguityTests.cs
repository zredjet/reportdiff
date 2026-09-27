using ReportDiff.Core;
using System.Runtime.Versioning;
using System.Text.Json;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

public sealed partial class PageFlowCliTests
{
    public static IEnumerable<object[]> AmbiguityCombinations() =>
        new[] { "chain-number", "chain-support-number", "chain-carry-number", "independent-number", "independent-endpoint-number", "independent-tone", "chain-down", "chain-up", "chain-variable", "independent-global" }
        .SelectMany(id => new[] { false, true }.Select(reverse => new object[] { id, reverse,
            !id.Contains("support", StringComparison.Ordinal) && !id.Contains("carry", StringComparison.Ordinal)
                && !id.Contains("endpoint", StringComparison.Ordinal) && !(id == "chain-variable" && reverse) }));

    [Theory]
    [MemberData(nameof(AmbiguityCombinations))]
    public void Ambiguity_combinations_preserve_content_and_all_original_evidence(string id, bool reverse, bool applied)
    {
        using var files = new Files("ambiguity-" + id, reverse);
        var global = id is "chain-down" or "chain-up" or "chain-variable" or "independent-global";
        var yaml = global ? "align: {enabled: true}" : "";
        Assert.Equal(1, files.Run("on", true, extraYaml: yaml).Code); Assert.Equal(1, files.Run("off", false, extraYaml: yaml).Code);
        var report = files.Report("on"); var off = files.Report("off"); var flow = report.PageFlow!;
        Assert.Equal(applied ? "applied" : "skipped", flow.Status);
        if (applied)
        {
            var link = Assert.Single(flow.Links, l => l.Ambiguity is not null); var proof = link.Ambiguity!;
            Assert.Equal(2, link.Id); Assert.False(proof.PixelEqualityProven);
            Assert.Equal("globally_aligned", proof.CoordinateSystem); Assert.Equal(new[] { 100, 200 }, proof.Shifts.Select(s => s.Dy));
            if (id is "chain-number" or "independent-number")
            {
                Assert.Equal(40, report.Pages.Sum(p => p.RawPixels)); Assert.Equal(1, report.Summary.Clusters);
                Assert.False(Assert.Single(flow.NumericMatches!).UsedAsExactSupport);
            }
            if (id == "independent-tone") Assert.True(report.Summary.Clusters > 0);
            var html = File.ReadAllText(files.PathOf("on", "report.html")); Assert.Contains("曖昧な変位の再検証", html);
            foreach (var alt in proof.Alternatives)
            {
                Assert.Null(alt.Source.Image); Assert.Null(alt.Target.Image);
                Assert.All(alt.SourceRows.Concat(alt.TargetRows), r => Assert.Equal(r.Row.Page, r.Counterpart.Page));
            }
        }
        else Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
        foreach (var (p, q) in report.Pages.Zip(off.Pages))
        foreach (var (x, y) in new[] { (p.RawEvidence!.A.Image, q.RawEvidence!.A.Image), (p.RawEvidence.B.Image, q.RawEvidence.B.Image), (p.RawEvidence.Overlay, q.RawEvidence.Overlay) })
            Assert.Equal(File.ReadAllBytes(files.PathOf("on", x)), File.ReadAllBytes(files.PathOf("off", y)));
    }

    [Theory]
    [InlineData("1-3")] [InlineData("1-2")]
    public void Explicit_selection_keeps_unresolved_boundaries(string selection)
    {
        using var files = new Files("shared-shared-chain");
        Assert.Equal(1, files.Run("on", true, extraArgs: ["--pages", selection]).Code);
        var flow = files.Report("on").PageFlow!; Assert.Equal("skipped", flow.Status);
        Assert.All(flow.Links, l => Assert.Null(l.Ambiguity)); Assert.Empty(flow.Aggregation.Groups); Assert.Null(flow.Aggregation.SharedComponents);
    }
}

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class PageFlowAmbiguityTests
{
    [Theory]
    [InlineData("shared-chain", 0)] [InlineData("shared-chain", 10)] [InlineData("shared-chain", -10)]
    [InlineData("shared-and-independent", 0)] [InlineData("shared-and-independent", 10)] [InlineData("shared-and-independent", -10)]
    public void Boundaries_preserve_candidate_ids_and_prove_every_alternative(string id, int dy)
    {
        using var fixture = new PageFlowNumericCoreTests.NumericInput(id, dy, "page-flow-shared"); var document = fixture.Document();
        var plan = PageFlowPlan.Prepare(document, fixture.Read, new(), new() { Enabled = true });
        Assert.True(plan.Decision.Ready); Assert.Equal(plan.Inference.Proposals.Count, plan.Links.Count);
        var proof = Assert.Single(plan.Ambiguity.Proofs); Assert.Equal(1, proof.CandidateIndex); Assert.Equal(2, proof.Boundary.Page);
        Assert.Equal(new[] { 100, 200 }, proof.Shifts.Select(s => s.Dy)); Assert.All(proof.Shifts, s => Assert.Equal(2, s.SupportText.Count));
        Assert.Equal("ambiguous_displacement", plan.Inference.Proposals[proof.CandidateIndex].Reason);
        if (id == "shared-chain")
        {
            Assert.Equal(200, proof.SelectedDy); Assert.Equal(2, proof.CrossingRows.Count); Assert.Empty(proof.Alternatives);
            Assert.Equal("band_verified", plan.Links[proof.CandidateIndex].Status);
            Assert.True(plan.Verifications[proof.CandidateIndex].OriginalComparisons > 0);
        }
        else
        {
            Assert.Null(proof.SelectedDy); Assert.Empty(proof.CrossingRows); Assert.Equal(2, proof.Alternatives.Count);
            Assert.Equal(PageFlowAmbiguity.NonflowReason, plan.Links[proof.CandidateIndex].Reason);
            Assert.Null(plan.Links[proof.CandidateIndex].Source); Assert.Equal(0, plan.Verifications[proof.CandidateIndex].OriginalComparisons);
            Assert.Equal(6, proof.Alternatives.Sum(a => a.SourceRows.Count + a.TargetRows.Count));
            Assert.All(proof.Alternatives.SelectMany(a => a.SourceRows.Concat(a.TargetRows)), r => Assert.Equal(r.Match.Row.Page.Page, r.Match.Counterpart.Page.Page));
        }
        foreach (var row in proof.CrossingRows.Concat(proof.Alternatives.SelectMany(a => a.SourceRows.Concat(a.TargetRows))))
        foreach (var (band, original) in new[] { (row.Match.Row, row.Match.OriginalRow), (row.Match.Counterpart, row.Match.OriginalCounterpart) })
            Assert.Equal(band.Top + (band.Page.Side == PageSpace.B ? dy : 0), original.Top);
    }

    [Theory]
    [InlineData("shared-chain")] [InlineData("shared-and-independent")]
    public void Exact_memory_boundary_accepts_and_one_byte_over_discards_every_resolution(string id)
    {
        using var fixture = new PageFlowNumericCoreTests.NumericInput(id, group: "page-flow-shared"); var document = fixture.Document();
        var options = new RowOptions { Enabled = true }; var plan = PageFlowPlan.Prepare(document, fixture.Read, new(), options);
        var added = plan.Usage.DescriptorBytes - document.Usage.DescriptorBytes; Assert.True(added > 0);
        var exact = new PageFlowDocumentDescriptor(document.Pages.ToArray(), document.Usage with { DescriptorBytes = PageFlowLimits.MaximumDescriptorBytes - added });
        var accepted = PageFlowPlan.Prepare(exact, fixture.Read, new(), options);
        Assert.True(accepted.Decision.Ready); Assert.Equal(PageFlowLimits.MaximumDescriptorBytes, accepted.Usage.DescriptorBytes);
        var over = new PageFlowDocumentDescriptor(document.Pages.ToArray(), exact.Usage with { DescriptorBytes = exact.Usage.DescriptorBytes + 1 });
        var rejected = PageFlowPlan.Prepare(over, fixture.Read, new(), options);
        Assert.False(rejected.Decision.Ready); Assert.Empty(rejected.Ambiguity.Proofs);
        Assert.Contains("flow_descriptor_limit", rejected.Decision.Reasons);
        Assert.Contains(rejected.Links, p => p.Reason == "ambiguous_displacement");
    }

    [Theory]
    [InlineData("shared-chain")] [InlineData("shared-and-independent")]
    public void Selection_partial_document_and_foreign_rows_never_resolve(string id)
    {
        using var fixture = new PageFlowNumericCoreTests.NumericInput(id, group: "page-flow-shared"); var doc = fixture.Document();
        var options = new RowOptions { Enabled = true }; var inference = PageFlowInference.Find(doc, options, 300);
        Assert.Empty(PageFlowAmbiguity.Find(doc, inference, options, 300, true, long.MaxValue).Proofs);
        var missing = new PageFlowDocumentDescriptor(doc.Pages.Skip(1).ToArray(), doc.Usage);
        Assert.Empty(PageFlowAmbiguity.Find(missing, inference, options, 300, false, long.MaxValue).Proofs);
        var layout = inference.Layouts.A[1];
        var foreign = inference with { Layouts = inference.Layouts with { A = inference.Layouts.A.Select(l => l == layout
            ? l with { Body = l.Body.Select(r => r with { Text = r.Text + " changed" }).ToArray() } : l).ToArray() } };
        Assert.Empty(PageFlowAmbiguity.Find(doc, foreign, options, 300, false, long.MaxValue).Proofs);
        Assert.Empty(PageFlowAmbiguity.Find(doc, inference with { Proposals = inference.Proposals.Skip(1).ToArray() }, options, 300, false, long.MaxValue).Proofs);
        Assert.Empty(PageFlowAmbiguity.Find(doc, inference, options with { MinSupportBands = 3 }, 300, false, long.MaxValue).Proofs);
    }

    [Fact]
    public void Nonflow_proof_cannot_be_reused_for_another_document_or_candidate()
    {
        using var fixture = new PageFlowNumericCoreTests.NumericInput("shared-and-independent", group: "page-flow-shared"); var doc = fixture.Document();
        var plan = PageFlowPlan.Prepare(doc, fixture.Read, new(), new() { Enabled = true });
        var candidates = plan.Links.Select(l => new PageFlowCandidate(l.Source, l.Target, l.Status == "band_verified", l.Reason)).ToArray();
        var pages = plan.Pages.Select(p => new PageFlowPageProof(p.Number, p.Built?.Status == "built",
            p.Built?.Removed.Where(r => r.Proof == "verified_carry_range").Select(r => r.Band).ToArray() ?? [])).ToArray();
        Assert.True(PageFlowRangeGate.Evaluate(doc, true, null, candidates, pages, plan.Nonflow.Proofs, plan.Ambiguity.Proofs).Ready);
        var other = new PageFlowDocumentDescriptor(doc.Pages.ToArray(), doc.Usage);
        Assert.False(PageFlowRangeGate.Evaluate(other, true, null, candidates, pages, plan.Nonflow.Proofs, plan.Ambiguity.Proofs).Ready);
        Assert.False(PageFlowRangeGate.Evaluate(doc, true, null, candidates.Skip(1).Concat(candidates.Take(1)).ToArray(), pages, plan.Nonflow.Proofs, plan.Ambiguity.Proofs).Ready);
        Assert.False(PageFlowRangeGate.Evaluate(doc, true, null, candidates, pages, plan.Nonflow.Proofs, []).Ready);
        Assert.False(PageFlowRangeGate.Evaluate(doc, true, null, candidates, pages, plan.Nonflow.Proofs, plan.Ambiguity.Proofs.Concat(plan.Ambiguity.Proofs).ToArray()).Ready);
    }
}
