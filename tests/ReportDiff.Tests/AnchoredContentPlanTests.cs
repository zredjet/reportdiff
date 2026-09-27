using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class AnchoredContentPlanTests
{
    private static readonly RowOptions Options = new() { Enabled = true, CarryEnabled = true };

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Fixed_pdf_creates_bound_plan_without_reading_images_or_claiming_adoption(bool reverse)
    {
        using var input = new Input(reverse); var doc = input.Document(); var prior = input.Prior(doc); var reads = input.Reads;
        Assert.False(prior.Decision.Ready);
        var settings = input.Settings(); using var plan = Required(prior, settings);
        Assert.Equal(reads, input.Reads); Assert.False(plan.Evidence.PixelEqualityProven);
        Assert.Equal(0, plan.Evidence.SamePageDisplacementSupport); Assert.Equal(1, plan.Evidence.CandidateId);
        Assert.Equal(2, plan.Evidence.BeforeSupport.Count); Assert.Equal(2, plan.Evidence.BetweenSupport.Count);
        Assert.Equal(4, plan.Evidence.NextPageSupport.Count); Assert.Equal(2, plan.Evidence.Crossing.Count);
        Assert.Equal(15_784_200, plan.SurfacePixels); Assert.Equal(64, plan.Fingerprint.Length);
        var side = reverse ? PageSpace.B : PageSpace.A; var edited = reverse ? PageSpace.A : PageSpace.B;
        Assert.Equal(new OriginalRowSpan(new(side, 1), 700, 200), plan.Evidence.Source);
        Assert.Equal(new OriginalRowSpan(new(edited, 2), 300, 200), plan.Evidence.Target);
        Assert.Equal(new[] { 500, 800 }, plan.Evidence.Causes.Select(c => c.Span.Top));
        Assert.All(plan.Surfaces, s => { Assert.Equal(new(999, 1250), s.Size); Assert.Equal(side, s.Anchor.Side); });
        Assert.All(plan.Displays, d => Assert.Equal(new(999, 1450), d.Size));
        Assert.Equal(reverse ? new[] { 500, 800 } : new[] { 500, 1000 }, plan.Displays[0].Segments
            .Where(s => s.Role == AnchoredBandRole.Cause).Select(s => s.Band.Top));
        var carried = Assert.Single(plan.Displays[0].Segments, s => s.Role == AnchoredBandRole.Carry);
        Assert.Equal(reverse ? 900 : 800, carried.Band.Top);
        Assert.Equal(300, Assert.Single(plan.Displays[1].Segments, s => s.Role == AnchoredBandRole.Carry).Band.Top);
        CheckRows(plan, doc);
        plan.VerifyBinding(prior, settings);
        // 新経路の計画作成で従来の見送り・CLIの未成立状態を書き換えない。
        Assert.False(prior.Decision.Ready); Assert.False(plan.Evidence.PixelEqualityProven);
        Assert.Throws<NotSupportedException>(() => ((IList<AnchoredContentPiece>)plan.Surfaces[0].Pieces).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<AnchoredRowMatch>)plan.Evidence.CommonRows).Clear());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Reference_binding_rejects_equal_but_foreign_documents_settings_and_plans(bool reverse)
    {
        using var input = new Input(reverse); var doc = input.Document(); var prior = input.Prior(doc); var settings = input.Settings();
        using var plan = Required(prior, settings);
        var otherDocument = new PageFlowDocumentDescriptor(doc.Pages.Reverse().ToArray(), doc.Usage);
        var other = input.Prior(otherDocument); using var duplicate = Required(other, settings);
        Assert.Equal(plan.Fingerprint, duplicate.Fingerprint);
        Assert.Throws<InvalidOperationException>(() => plan.VerifyBinding(other, settings));
        Assert.Throws<InvalidOperationException>(() => plan.VerifyBinding(input.Prior(doc), settings));
        Assert.Throws<InvalidOperationException>(() => plan.VerifyBinding(prior, input.Settings()));
        foreach (var variant in new[] { input.Settings(hashA: Hash("other pdf")), input.Settings(textHash: Hash("other text settings")),
            input.Settings(rows: Options with { MinImprovement = 1 }), input.Settings(overlap: .75) })
        {
            using var changed = Required(prior, variant); Assert.NotEqual(plan.Fingerprint, changed.Fingerprint);
            Assert.Throws<InvalidOperationException>(() => plan.VerifyBinding(prior, variant));
        }
        var before = plan.Budget.ExistingBytes; plan.Dispose(); plan.Dispose();
        Assert.Equal(before, plan.Budget.UsedBytes);
        Assert.Throws<ObjectDisposedException>(() => plan.VerifyBinding(prior, settings));
        Assert.Throws<ObjectDisposedException>(() => plan.Surfaces);
        Assert.Throws<ObjectDisposedException>(() => plan.Budget.TryReserve(AnchoredAllocation.MaskRun, 1, out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Plan_reservation_accepts_exact_remaining_bytes_and_rejects_one_byte_less(bool reverse)
    {
        using var input = new Input(reverse); var doc = input.Document(); var settings = input.Settings();
        using var baseline = Required(input.Prior(doc), settings);
        var needed = baseline.Budget.PeakBytes - baseline.Budget.ExistingBytes; Assert.True(needed > 0);
        var exactDoc = new PageFlowDocumentDescriptor(doc.Pages.ToArray(), doc.Usage with
            { DescriptorBytes = PageFlowLimits.MaximumDescriptorBytes - needed });
        using var exact = Required(input.Prior(exactDoc), settings);
        Assert.Equal(PageFlowLimits.MaximumDescriptorBytes, exact.Budget.PeakBytes);
        var overDoc = new PageFlowDocumentDescriptor(doc.Pages.ToArray(), exactDoc.Usage with
            { DescriptorBytes = exactDoc.Usage.DescriptorBytes + 1 });
        Assert.Null(AnchoredContentPlan.Prepare(input.Prior(overDoc), settings, out var reason));
        Assert.Equal("flow_descriptor_limit", reason);
    }

    [Fact]
    public void Planned_pixels_use_existing_document_usage_and_the_whole_c_and_d_pair()
    {
        using var input = new Input(); var doc = input.Document(); var settings = input.Settings();
        using var baseline = Required(input.Prior(doc), settings);
        var added = baseline.SurfacePixels - doc.Usage.Pixels;
        var exactDoc = new PageFlowDocumentDescriptor(doc.Pages.ToArray(), doc.Usage with { Pixels = PageFlowLimits.MaximumPixels - added });
        using var exact = Required(input.Prior(exactDoc), settings); Assert.Equal(PageFlowLimits.MaximumPixels, exact.SurfacePixels);
        var overDoc = new PageFlowDocumentDescriptor(doc.Pages.ToArray(), exactDoc.Usage with { Pixels = exactDoc.Usage.Pixels + 1 });
        Assert.Null(AnchoredContentPlan.Prepare(input.Prior(overDoc), settings, out var reason)); Assert.Equal("flow_pixel_limit", reason);
    }

    [Theory]
    [InlineData("disabled")] [InlineData("carry_disabled")] [InlineData("selection")] [InlineData("alignment")]
    [InlineData("dpi")] [InlineData("strict")] [InlineData("loose")] [InlineData("ink")]
    [InlineData("cluster")] [InlineData("move")] [InlineData("exclude")] [InlineData("regions")]
    [InlineData("different_pages")] [InlineData("support")] [InlineData("maximum")]
    public void Unsupported_scope_or_stale_inference_never_returns_a_plan(string kind)
    {
        using var input = new Input(); var prior = input.Prior(input.Document());
        var p = new ComparisonParameters(); var second = p; var rows = Options;
        switch (kind)
        {
            case "disabled": rows = rows with { Enabled = false, CarryEnabled = false }; break;
            case "carry_disabled": rows = rows with { CarryEnabled = false }; break;
            case "dpi": p = p with { Dpi = 150 }; break;
            case "strict": p = p with { Diff = p.Diff with { MaxShiftMm = 0, EdgeTolerance = 0 } }; break;
            case "loose": p = p with { Diff = p.Diff with { MaxShiftMm = .30 } }; break;
            case "ink": p = p with { Ink = p.Ink with { ContrastThreshold = 26 } }; break;
            case "cluster": p = p with { Cluster = p.Cluster with { MinPixels = 5 } }; break;
            case "move": p = p with { Move = p.Move with { MinScore = .99 } }; break;
            case "exclude": p = p with { Exclude = [new(0, 0, 1, 1)] }; break;
            case "regions": p = p with { Regions = [new(0, "領域", new(0, 0, 1, 1), "compare", new())] }; break;
            case "different_pages": second = p with { Diff = p.Diff with { MaxShiftMm = .3 } }; break;
            case "support": rows = rows with { MinSupportBands = 3 }; break;
            case "maximum": rows = rows with { MaxShiftMm = 10 }; break;
        }
        var settings = input.Settings(p, kind == "different_pages" ? second : p, rows, selection: kind == "selection", alignment: kind == "alignment");
        Assert.Null(AnchoredContentPlan.Prepare(prior, settings, out var reason)); Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("capacity7")] [InlineData("capacity8")] [InlineData("capacity9")] [InlineData("capacity10")]
    [InlineData("later-short")] [InlineData("later-supported")] [InlineData("both-later")]
    [InlineData("before-short")] [InlineData("between-short")] [InlineData("content-tone")]
    [InlineData("carry-tone")] [InlineData("repeated")] [InlineData("reordered")] [InlineData("faint-support")]
    public void Other_fixed_fixtures_are_not_silently_added_to_the_new_scope(string id)
    {
        foreach (var reverse in new[] { false, true })
        {
            using var input = new Input(reverse, id); var prior = input.Prior(input.Document()); var ready = prior.Decision.Ready;
            Assert.Null(AnchoredContentPlan.Prepare(prior, input.Settings(), out var reason)); Assert.NotNull(reason);
            Assert.Equal(ready, prior.Decision.Ready);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Content_changes_do_not_turn_text_evidence_into_a_pixel_proof(bool reverse)
    {
        // 新しい検出正例ではない。帯の画素証明を後段へ残した型であることの対照。
        using var input = new Input(reverse, changeCarry: true); var doc = input.Document(); var prior = input.Prior(doc);
        using var plan = Required(prior, input.Settings()); Assert.False(plan.Evidence.PixelEqualityProven);
        using var source = input.Read(plan.Evidence.Source.Page); using var target = input.Read(plan.Evidence.Target.Page);
        var candidate = new PageFlowInference.Proposal(plan.Evidence.Source.ToBand(), plan.Evidence.Target.ToBand(),
            plan.Evidence.Crossing.Select(r => r.Source.Line.Text).ToArray(), 0, "candidate", null);
        var verified = PageFlowBandVerifier.Verify(candidate, doc.Pages.Single(p => p.Key == plan.Evidence.Source.Page),
            doc.Pages.Single(p => p.Key == plan.Evidence.Target.Page), source, target, new());
        Assert.Equal("skipped", verified.Proposal.Status); Assert.Equal("nonidentical_band_not_proven", verified.Proposal.Reason);
    }

    private static AnchoredContentPlan Required(PageFlowPlan prior, AnchoredContentSettings settings)
    { var plan = AnchoredContentPlan.Prepare(prior, settings, out var reason); Assert.True(plan is not null, reason); return plan!; }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Invisible_text_does_not_create_a_nonwhite_omission(bool reverse)
    {
        using var input = new Input(reverse, whiteFirstCause: true); var prior = input.Prior(input.Document());
        Assert.Equal("prepared", prior.Inference.Layouts.Status);
        Assert.Null(AnchoredContentPlan.Prepare(prior, input.Settings(), out var reason)); Assert.Equal("white_cause", reason);
    }

    private static void CheckRows(AnchoredContentPlan plan, PageFlowDocumentDescriptor doc)
    {
        var rows = doc.Pages.ToDictionary(p => p.Key, p => new int[p.Size.Height]);
        foreach (var s in plan.Surfaces)
        foreach (var p in s.Pieces)
        foreach (var span in new[] { p.A, p.B }.OfType<OriginalRowSpan>())
            for (var y = span.Top; y < span.Bottom; y++) rows[span.Page][y]++;
        foreach (var c in plan.Evidence.Causes) for (var y = c.Span.Top; y < c.Span.Bottom; y++) rows[c.Span.Page][y]++;
        Assert.All(rows.Values.SelectMany(x => x), count => Assert.Equal(1, count));
        foreach (var d in plan.Displays)
        foreach (var side in new[] { PageSpace.A, PageSpace.B })
        {
            var covered = new int[d.OriginalSize.Height];
            foreach (var p in d.Segments.Select(s => side == PageSpace.A ? s.Band.A : s.Band.B).OfType<OriginalRowSpan>())
                for (var y = p.Top; y < p.Bottom; y++) covered[y]++;
            Assert.All(covered, count => Assert.Equal(1, count));
        }
    }

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private sealed class Input : IDisposable
    {
        private readonly PdfReader a, b;
        private readonly PdfTextReader ta, tb;
        private readonly string hashA, hashB;
        private readonly bool reverse, changeCarry, whiteFirstCause;
        internal int Reads { get; private set; }
        internal Input(bool reverse = false, string id = "same-page-two", bool changeCarry = false, bool whiteFirstCause = false)
        {
            this.reverse = reverse; this.changeCarry = changeCarry; this.whiteFirstCause = whiteFirstCause;
            var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-same-page-support", id);
            var pa = Path.Combine(dir, reverse ? "b.pdf" : "a.pdf"); var pb = Path.Combine(dir, reverse ? "a.pdf" : "b.pdf");
            a = PdfReader.Open(pa); b = PdfReader.Open(pb); ta = new(pa); tb = new(pb);
            hashA = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pa)));
            hashB = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pb)));
        }
        internal Mat Read(PageFlowPageKey key)
        {
            Reads++;
            using var page = (key.Side == PageSpace.A ? a : b).ReadPage(key.Page, 300);
            var pixels = page.Pixels.Clone();
            if (changeCarry && key.Page == 2 && key.Side == (reverse ? PageSpace.A : PageSpace.B))
                pixels.Set(350, 650, new Vec3b(254, 255, 255));
            if (whiteFirstCause && key.Page == 1 && key.Side == (reverse ? PageSpace.A : PageSpace.B))
            {
                using var band = new Mat(pixels, new Rect(0, 500, pixels.Width, 100)); band.SetTo(Scalar.White);
            }
            return pixels;
        }
        internal PageFlowDocumentDescriptor Document()
        {
            var keys = new[] { new PageFlowPageKey(PageSpace.A, 1), new(PageSpace.B, 1), new(PageSpace.A, 2), new(PageSpace.B, 2) };
            var collector = new PageFlowCollector(keys);
            foreach (var key in keys)
            {
                using var image = Read(key);
                Assert.True(collector.Add(key, image, (key.Side == PageSpace.A ? ta : tb).ReadRowWords(key.Page, image.Size(), 300), .5));
            }
            return collector.Complete()!;
        }
        internal PageFlowPlan Prior(PageFlowDocumentDescriptor doc) => PageFlowPlan.Prepare(doc, Read, new(), Options);
        internal AnchoredContentSettings Settings(ComparisonParameters? p = null, ComparisonParameters? second = null, RowOptions? rows = null,
            string? hashA = null, string? textHash = null, double overlap = .5, bool selection = false, bool alignment = false) =>
            new(p ?? new(), second ?? p ?? new(), rows ?? Options, hashA ?? this.hashA, hashB, textHash ?? Hash("fixed-text-options"), overlap, selection, alignment);
        public void Dispose() { ta.Dispose(); tb.Dispose(); a.Dispose(); b.Dispose(); }
    }
}
