using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class AnchoredComparisonTests
{
    [Theory]
    [InlineData(false, "original", 0)] [InlineData(true, "original", 0)]
    [InlineData(false, "tone", 3807)] [InlineData(true, "tone", 3807)]
    [InlineData(false, "context", 956)] [InlineData(true, "context", 956)]
    public void Both_directions_keep_content_counts_and_display_references(bool reverse, string variant, int raw)
    {
        using var input = new Input(reverse, variant); using var plan = input.Plan(); var before = plan.Budget.UsedBytes;
        using (var result = plan.Compare(input.Read, input.Text, out var reason))
        {
            Assert.Null(reason); Assert.NotNull(result); Assert.Equal(12, result.BandVerification.OriginalComparisons);
            Assert.Equal("band_verified", result.BandVerification.Proposal.Status);
            Assert.All(result.Adoption, a => Assert.True(a.Accepted));
            Assert.Equal(new[] { 4, 2, 2 }, result.Adoption[0].SupportBands);
            Assert.Equal(new[] { 2, 4, 2 }, result.Adoption[1].SupportBands);
            Assert.Equal(raw, result.Contents.Sum(c => c.RawPixels));
            Assert.Equal(raw == 0 ? 0 : 1, result.Contents.Sum(c => c.Clusters.Count));
            Assert.Equal(6, result.Structures.Count); Assert.Equal(2, result.Structures.Count(s => s.Role == "cause"));
            Assert.Equal(2, result.Structures.Count(s => s.Role == "carry"));
            Assert.All(result.Structures, s => Assert.Equal(s.Role != "cause", s.ComparedInContent));
            Assert.Equal(raw == 0 ? 6 : 7, result.Aggregation.DifferenceCount);
            Assert.Equal(raw == 0 ? 2 : 3, result.Aggregation.AggregatedDifferenceCount);
            Assert.Equal(199800, result.OmittedBandPixels); Assert.True(result.OmittedNonwhitePixels > 0);
            var content = result.Contents[0];
            for (var page = 1; page <= 2; page++)
            {
                using var display = result.ProjectDisplay(page);
                Assert.All(display.Structures, s => Assert.Equal(page, s.Structure.Reference.Page));
                Assert.Equal(page == 1 ? raw : variant == "context" ? 238 : 0, display.DisplayRawPixels);
                Assert.All(display.Parts, p => Assert.Equal(new ContentClusterKey(new(1), 1), p.Content));
                CheckIndependentProjection(plan, result, display);
            }
            if (variant == "context")
            {
                Assert.Equal(3, content.Parts.Count); Assert.Equal(new[] { 1, 2 }, content.Parts.Select(p => p.DisplayPage).Distinct());
                var reference = Assert.Single(content.Parts, p => p.DisplayPage == 2); Assert.Equal(238, reference.Pixels);
                Assert.Equal(300, reference.DisplayBounds.Top);
            }
            if (Environment.GetEnvironmentVariable("REPORTDIFF_ANCHORED_EVIDENCE") is { Length: > 0 } output)
                SaveEvidence(Path.Combine(output, variant + (reverse ? "-ba" : "-ab")), input.Read, plan, result);
        }
        Assert.Equal(before, plan.Budget.UsedBytes);
    }

    [Theory]
    [InlineData(false, "carry")] [InlineData(true, "carry")]
    [InlineData(false, "thin")] [InlineData(true, "thin")]
    [InlineData(false, "one-ink")] [InlineData(true, "one-ink")]
    [InlineData(false, "flat")] [InlineData(true, "flat")]
    [InlineData(false, "later-tone")] [InlineData(true, "later-tone")]
    public void Band_and_second_page_failures_release_the_entire_candidate(bool reverse, string variant)
    {
        using var input = new Input(reverse, variant); using var plan = input.Plan(variant == "later-tone" ? 1 : .05);
        var before = plan.Budget.UsedBytes;
        Assert.Null(plan.Compare(input.Read, input.Text, out var reason)); Assert.NotNull(reason);
        if (variant == "carry") Assert.Equal("nonidentical_band_not_proven", reason);
        else Assert.StartsWith("page_2_", reason);
        Assert.Equal(before, plan.Budget.UsedBytes);
    }

    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(7)] [InlineData(10)]
    public void Changed_donor_and_read_failure_are_errors_with_no_leaked_reservation(int at)
    {
        using var input = new Input(); using var plan = input.Plan(); var before = plan.Budget.UsedBytes; var reads = 0;
        Assert.Throws<InvalidOperationException>(() => plan.Compare(key =>
        {
            var image = input.Read(key); if (++reads == at) image.Set(0, 0, new Vec3b(254, 255, 255)); return image;
        }, input.Text, out _));
        Assert.Equal(before, plan.Budget.UsedBytes); reads = 0;
        Assert.Throws<IOException>(() => plan.Compare(key => ++reads == at ? throw new IOException("読込対照") : input.Read(key), input.Text, out _));
        Assert.Equal(before, plan.Budget.UsedBytes);
    }

    [Theory]
    [InlineData(1, false)] [InlineData(4, false)] [InlineData(1, true)] [InlineData(4, true)]
    public void Original_words_must_still_match_the_collected_line_descriptor(int at, bool unavailable)
    {
        using var input = new Input(); using var plan = input.Plan(); var before = plan.Budget.UsedBytes; var calls = 0;
        Assert.Throws<InvalidOperationException>(() => plan.Compare(input.Read, key =>
        {
            var text = input.Text(key);
            if (++calls != at) return text;
            if (unavailable) return new("unavailable", "対照", []);
            return text with { Words = text.Words.Select((w, i) => i == 0 ? w with { Text = "変更された本文" } : w).ToArray() };
        }, out _));
        Assert.Equal(at, calls); Assert.Equal(before, plan.Budget.UsedBytes);
        calls = 0;
        using var result = plan.Compare(input.Read, key => { calls++; return input.Text(key); }, out _);
        Assert.NotNull(result); Assert.Equal(4, calls);
    }

    [Fact]
    public void Text_line_overlap_uses_the_same_positive_range_as_the_collector()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnchoredContentSettings(new(), new(),
            new() { Enabled = true, CarryEnabled = true }, new('a', 64), new('b', 64), new('c', 64), 0));
    }

    [Fact]
    public void Additional_budget_shortage_releases_every_completed_content()
    {
        using var input = new Input(false, "context"); using var plan = input.Plan();
        var before = plan.Budget.UsedBytes;
        using (var result = plan.Compare(input.Read, input.Text, out _)) Assert.NotNull(result);
        var required = plan.Budget.PeakBytes - before;
        Assert.True(plan.Budget.TryReserveBytes(plan.Budget.RemainingBytes - required, out var blocker));
        using (blocker)
        {
            var blocked = plan.Budget.UsedBytes;
            using (var exact = plan.Compare(input.Read, input.Text, out _)) Assert.NotNull(exact);
            Assert.Equal(blocked, plan.Budget.UsedBytes);
            Assert.True(plan.Budget.TryReserveBytes(1, out var extra));
            using (extra)
            {
                Assert.Null(plan.Compare(input.Read, input.Text, out var reason)); Assert.Equal("flow_descriptor_limit", reason);
                Assert.Equal(blocked + 1, plan.Budget.UsedBytes);
            }
        }
        Assert.Equal(before, plan.Budget.UsedBytes);
    }

    [Fact]
    public void Dense_sparse_runs_fail_before_growth_and_release_partial_buffers()
    {
        using var input = new Input(); using var plan = input.Plan(); var size = plan.Surfaces[0].Size;
        var pixels = new byte[size.Width * size.Height];
        for (var i = 0; i < pixels.Length; i += 2) pixels[i] = 255;
        var raw = new Mat(size, MatType.CV_8UC1); Marshal.Copy(pixels, 0, raw.Data, pixels.Length);
        using var comparison = new PageComparison("same", [], pixels.Count(p => p != 0), 1, 0, 0,
            raw, new(size, MatType.CV_8UC1, Scalar.Black), []);
        Assert.True(plan.Budget.TryReserveBytes(plan.Budget.RemainingBytes - 12000, out var blocker));
        using (blocker)
        {
            var before = plan.Budget.UsedBytes;
            Assert.Throws<AnchoredContentResourceLimitException>(() => new AnchoredContentResult(plan, plan.Surfaces[0], comparison));
            Assert.Equal(before, plan.Budget.UsedBytes); Assert.False(comparison.RawMask.IsDisposed);
        }
    }

    [Fact]
    public void Deferred_display_failure_keeps_owned_content_until_the_caller_discards_the_attempt()
    {
        using var input = new Input(false, "context"); using var plan = input.Plan();
        var initial = plan.Budget.UsedBytes; var result = plan.Compare(input.Read, input.Text, out _)!;
        Assert.True(plan.Budget.TryReserveBytes(plan.Budget.RemainingBytes, out var blocker));
        using (blocker)
        {
            var before = plan.Budget.UsedBytes;
            Assert.Throws<AnchoredContentResourceLimitException>(() => result.ProjectDisplay(2));
            Assert.Equal(before, plan.Budget.UsedBytes); Assert.Equal(956, result.Contents[0].RawPixels);
        }
        result.Dispose(); Assert.Equal(initial, plan.Budget.UsedBytes);
        Assert.Throws<ObjectDisposedException>(() => result.ProjectDisplay(1));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Rendering_keeps_the_anchor_and_disposes_each_donor_before_the_next(bool reverse)
    {
        using var input = new Input(reverse); using var plan = input.Plan();
        Mat? donor = null;
        for (var page = 1; page <= 2; page++)
        {
            using var images = plan.RenderContent(page, key =>
            {
                Assert.True(donor is null || donor.IsDisposed); donor = input.Read(key); return donor;
            });
            Assert.True(donor!.IsDisposed);
            using var anchor = input.Read(plan.Surfaces[page - 1].Anchor);
            Assert.Equal(0, Cv2.Norm(anchor, reverse ? images.B : images.A, NormTypes.INF));
        }
    }

    [Fact]
    public void Missing_movement_proof_preserves_the_content_as_changed()
    {
        using var input = new Input(); using var plan = input.Plan(); var size = plan.Surfaces[0].Size;
        using var a = new Mat(size, MatType.CV_8UC3, Scalar.White); using var b = a.Clone();
        MovementTests.Draw(a, new(400, 350)); MovementTests.Draw(b, new(424, 350));
        using var comparison = PageComparer.Compare(a, b, new(), true, retainProjection: true);
        ((Dictionary<int, MovementProjectionProof>)comparison.ProjectionData!.Movements).Clear();
        using var content = new AnchoredContentResult(plan, plan.Surfaces[0], comparison);
        Assert.NotEmpty(content.Annotations);
        Assert.All(content.Annotations, a => { Assert.Equal("changed", a.Kind); Assert.Equal("movement_proof_unavailable", a.Reason); });
    }

    [Theory]
    [InlineData(false, 1)] [InlineData(true, 1)] [InlineData(false, 2)] [InlineData(true, 2)]
    public void Tiny_reference_and_unclustered_noise_are_not_filtered_again(bool reverse, int clusterCount)
    {
        using var input = new Input(reverse); using var plan = input.Plan();
        var size = plan.Surfaces[0].Size; var count = size.Width * size.Height;
        var ids = new int[count]; var bytes = new byte[count];
        for (var id = 1; id <= clusterCount; id++)
        foreach (var y in new[] { 699, 700 })
        foreach (var x in clusterCount == 1 ? new[] { 620, 621 } : new[] { 619 + id, 621 + id })
        { ids[y * size.Width + x] = id; bytes[y * size.Width + x] = 255; }
        var labels = new Mat(size, MatType.CV_8UC1); Marshal.Copy(bytes, 0, labels.Data, bytes.Length);
        bytes[700 * size.Width + 650] = 255;
        var raw = new Mat(size, MatType.CV_8UC1); Marshal.Copy(bytes, 0, raw.Data, bytes.Length);
        var clusters = Enumerable.Range(1, clusterCount).Select(id => new DifferenceCluster(id,
            new(619 + id, 699, clusterCount == 1 ? 2 : 3, 2), 4)).ToArray();
        using var comparison = new PageComparison("different", clusters, 4 * clusterCount + 1, 1, 0, 2, raw, labels, [])
            { ProjectionData = new(size, ids, new Dictionary<int, MovementProjectionProof>()) };
        using var content = new AnchoredContentResult(plan, plan.Surfaces[0], comparison);
        Assert.Equal(3 * clusterCount, content.Parts.Count); Assert.All(content.Parts, p => Assert.Equal(2, p.Pixels));
        using var d1 = AnchoredProjection.Display(plan, 1, [content], []);
        using var d2 = AnchoredProjection.Display(plan, 2, [content], []);
        Assert.Equal(4 * clusterCount + 1, d1.DisplayRawPixels); Assert.Equal(2 * clusterCount + 1, d2.DisplayRawPixels);
        Assert.Equal(Enumerable.Range(1, clusterCount), content.Parts.Select(p => p.Content.ClusterId).Distinct());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Replaying_the_same_plan_keeps_ids_masks_and_counts(bool reverse)
    {
        using var input = new Input(reverse, "context"); using var plan = input.Plan(); string? prior = null;
        for (var i = 0; i < 2; i++)
        {
            using var result = plan.Compare(input.Read, input.Text, out _)!;
            using var display = result.ProjectDisplay(2);
            var signature = System.Text.Json.JsonSerializer.Serialize(new
            {
                result.Aggregation, Parts = result.Contents[0].Parts, result.Contents[0].Clusters,
                Mask = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(MatBuffers.Bytes(display.RawMask)))
            });
            if (prior is not null) Assert.Equal(prior, signature); prior = signature;
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Higher_improvement_requirement_does_not_hide_the_content_change(bool reverse)
    {
        using var input = new Input(reverse, "tone"); using var plan = input.Plan(1); var before = plan.Budget.UsedBytes;
        Assert.Null(plan.Compare(input.Read, input.Text, out var reason)); Assert.Equal("page_1_low_improvement", reason);
        Assert.Equal(before, plan.Budget.UsedBytes);
    }

    [Theory]
    [InlineData(350, true)] [InlineData(685, false)] [InlineData(720, false)]
    public void Real_movement_requires_all_windows_to_have_one_display_mapping(int y, bool uniform)
    {
        using var input = new Input(); using var plan = input.Plan(); var size = plan.Surfaces[0].Size;
        using var a = new Mat(size, MatType.CV_8UC3, Scalar.White); using var b = a.Clone();
        MovementTests.Draw(a, new(400, y)); MovementTests.Draw(b, new(424, y));
        using var reservations = new AnchoredReservations(plan.Budget);
        using var comparison = PageComparer.Compare(a, b, new(), true, retainProjection: true, projectionBudget: reservations);
        Assert.NotEmpty(comparison.Clusters); Assert.All(comparison.Clusters, c => Assert.Equal("moved", c.Kind));
        using var content = new AnchoredContentResult(plan, plan.Surfaces[0], comparison);
        Assert.All(content.Annotations, annotation =>
        {
            Assert.Equal(uniform ? "moved" : "changed", annotation.Kind);
            Assert.Equal(uniform ? null : "nonuniform_display_mapping", annotation.Reason);
            if (uniform) Assert.Equal(new MovementShift(24, 0), annotation.Shift);
            else { Assert.Null(annotation.Shift); Assert.Empty(annotation.RelatedClusterIds); }
        });
    }

    private static void CheckIndependentProjection(AnchoredContentPlan plan, AnchoredComparisonResult result, AnchoredDisplayResult display)
    {
        var map = plan.Displays.Single(d => d.Page == display.Page); var expected = new byte[map.Size.Width * map.Size.Height];
        foreach (var c in result.Contents)
        foreach (var run in c.Runs.Where(r => r.Flags.HasFlag(AnchoredMaskFlags.Raw)))
        {
            var piece = plan.Surfaces.Single(s => s.Id == c.Id).Pieces.Single(p => run.Y >= p.Top && run.Y < p.Top + p.Height);
            foreach (var source in new[] { piece.A, piece.B })
            {
                if (source is null || source.Page.Page != display.Page) continue;
                var oy = source.Top + run.Y - piece.Top;
                var segment = map.Segments.Single(s => (source.Page.Side == PageSpace.A ? s.Band.A : s.Band.B) is { } span
                    && oy >= span.Top && oy < span.Bottom);
                var span = source.Page.Side == PageSpace.A ? segment.Band.A! : segment.Band.B!;
                var dy = segment.Band.Top + oy - span.Top;
                Array.Fill(expected, (byte)255, dy * map.Size.Width + run.X, run.Length);
            }
        }
        var actual = new byte[expected.Length]; Marshal.Copy(display.RawMask.Data, actual, 0, actual.Length); Assert.Equal(expected, actual);
    }

    internal static void SaveEvidence(string folder, Func<PageFlowPageKey, Mat> read, AnchoredContentPlan plan, AnchoredComparisonResult result)
    {
        Directory.CreateDirectory(folder);
        foreach (var side in new[] { PageSpace.A, PageSpace.B })
        for (var page = 1; page <= 2; page++)
        { using var image = read(new(side, page)); Save(image, $"O{page}-{side}.png"); }
        for (var page = 1; page <= 2; page++)
        {
            using (var images = plan.RenderContent(page, read)) { Save(images.A, $"C{page}-A.png"); Save(images.B, $"C{page}-B.png"); }
            using (var images = plan.RenderDisplay(page, read)) { Save(images.A, $"D{page}-A.png"); Save(images.B, $"D{page}-B.png"); }
            using var mask = new Mat(plan.Surfaces[page - 1].Size, MatType.CV_8UC1, Scalar.Black);
            foreach (var run in result.Contents[page - 1].Runs.Where(r => r.Flags.HasFlag(AnchoredMaskFlags.Raw)))
            { using var band = new Mat(mask, new Rect(run.X, run.Y, run.Length, 1)); band.SetTo(Scalar.White); }
            Save(mask, $"C{page}-raw.png");
            using var display = result.ProjectDisplay(page); Save(display.RawMask, $"D{page}-raw.png");
        }
        File.WriteAllText(Path.Combine(folder, "result.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            result.Adoption, result.Aggregation, result.OmittedBandPixels, result.OmittedNonwhitePixels,
            Contents = result.Contents.Select(c => new { c.Id.OwnerPage, c.RawPixels, c.NoiseDropped, c.Clusters, c.Parts, c.Annotations }),
            result.Structures
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        void Save(Mat image, string file) => File.WriteAllBytes(Path.Combine(folder, file), image.ImEncode(".png"));
    }

    internal sealed class Input : IDisposable
    {
        private readonly PdfReader a, b; private readonly PdfTextReader ta, tb;
        private readonly bool reverse; private readonly string variant;
        internal Input(bool reverse = false, string variant = "original")
        {
            this.reverse = reverse; this.variant = variant;
            var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-same-page-support/same-page-two");
            var pa = Path.Combine(dir, reverse ? "b.pdf" : "a.pdf"); var pb = Path.Combine(dir, reverse ? "a.pdf" : "b.pdf");
            a = PdfReader.Open(pa); b = PdfReader.Open(pb); ta = new(pa); tb = new(pb);
        }
        internal Mat Read(PageFlowPageKey key)
        {
            using var page = (key.Side == PageSpace.A ? a : b).ReadPage(key.Page, 300);
            var image = page.Pixels.Clone(); var edited = key.Side == (reverse ? PageSpace.A : PageSpace.B);
            if (edited && key.Page == 1 && variant == "tone") Tone(3, 166);
            if (edited && key.Page == 1 && variant == "context") Cv2.Rectangle(image, new Rect(580, 798, 240, 2), Scalar.Black, -1);
            if (edited && key.Page == 2 && variant == "carry") image.Set(350, 650, new Vec3b(254, 255, 255));
            if (edited && key.Page == 2 && variant == "later-tone") Tone(4, 166);
            if (key.Page == 2 && variant is "thin" or "one-ink" or "flat")
                for (var i = 0; i < 4; i++)
                {
                    if (variant == "one-ink" && i == 0) continue;
                    var slot = (edited ? 2 : 0) + i;
                    if (variant == "flat") { using var band = new Mat(image, new Rect(0, 300 + slot * 100, image.Width, 100)); band.SetTo(Scalar.Black); }
                    else Tone(slot, 248);
                }
            return image;
            void Tone(int slot, int value)
            { using var band = new Mat(image, new Rect(0, 300 + slot * 100, image.Width, 100)); band.ConvertTo(band, MatType.CV_8UC3, (255 - value) / 255.0, value); }
        }
        internal RowTextResult Text(PageFlowPageKey key) => (key.Side == PageSpace.A ? ta : tb).ReadRowWords(key.Page, new(999, 1250), 300);
        internal AnchoredContentPlan Plan(double minimumImprovement = .05)
        {
            var keys = new[] { new PageFlowPageKey(PageSpace.A, 1), new(PageSpace.B, 1), new(PageSpace.A, 2), new(PageSpace.B, 2) };
            var collector = new PageFlowCollector(keys);
            foreach (var key in keys)
            {
                using var image = Read(key);
                Assert.True(collector.Add(key, image, (key.Side == PageSpace.A ? ta : tb).ReadRowWords(key.Page, image.Size(), 300), .5));
            }
            var options = new RowOptions { Enabled = true, CarryEnabled = true, MinImprovement = minimumImprovement };
            var prior = PageFlowPlan.Prepare(collector.Complete()!, Read, new(), options);
            var settings = new AnchoredContentSettings(new(), new(), options, new('a', 64), new('b', 64), new('c', 64));
            var plan = AnchoredContentPlan.Prepare(prior, settings, out var reason); Assert.Null(reason); return Assert.IsType<AnchoredContentPlan>(plan);
        }
        public void Dispose() { ta.Dispose(); tb.Dispose(); a.Dispose(); b.Dispose(); }
    }
}
