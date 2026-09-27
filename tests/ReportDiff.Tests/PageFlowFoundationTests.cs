using System.Collections;
using System.Security.Cryptography;
using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class PageFlowFoundationTests
{
    [Theory]
    [InlineData(PageSpace.Canvas, 1)]
    [InlineData((PageSpace)99, 1)]
    [InlineData(PageSpace.A, 0)]
    public void Coordinates_require_an_original_side_and_physical_page(PageSpace side, int page) =>
        Assert.Throws<ArgumentException>(() => new PageFlowPageKey(side, page));

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(int.MaxValue, 1)]
    public void Bands_reject_invalid_or_overflowing_ranges(int top, int height) =>
        Assert.Throws<ArgumentException>(() => new PageFlowBand(new(PageSpace.A, 1), top, height));

    [Fact]
    public void Descriptor_detaches_pixels_words_and_baselines_and_keeps_faint_nonwhite()
    {
        var key = new PageFlowPageKey(PageSpace.A, 1);
        var baselines = new[] { 3.0 }; var words = new[] { new RowWord(" 1 １ A a ", new(0, 0, 8, 5), baselines) };
        var collector = new PageFlowCollector([key]);
        using (var larger = new Mat(10, 12, MatType.CV_8UC3, Scalar.White))
        using (var roi = new Mat(larger, new Rect(1, 1, 10, 8)))
        {
            roi.Set(2, 3, new Vec3b(255, 254, 255));
            Assert.True(collector.Add(key, roi, new("available", null, words), .5));
            roi.SetTo(Scalar.Black);
        }
        baselines[0] = 99; words[0] = new("CHANGED", new(0, 0, 1, 1), [0]);
        var result = Assert.IsType<PageFlowDocumentDescriptor>(collector.Complete());
        Assert.Equal(0, collector.RetainedPages);
        var page = Assert.Single(result.Pages);
        Assert.Equal("1 １ A a", Assert.Single(page.Lines).Text); Assert.Equal(3, page.Lines[0].Baseline);
        Assert.True(page.RowHasNonwhite(2)); Assert.False(page.RowHasNonwhite(1));
        var bytes = Enumerable.Repeat((byte)255, 10 * 8 * 3).ToArray(); bytes[(2 * 10 + 3) * 3 + 1] = 254;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), page.PixelSha256);
        Assert.False(page.SameRowHashes(page, 2, 1, 1)); Assert.True(page.SameRowHashes(page, 0, 0, 8));
        Assert.Throws<InvalidOperationException>(() => collector.Complete());
    }

    [Fact]
    public void Resource_failure_clears_partial_descriptors_and_never_recovers()
    {
        var first = new PageFlowPageKey(PageSpace.A, 1); var second = new PageFlowPageKey(PageSpace.A, 2);
        var collector = new PageFlowCollector([first, second]);
        using var image = new Mat(10, 10, MatType.CV_8UC3, Scalar.White);
        Assert.True(collector.Add(first, image, new("available", null, []), .5));
        Assert.False(collector.Add(second, image, new("available", null,
            [new(new string('X', (int)PageFlowLimits.MaximumTextCharacters + 1), new(0, 0, 1, 1), [1])]), .5));
        Assert.Equal("flow_text_limit", collector.FailureReason); Assert.Equal(0, collector.RetainedPages);
        Assert.False(collector.Add(second, image, new("available", null, []), .5));
        Assert.Null(collector.Complete()); Assert.Equal(1, collector.Usage.Pages);
    }

    [Fact]
    public void Missing_selected_page_is_not_a_complete_document()
    {
        var collector = new PageFlowCollector([new(PageSpace.A, 1), new(PageSpace.B, 1)]);
        using var image = new Mat(1, 1, MatType.CV_8UC3, Scalar.White);
        Assert.True(collector.Add(new(PageSpace.A, 1), image, new("text_unavailable", "open_failed", []), .5));
        Assert.Null(collector.Complete()); Assert.Equal("missing_page_descriptor", collector.FailureReason);
        Assert.Equal(0, collector.RetainedPages);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void Selected_physical_page_limit_is_checked_before_images(int count, bool allowed)
    {
        var collector = new PageFlowCollector(Enumerable.Range(1, count).Select(n => new PageFlowPageKey(PageSpace.A, n)).ToArray());
        Assert.Equal(allowed ? null : "flow_page_limit", collector.FailureReason);
    }

    [Fact]
    public void Oversized_page_plan_is_not_enumerated()
    {
        var collector = new PageFlowCollector(new UnreadableList<PageFlowPageKey>(257));
        Assert.Equal("flow_page_limit", collector.FailureReason);
    }

    [Theory]
    [InlineData("lines", "flow_line_limit")]
    [InlineData("text", "flow_text_limit")]
    [InlineData("pixels", "flow_pixel_limit")]
    [InlineData("bytes", "flow_descriptor_limit")]
    public void Every_budget_accepts_the_boundary_and_rejects_the_next_unit(string dimension, string reason)
    {
        var limit = new PageFlowUsage(256, 32_768, 2_097_152, 536_870_912, 67_108_864);
        Assert.Null(PageFlowLimits.Exceeded(limit));
        var over = dimension switch { "lines" => limit with { Lines = limit.Lines + 1 },
            "text" => limit with { TextCharacters = limit.TextCharacters + 1 },
            "pixels" => limit with { Pixels = limit.Pixels + 1 }, _ => limit with { DescriptorBytes = limit.DescriptorBytes + 1 } };
        Assert.Equal(reason, PageFlowLimits.Exceeded(over));
    }

    [Fact]
    public void Existing_per_page_row_limit_remains_effective()
    {
        var collector = new PageFlowCollector([new(PageSpace.A, 1)]);
        using var image = new Mat(5000, 10, MatType.CV_8UC3, Scalar.White);
        var words = Enumerable.Range(0, 2001).Select(i => new RowWord("X", new(0, i * 2, 1, i * 2 + 1), [i * 2 + 1])).ToArray();
        Assert.False(collector.Add(new(PageSpace.A, 1), image, new("available", null, words), .5));
        Assert.Equal("row_limit", collector.FailureReason); Assert.Null(collector.Complete());
    }

    [Fact]
    public void Unselected_page_is_not_accepted()
    {
        var collector = new PageFlowCollector([new(PageSpace.A, 1), new(PageSpace.A, 3)]);
        using var image = new Mat(1, 1, MatType.CV_8UC3, Scalar.White);
        Assert.Throws<ArgumentException>(() => collector.Add(new(PageSpace.A, 2), image, new("available", null, []), .5));
        Assert.Equal(0, collector.RetainedPages);
    }

    [Theory]
    [InlineData("missing_map")]
    [InlineData("missing_proof")]
    [InlineData("wrong_page_proof")]
    [InlineData("duplicate_link")]
    [InlineData("overlap")]
    [InlineData("outside_page")]
    [InlineData("wrong_direction")]
    [InlineData("unselected_endpoint")]
    [InlineData("duplicate_proof_page")]
    [InlineData("orphan_proof")]
    [InlineData("unverified")]
    [InlineData("layout_failure")]
    public void Broken_evidence_reverts_every_paired_page(string damage)
    {
        var document = Document();
        var source = Band(PageSpace.A, 1, 100); var target = Band(PageSpace.B, 2, 0);
        var links = new List<PageFlowCandidate> { new(source, target, true, null) };
        var proofs = new List<PageFlowPageProof> { new(1, true, [source]), new(2, true, [target]) };
        switch (damage)
        {
            case "missing_map": proofs[1] = proofs[1] with { MapBuilt = false }; break;
            case "missing_proof": proofs[1] = proofs[1] with { Bands = [] }; break;
            case "wrong_page_proof": proofs[0] = proofs[0] with { Bands = [source, target] }; proofs[1] = proofs[1] with { Bands = [] }; break;
            case "duplicate_link": links.Add(links[0]); break;
            case "overlap": links.Add(new(Band(PageSpace.A, 1, 101), Band(PageSpace.B, 2, 1), true, null)); break;
            case "outside_page": links[0] = links[0] with { Source = Band(PageSpace.A, 1, 290) }; break;
            case "wrong_direction": links[0] = links[0] with { Target = Band(PageSpace.A, 2, 0) }; break;
            case "unselected_endpoint": links[0] = links[0] with { Target = Band(PageSpace.B, 3, 0) }; break;
            case "duplicate_proof_page": proofs[1] = proofs[0]; break;
            case "orphan_proof": proofs[0] = proofs[0] with { Bands = [source, Band(PageSpace.B, 1, 200)] }; break;
            case "unverified": links[0] = links[0] with { Verified = false, Reason = "pixels_differ" }; break;
        }
        var decision = PageFlowRangeGate.Evaluate(document, damage != "layout_failure", "fixed_parts_support", links, proofs);
        Assert.False(decision.Ready); Assert.NotEmpty(decision.Reasons); Assert.Empty(decision.SelectedLinks);
        Assert.All(decision.Selections, s => Assert.Equal("baseline", s.Choice));
    }

    [Fact]
    public void Candidate_limit_stops_before_enumeration_and_keeps_all_baseline_pages()
    {
        var decision = PageFlowRangeGate.Evaluate(Document(), true, null, new UnreadableList<PageFlowCandidate>(129), []);
        Assert.Equal(["flow_candidate_limit"], decision.Reasons); Assert.Empty(decision.SelectedLinks);
        Assert.All(decision.Selections, s => Assert.Equal("baseline", s.Choice));
    }

    [Fact]
    public void Exactly_128_independent_band_pairs_are_accepted_without_truncation()
    {
        var sources = Enumerable.Range(0, 128).Select(i => new PageFlowBand(new(PageSpace.A, 1), i, 1)).ToArray();
        var targets = Enumerable.Range(0, 128).Select(i => new PageFlowBand(new(PageSpace.B, 2), i, 1)).ToArray();
        var links = sources.Zip(targets, (s, t) => new PageFlowCandidate(s, t, true, null)).ToArray();
        var decision = PageFlowRangeGate.Evaluate(Document(), true, null, links, [new(1, true, sources), new(2, true, targets)]);
        Assert.True(decision.Ready); Assert.Equal(128, decision.SelectedLinks.Count);
        Assert.All(decision.Selections, s => Assert.Equal("candidate", s.Choice));
        links[0] = new(null, null, false, "changed_after_decision");
        Assert.True(decision.SelectedLinks[0].Verified);
    }

    [Fact]
    public void Unpaired_endpoint_remains_unpaired_when_every_range_is_proven()
    {
        var source = Band(PageSpace.A, 1, 100); var target = Band(PageSpace.B, 2, 0);
        var decision = PageFlowRangeGate.Evaluate(Document(unpaired: true), true, null, [new(source, target, true, null)],
            [new(1, true, [source]), new(2, false, [target])]);
        Assert.True(decision.Ready); Assert.Equal("candidate", decision.Selections[0].Choice);
        Assert.Equal("unpaired", decision.Selections[1].Choice);
    }

    private static PageFlowBand Band(PageSpace side, int page, int top) => new(new(side, page), top, 20);
    private static PageFlowDocumentDescriptor Document(bool unpaired = false)
    {
        var keys = new List<PageFlowPageKey> { new(PageSpace.A, 1), new(PageSpace.B, 1), new(PageSpace.B, 2) };
        if (!unpaired) keys.Add(new(PageSpace.A, 2));
        var collector = new PageFlowCollector(keys);
        using var image = new Mat(300, 20, MatType.CV_8UC3, Scalar.White);
        foreach (var key in keys) Assert.True(collector.Add(key, image, new("available", null, []), .5));
        return collector.Complete()!;
    }
    private sealed class UnreadableList<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count;
        public T this[int index] => throw new InvalidOperationException("上限超過の要素を読んでいます。");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("上限超過を列挙しています。");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
