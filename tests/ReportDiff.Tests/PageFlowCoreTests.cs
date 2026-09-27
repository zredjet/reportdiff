using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class PageFlowCoreTests
{
    [Theory]
    [InlineData(false, 10, 5, true)]
    [InlineData(true, 10, 5, true)]
    [InlineData(false, 12, 6, false)]
    [InlineData(true, 12, 6, false)]
    [InlineData(false, 16, 8, true)]
    [InlineData(true, 16, 8, true)]
    public void Pipeline_uses_actual_structure_ids_and_preserves_unpaired_incompleteness(bool reverse, int rows, int count, bool complete)
    {
        using var fixture = new FlowImages(rows, reverse);
        var plan = fixture.Prepare();
        Assert.True(plan.Decision.Ready, string.Join(',', plan.Decision.Reasons));
        var pages = fixture.Compare(plan);
        var result = plan.Aggregate(pages, false);
        Assert.Equal("grouped", result.Status); Assert.Equal(count, result.DifferenceCount);
        Assert.Equal(1, result.AggregatedDifferenceCount); Assert.Equal(complete, result.AggregatedDifferenceCountComplete);
        Assert.Equal(count, Assert.Single(result.Groups).Structures.Count);
        Assert.All(result.Groups[0].Structures, r => Assert.Contains(pages.Single(p => p.Number == r.Page).Structures, s => s.Reference == r));
        Assert.All(plan.Verifications, v => Assert.Equal(12, v.OriginalComparisons));
        var disabled = plan.Aggregate(pages, false, false);
        Assert.Null(disabled.AggregatedDifferenceCount); Assert.Null(disabled.AggregatedDifferenceCountComplete);
        Assert.Empty(disabled.Groups); Assert.Equal(count, disabled.DifferenceCount);
    }

    [Theory]
    [InlineData(.1, 2, 300, 0)]
    [InlineData(5, 2, 72, 0)]
    [InlineData(5, 2, 300, 1)]
    [InlineData(20, 4, 300, 0)]
    public void Inference_respects_actual_dpi_shift_and_support(double shift, int support, int dpi, int candidates)
    {
        using var fixture = new FlowImages();
        var result = PageFlowInference.Find(fixture.Describe(), new() { MaxShiftMm = shift, MinSupportBands = support }, dpi);
        Assert.Equal("prepared", result.Layouts.Status); Assert.Equal(candidates, result.Proposals.Count);
    }

    [Fact]
    public void Disabled_rows_do_not_read_images_or_allow_partial_comparison()
    {
        using var fixture = new FlowImages();
        var plan = PageFlowPlan.Prepare(fixture.Describe(), _ => throw new InvalidOperationException("unexpected read"), new(), new());
        Assert.False(plan.Decision.Ready); Assert.Contains("rows_disabled", plan.Decision.Reasons);
        Assert.Throws<InvalidOperationException>(() => plan.Compare(1, fixture.Image(PageSpace.A, 1), fixture.Image(PageSpace.B, 1)));
    }

    [Fact]
    public void Faint_band_difference_cannot_be_proven_by_tolerance_or_exclusion()
    {
        using var fixture = new FlowImages();
        // A末尾とB次ページの帯は、一画素の255→254以外は一致する。
        fixture.Image(PageSpace.B, 2).Set(85, 130, new Vec3b(254, 255, 255));
        var parameters = new ComparisonParameters { Dpi = 72, Exclude = [new(0, 0, 1000, 1000)] };
        var plan = fixture.Prepare(parameters);
        Assert.False(plan.Decision.Ready);
        Assert.Equal("nonidentical_band_not_proven", Assert.Single(plan.Links).Reason);
        Assert.Equal(0, Assert.Single(plan.Verifications).OriginalComparisons);
        Assert.All(plan.Decision.Selections, p => Assert.Equal("baseline", p.Choice));
    }

    [Fact]
    public void Late_page_failure_returns_the_earlier_successful_page_to_baseline()
    {
        using var fixture = new FlowImages();
        // 後ろのページの本文端だけ1px延びる。送り帯そのものは完全一致のまま。
        fixture.Image(PageSpace.B, 2).Set(280, 12, new Vec3b(0, 0, 0));
        var plan = fixture.Prepare();
        Assert.Equal("band_verified", Assert.Single(plan.Links).Status);
        Assert.Equal("built", plan.Pages[0].Built!.Status);
        Assert.False(plan.Decision.Ready); Assert.Empty(plan.Decision.SelectedLinks);
        Assert.All(plan.Decision.Selections, p => Assert.Equal("baseline", p.Choice));
        Assert.Throws<InvalidOperationException>(() => plan.Compare(1, fixture.Image(PageSpace.A, 1), fixture.Image(PageSpace.B, 1)));
    }

    [Fact]
    public void Reread_change_fails_preparation_instead_of_silently_falling_back()
    {
        using var fixture = new FlowImages(); var descriptors = fixture.Describe();
        fixture.Image(PageSpace.A, 1).Set(0, 0, new Vec3b(254, 255, 255));
        var error = Assert.Throws<InvalidOperationException>(() => PageFlowPlan.Prepare(descriptors, fixture.Read,
            new() { Dpi = 72 }, new() { Enabled = true }));
        Assert.Contains("flow_original_changed", error.Message);
    }

    [Fact]
    public void Change_after_range_commit_fails_final_comparison()
    {
        using var fixture = new FlowImages(); var plan = fixture.Prepare();
        fixture.Image(PageSpace.B, 2).Set(0, 0, new Vec3b(254, 255, 255));
        var error = Assert.Throws<InvalidOperationException>(() => plan.Compare(2, fixture.Image(PageSpace.A, 2), fixture.Image(PageSpace.B, 2)));
        Assert.Contains("flow_original_changed", error.Message);
    }

    [Fact]
    public void Hash_check_accepts_noncontinuous_roi_and_does_not_take_ownership()
    {
        using var fixture = new FlowImages(); var page = fixture.Describe().Pages[0];
        using var larger = new Mat(402, 142, MatType.CV_8UC3, Scalar.White);
        using var roi = new Mat(larger, new Rect(1, 1, 140, 400)); fixture.Image(PageSpace.A, 1).CopyTo(roi);
        PageFlowBandVerifier.VerifyOriginal(page, roi);
        Assert.False(roi.IsDisposed);
    }

    [Fact]
    public void Selection_never_reads_unselected_or_bridges_physical_pages()
    {
        using var fixture = new FlowImages(16);
        var document = fixture.Describe(k => k.Page != 2);
        var plan = PageFlowPlan.Prepare(document, k => k.Page == 2 ? throw new InvalidOperationException("unselected") : fixture.Read(k),
            new() { Dpi = 72 }, new() { Enabled = true });
        Assert.False(plan.Decision.Ready); Assert.Empty(plan.Links);
        Assert.Equal(new[] { 1, 3 }, plan.Decision.Selections.Select(s => s.Page));
    }

    [Fact]
    public void Unpaired_coverage_and_completeness_come_from_proof_not_callers_claim()
    {
        using var fixture = new FlowImages(12); var plan = fixture.Prepare(); var pages = fixture.Compare(plan);
        var changed = pages.Select(p => p.Paired ? p : p with { Complete = true, UnpairedCovered = false }).ToArray();
        var result = plan.Aggregate(changed, false);
        Assert.Equal("grouped", result.Status); Assert.False(result.DifferenceCountComplete);
        Assert.False(result.AggregatedDifferenceCountComplete);
        Assert.Throws<ArgumentException>(() => plan.Aggregate(pages.Take(1).ToArray(), false));
    }

    [Fact]
    public void Aggregation_keeps_content_cluster_and_never_raises_false_completeness()
    {
        using var fixture = new FlowImages(); var plan = fixture.Prepare(); var pages = fixture.Compare(plan).ToArray();
        pages[0] = pages[0] with { Clusters = 1, DifferenceCount = pages[0].DifferenceCount + 1, Complete = false };
        var result = plan.Aggregate(pages, false);
        Assert.Equal("grouped", result.Status); Assert.Equal(6, result.DifferenceCount); Assert.Equal(2, result.AggregatedDifferenceCount);
        Assert.False(result.AggregatedDifferenceCountComplete);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("excluded")]
    [InlineData("reference")]
    [InlineData("side")]
    public void Invalid_actual_structures_keep_unaggregated_count(string fault)
    {
        using var fixture = new FlowImages(); var plan = fixture.Prepare(); var pages = fixture.Compare(plan).ToArray();
        var page = pages[0]; var structures = page.Structures.ToList(); var first = structures[0];
        if (fault == "missing") structures.RemoveAt(0);
        if (fault == "duplicate") structures.Add(first);
        if (fault == "excluded") structures[0] = first with { Excluded = true };
        if (fault == "reference") structures[0] = first with { Reference = first.Reference with { Page = 2 } };
        if (fault == "side") structures[0] = first with { A = first.B, B = first.A };
        pages[0] = page with { Structures = structures, DifferenceCount = page.Clusters + structures.Count(s => !s.Excluded) };
        var result = plan.Aggregate(pages, false);
        Assert.Equal("skipped", result.Status); Assert.Empty(result.Groups); Assert.Equal(result.DifferenceCount, result.AggregatedDifferenceCount);
    }

    [Fact]
    public void Candidate_limit_discards_all_proposals_before_original_rereads()
    {
        using var fixture = new FlowImages(128 * 6, reorder: true);
        var plan = PageFlowPlan.Prepare(fixture.Describe(), _ => throw new InvalidOperationException("unexpected reread"),
            new() { Dpi = 150 }, new() { Enabled = true });
        Assert.False(plan.Decision.Ready); Assert.Empty(plan.Links); Assert.Empty(plan.Decision.SelectedLinks);
        Assert.Contains("flow_candidate_limit", plan.Decision.Reasons);
        Assert.Equal(128, plan.Decision.Selections.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_content_change_and_source_exclusion_pass_through_C_and_D(bool exclude)
    {
        using var fixture = new FlowImages();
        Cv2.Rectangle(fixture.Image(PageSpace.B, 1), new Rect(24, 248, 3, 8), new Scalar(128, 128, 128), -1);
        var exclusions = new List<RectMm>();
        if (exclude) exclusions.Add(PageMap.CanvasMillimeters(new(22, 206, 8, 14), 72));
        var parameters = new ComparisonParameters { Dpi = 72, Exclude = exclusions,
            Regions = [new(0, "本文", PageMap.CanvasMillimeters(new(0, 80, 140, 240), 72), "compare", new())] };
        var plan = fixture.Prepare(parameters);
        Assert.True(plan.Decision.Ready, string.Join(',', plan.Decision.Reasons));
        exclusions.Clear(); // 準備後の呼び出し元の設定配列変更は採用済みの条件を変えない。
        var pages = fixture.Compare(plan);
        Assert.Equal(exclude ? 0 : 1, pages.Sum(p => p.Clusters));
        var result = plan.Aggregate(pages, false);
        Assert.Equal("grouped", result.Status); Assert.Equal(exclude ? 1 : 2, result.AggregatedDifferenceCount);
    }

    [Theory]
    [InlineData(4)] [InlineData(-6)]
    public void Aligned_descriptors_prove_original_bands_and_keep_G_aggregation_separate(int dy)
    {
        using var fixture = new FlowImages();
        var document = fixture.DescribeAligned(dy, out var originals);
        try
        {
            var plan = PageFlowPlan.Prepare(document, k => originals[k].Clone(), new() { Dpi = 72 }, new() { Enabled = true });
            Assert.True(plan.Decision.Ready);
            var logical = Assert.Single(plan.Links); var original = Assert.Single(plan.OriginalLinks);
            Assert.Equal(logical.Source, original.Source); Assert.Equal(logical.Target!.Top + dy, original.Target!.Top);
            Assert.Equal(12, Assert.Single(plan.Verifications).OriginalComparisons);
            var pages = fixture.Compare(plan); Assert.Equal(1, plan.Aggregate(pages, false).AggregatedDifferenceCount);
            using var comparison = plan.Compare(2, fixture.Image(PageSpace.A, 2), fixture.Image(PageSpace.B, 2));
            foreach (var structure in comparison.Display.StructuralChanges.Where(s => s.SourceB is not null))
            {
                var g = comparison.Describe().Structures.Single(s => s.Reference.StructuralChangeId == structure.Id);
                Assert.Equal(g.B!.Top + dy, structure.SourceB!.Bounds.Top);
                Assert.Equal(g.Dy, structure.DisplacementPx?.Dy);
            }
            var key = new PageFlowPageKey(PageSpace.B, 2);
            plan.VerifyOriginal(key, originals[key]);
            Assert.Contains("flow_original_changed", Assert.Throws<InvalidOperationException>(() => plan.VerifyOriginal(key, fixture.Image(PageSpace.B, 2))).Message);
            Assert.Contains("flow_comparison_changed", Assert.Throws<InvalidOperationException>(() => plan.Compare(2, fixture.Image(PageSpace.A, 2), originals[key])).Message);
            Assert.Throws<InvalidOperationException>(() => PageFlowPlan.Prepare(document, fixture.Read, new() { Dpi = 72 }, new() { Enabled = true }));
            originals[key].Set(0, 0, new Vec3b(254, 255, 255));
            Assert.Throws<InvalidOperationException>(() => plan.VerifyOriginal(key, originals[key]));
        }
        finally { foreach (var image in originals.Values) image.Dispose(); }
    }

    [Theory]
    [InlineData(4, 398)] [InlineData(-6, 0)]
    public void Aligned_original_endpoints_must_not_be_clipped(int dy, int top)
    {
        using var fixture = new FlowImages(); var document = fixture.DescribeAligned(dy, out var originals);
        try
        {
            var descriptor = document.Pages.Single(p => p.Key == new PageFlowPageKey(PageSpace.B, 2));
            Assert.Null(descriptor.MapOriginalBand(new(descriptor.Key, top, 2)));
            Assert.Equal(new(descriptor.Key, 80 + dy, 40), descriptor.MapOriginalBand(new(descriptor.Key, 80, 40)));
            Assert.Equal(fixture.Describe().Usage.DescriptorBytes + 2 * 512, document.Usage.DescriptorBytes);
        }
        finally { foreach (var image in originals.Values) image.Dispose(); }
    }

    private sealed class FlowImages : IDisposable
    {
        private readonly Dictionary<PageFlowPageKey, (Mat Image, RowTextResult Text)> pages = [];
        internal FlowImages(int count = 10, bool reverse = false, bool reorder = false)
        {
            var original = Enumerable.Range(0, count).Select(i => "ROW" + i).ToList();
            var revised = original.ToList();
            if (reorder) revised = original.Chunk(6).SelectMany(c => new[] { c[2], c[3], c[0], c[1], c[4], c[5] }).ToList();
            else revised.Insert(2, "NEW");
            Add(reverse ? PageSpace.B : PageSpace.A, original); Add(reverse ? PageSpace.A : PageSpace.B, revised);
        }
        private void Add(PageSpace side, List<string> rows)
        {
            for (var offset = 0; offset < rows.Count; offset += 6)
            {
                var image = new Mat(400, 140, MatType.CV_8UC3, Scalar.White); var words = new List<RowWord>();
                Line("HEADER", 10); Line("SUBHEAD", 30); Line("FOOTER", 350); Line("END", 370);
                foreach (var (text, i) in rows.Skip(offset).Take(6).Select((t, i) => (t, i)))
                {
                    var top = 80 + i * 40;
                    Cv2.Rectangle(image, new Rect(12, top, 2, 40), Scalar.Black, -1);
                    Cv2.Rectangle(image, new Rect(12, top + 38, 108, 2), Scalar.Black, -1);
                    Line(text, top + 8);
                }
                pages.Add(new(side, offset / 6 + 1), (image, new("available", null, words)));
                void Line(string text, int top)
                {
                    words.Add(new(text, new(24, top, 104, top + 10), [top + 9]));
                    for (var i = 0; i < text.Length; i++)
                        Cv2.Rectangle(image, new Rect(24 + i * 8, top, 3, 3 + text[i] % 8), Scalar.Black, -1);
                }
            }
        }
        internal Mat Image(PageSpace side, int page) => pages[new(side, page)].Image;
        internal Mat Read(PageFlowPageKey key) => pages[key].Image.Clone();
        internal PageFlowDocumentDescriptor Describe(Func<PageFlowPageKey, bool>? selected = null)
        {
            var keys = pages.Keys.Where(selected ?? (_ => true)).ToArray(); var collector = new PageFlowCollector(keys);
            foreach (var key in keys) Assert.True(collector.Add(key, pages[key].Image, pages[key].Text, .5));
            return collector.Complete()!;
        }
        internal PageFlowDocumentDescriptor DescribeAligned(int dy, out Dictionary<PageFlowPageKey, Mat> originals)
        {
            originals = []; var collector = new PageFlowCollector(pages.Keys.ToArray());
            foreach (var (key, entry) in pages)
            {
                var input = key.Side == PageSpace.A ? entry.Image.Clone()
                    : PageMap.Global(entry.Image.Size(), entry.Image.Size(), entry.Image.Size(), new(0, dy)).Render(entry.Image, key.Side);
                originals.Add(key, input);
                Assert.True(key.Side == PageSpace.A ? collector.Add(key, input, entry.Text, .5)
                    : collector.AddAligned(key, input, new(0, -dy), entry.Text, .5));
            }
            return collector.Complete()!;
        }
        internal PageFlowPlan Prepare(ComparisonParameters? parameters = null) => PageFlowPlan.Prepare(Describe(), Read,
            parameters ?? new() { Dpi = 72 }, new() { Enabled = true });
        internal IReadOnlyList<PageFlowAggregation.Page> Compare(PageFlowPlan plan) => plan.Pages.Select(p =>
        {
            if (plan.Decision.Selections.Single(s => s.Page == p.Number).Choice == "unpaired")
                return new PageFlowAggregation.Page(p.Number, false, 0, 0, false, p.UnpairedCovered, []);
            using var comparison = plan.Compare(p.Number, Image(PageSpace.A, p.Number), Image(PageSpace.B, p.Number));
            Assert.Equal(comparison.Content.RawPixels, comparison.Display.Comparison.RawPixels);
            Assert.Equal(comparison.Content.Clusters.Count, comparison.Display.Comparison.Clusters.Count);
            return comparison.Describe();
        }).ToArray();
        public void Dispose() { foreach (var page in pages.Values) page.Image.Dispose(); }
    }
}
