using System.Runtime.Versioning;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;

namespace ReportDiff.Cli;

/// <summary>送り有効時だけの事前検証。全体採否を確定してからページ結果を保存する。</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
internal sealed class PageFlowRun
{
    private readonly PagePairingPlan pairing;
    private readonly bool selectionLimited;
    private readonly int dpi;
    private readonly AppSettings settings;
    private readonly List<string> reasons = [];
    private readonly List<PageFlowAggregation.Page> compared = [];
    private readonly Dictionary<(int Link, bool Source), string> images = [];
    private readonly Dictionary<int, AlignmentResult> alignments = [];
    public AlignmentResult Alignment(int page) => alignments[page];
    public PageFlowPlan? Plan { get; private set; }
    public Dictionary<int, PageFlowAdoptionResult> Adoptions { get; } = [];
    public bool Applied { get; private set; }

    private PageFlowRun(PagePairingPlan pairing, bool selectionLimited, int dpi, AppSettings settings)
    { this.pairing = pairing; this.selectionLimited = selectionLimited; this.dpi = dpi; this.settings = settings; }

    public static PageFlowRun Prepare(ComparisonInput a, ComparisonInput b, PagePairingPlan pairing,
        AppSettings settings, bool selectionLimited, ConsoleProgress? progress)
    {
        var run = new PageFlowRun(pairing, selectionLimited, a.Dpi, settings);
        if (a.Format != InputFormat.Pdf || b.Format != InputFormat.Pdf) return Skip("not_pdf");
        if (pairing.Pages.Count > PageFlowLimits.MaximumSelectedPages) return Skip("flow_page_limit");
        var keys = pairing.Pages.SelectMany(p => new[] { p.HasA ? new PageFlowPageKey(PageSpace.A, p.PageNumber) : null,
            p.HasB ? new PageFlowPageKey(PageSpace.B, p.PageNumber) : null }.OfType<PageFlowPageKey>()).ToArray();
        var collector = new PageFlowCollector(keys);
        if (collector.FailureReason is { } preflight) return Skip(preflight);
        foreach (var page in pairing.Pages)
        {
            progress?.Flow(page.PageNumber);
            using var ia = page.HasA ? a.ReadPage(page.PageNumber) : null;
            using var ib = page.HasB ? b.ReadPage(page.PageNumber) : null;
            if (ia is not null && ib is not null)
            {
                if (ia.Pixels.Size() != ib.Pixels.Size()) return Skip("size_mismatch");
                var alignment = GlobalAligner.Estimate(ia.Pixels, ib.Pixels, settings.ForPage(page.PageNumber, a.Dpi), settings.Align);
                run.alignments.Add(page.PageNumber, alignment);
                if (alignment.Status == "applied" && (alignment.EstimatedShiftPx!.Dx != 0 || pairing.Pages.Any(p => !p.CanCompare)))
                    return Skip("global_alignment_applied");
            }
            foreach (var (input, image, side) in new[] { (a, ia, PageSpace.A), (b, ib, PageSpace.B) })
            {
                if (image is null) continue;
                var alignment = run.alignments.GetValueOrDefault(page.PageNumber);
                var shift = alignment?.Status == "applied" ? alignment.EstimatedShiftPx : null;
                var map = PageMap.Global(image.Pixels.Size(), image.Pixels.Size(), image.Pixels.Size(), shift);
                var text = input.ReadRowWords(page.PageNumber, map, side);
                var added = side == PageSpace.B && shift is not null
                    ? collector.AddAligned(new(side, page.PageNumber), image.Pixels, shift, text, settings.Text.MinLineOverlap)
                    : collector.Add(new(side, page.PageNumber), image.Pixels, text, settings.Text.MinLineOverlap);
                if (!added) return Skip(collector.FailureReason!);
            }
        }
        var document = collector.Complete();
        if (document is null) return Skip(collector.FailureReason!);
        run.Plan = PageFlowPlan.PrepareForPages(document, ReadOriginal, n => settings.ForPage(n, a.Dpi), settings.Rows, selectionLimited, settings.Align.Enabled);
        if (!run.Plan.Decision.Ready) { run.reasons.AddRange(run.Plan.Decision.Reasons); return run; }
        foreach (var page in pairing.Pages.Where(p => p.CanCompare))
        {
            using var ia = ReadOriginal(new(PageSpace.A, page.PageNumber)); using var ib = ReadOriginal(new(PageSpace.B, page.PageNumber));
            var alignment = run.alignments[page.PageNumber];
            var shift = alignment.Status == "applied" ? alignment.EstimatedShiftPx : null;
            var map = PageMap.Global(ia.Size(), ib.Size(), ia.Size(), shift);
            using var correctedB = shift is null ? null : map.Render(ib, PageSpace.B);
            var adoption = PageFlowAdoption.Evaluate(run.Plan, page.PageNumber, ia, correctedB ?? ib, settings.ForPage(page.PageNumber, a.Dpi), settings.Rows,
                a.ReadRowWords(page.PageNumber, map, PageSpace.A),
                b.ReadRowWords(page.PageNumber, map, PageSpace.B), settings.Text.MinLineOverlap);
            run.Adoptions.Add(page.PageNumber, adoption);
        }
        run.Applied = run.Adoptions.Values.All(d => d.Accepted);
        if (!run.Applied) run.reasons.Add("adoption_not_ready");
        return run;

        PageFlowRun Skip(string reason) { run.reasons.Add(reason); return run; }
        Mat ReadOriginal(PageFlowPageKey key)
        {
            using var image = (key.Side == PageSpace.A ? a : b).ReadPage(key.Page);
            run.Plan?.VerifyOriginal(key, image.Pixels);
            return image.Pixels.Clone();
        }
    }

    public void VerifyAndSave(ReportWriter writer, int page, PageSpace side, Mat original)
    {
        Plan?.VerifyOriginal(new(side, page), original);
        if (Plan is null) return;
        var originalLinks = Plan.OriginalLinks;
        for (var i = 0; i < originalLinks.Count; i++)
        foreach (var source in new[] { true, false })
        {
            var band = source ? originalLinks[i].Source : originalLinks[i].Target;
            if (band is null || band.Page.Page != page || band.Page.Side != side) continue;
            images.Add((i + 1, source), writer.AddFlowBand(i + 1, source, original, band));
        }
    }

    public void Record(ReportPage report, PageFlowComparison? flow = null) => compared.Add(flow?.Describe()
        ?? new(report.Page, report.Status is not ("only_in_a" or "only_in_b"), report.Clusters.Count,
            report.DifferenceCount, report.DifferenceCountComplete, false, []));

    public ReportPageFlow Complete()
    {
        var decision = Applied ? Plan!.Aggregate(compared, selectionLimited)
            : PageFlowAggregation.Evaluate(new(false, selectionLimited, [], [], compared));
        var links = Plan?.Links.Select((l, i) => new ReportFlowLink(i + 1, Applied && l.Status == "band_verified" ? "carried" : "skipped",
            Plan.Verifications[i].Proposal.Status == "band_verified" ? "verified"
                : Plan.Verifications[i].Proposal.Reason is "nonidentical_band_not_proven" or "original_substitution_not_proven" ? "failed" : "not_performed",
            l.Reason, Endpoint(Plan.OriginalLinks[i].Source, l.Source, i + 1, true), Endpoint(Plan.OriginalLinks[i].Target, l.Target, i + 1, false), l.Support,
            Plan.Verifications[i].OriginalComparisons,
            l.Source is not null && l.Target is not null ? new(ProofParameters(l.Source.Page.Page), ProofParameters(l.Target.Page.Page), true, false) : null,
            SamePageProof(i), AmbiguityProof(i))).ToArray() ?? [];
        var groups = decision.Groups.Select((g, i) => new ReportFlowGroup(i + 1, Ref(g.Cause), g.Structures.Select(Ref).ToArray(),
            g.Links.Select(l => Array.FindIndex(Plan!.Links.ToArray(), p => p.Source == l.Source && p.Target == l.Target) + 1).ToArray(),
            g.AuxiliaryBands.Select(b => links.SelectMany(l => new[] { l.Source, l.Target }).OfType<ReportFlowEndpoint>()
                .Single(e => e.Page == b.Page.Page && e.Side == (b.Page.Side == PageSpace.A ? "a" : "b") && e.BoundsPx.Y == b.Top && e.BoundsPx.H == b.Height)).ToArray(), g.Balance)).ToArray();
        var shared = decision.SharedComponents?.Select((c, i) => new ReportFlowSharedComponent(i + 1, "globally_aligned", c.Pages,
            c.Causes.Select(x => new ReportFlowSharedCause(Ref(x.Reference), x.Rows, x.Delta)).ToArray(), c.Structures.Select(Ref).ToArray(),
            c.Movements.Select(x => new ReportFlowSharedMovement(Ref(x.Structure), x.Causes.Select(Ref).ToArray(), x.Dy)).ToArray(),
            c.Links.Select(x => new ReportFlowSharedLink(Array.FindIndex(Plan!.Links.ToArray(), l => l.Source == x.Link.Source && l.Target == x.Link.Target) + 1,
                x.Structures.Select(Ref).ToArray(), x.Causes.Select(Ref).ToArray(), x.Rows, Auxiliary(x.AuxiliaryBands))).ToArray(),
            c.Balance, Auxiliary(c.AuxiliaryBands))).ToArray();
        return new(Applied ? "applied" : "skipped", reasons.AsReadOnly(), Plan?.Decision.Ready ?? false, selectionLimited, links,
            pairing.Pages.Select(p => new ReportFlowPage(p.PageNumber, !p.CanCompare ? "unpaired" : Applied ? "candidate" : "baseline",
                Plan?.Pages.Single(x => x.Number == p.PageNumber).Built?.Reason,
                Applied && Plan!.Pages.Single(x => x.Number == p.PageNumber).UnpairedCovered, Adoptions.GetValueOrDefault(p.PageNumber))).ToArray(),
            new(decision.Status, decision.Reason, decision.DifferenceCount, decision.AggregatedDifferenceCount,
                decision.DifferenceCountComplete, decision.AggregatedDifferenceCountComplete, groups, shared),
            Plan?.NumericRows.Proofs.Count > 0 ? Plan.NumericRows.Proofs.Select(p => new ReportFlowNumericMatch(p.Id,
                Applied ? "applied" : "not_applied", "numeric_tokens_with_exact_neighbors", false, false, NumericRow(p.A), NumericRow(p.B),
                p.ChangedTokenIndices, p.Anchors.Select(a => new ReportFlowNumericAnchor(NumericRow(a.A), NumericRow(a.B))).ToArray())).ToArray() : null);

        IReadOnlyList<ReportFlowEndpoint>? Auxiliary(IReadOnlyList<PageFlowBand>? bands) => bands?.Select(b =>
            links.SelectMany(l => new[] { l.Source, l.Target }).OfType<ReportFlowEndpoint>()
                .Single(e => e.Page == b.Page.Page && e.Side == (b.Page.Side == PageSpace.A ? "a" : "b")
                    && e.BoundsPx.Y == b.Top && e.BoundsPx.H == b.Height)).ToArray();

        ReportFlowNumericRow NumericRow(PageFlowNumericRows.Row row) => new(row.Text, Endpoint(row.OriginalBand, row.Band, 0, true)!);

        ReportFlowEndpoint? Endpoint(PageFlowBand? band, PageFlowBand? logical, int link, bool source)
        {
            if (band is null || Plan is null) return null;
            var descriptor = Plan.Inference.Layouts.A.Concat(Plan.Inference.Layouts.B).Single(p => p.Page.Key == band.Page).Page;
            var rectangle = new Rect(0, band.Top, descriptor.Original.Size.Width, band.Height); var mm = PageMap.CanvasMillimeters(rectangle, dpi);
            var references = Applied ? compared.Where(p => p.Number == band.Page.Page).SelectMany(p => p.Structures)
                .Where(s => s.A == logical || s.B == logical).Select(s => Ref(s.Reference)).ToArray() : [];
            return new(band.Page.Side == PageSpace.A ? "a" : "b", band.Page.Page, "original_top_left",
                new(0, band.Top, descriptor.Original.Size.Width, band.Height), new(mm.X, mm.Y, mm.W, mm.H), images.GetValueOrDefault((link, source)), references);
        }
        ReportFlowNonflow? SamePageProof(int index)
        {
            var proof = Plan!.Nonflow.Proofs.SingleOrDefault(p => p.CandidateIndex == index);
            return proof is null ? null : new(true, true, false,
                proof.SourceRows.Select(Match).ToArray(), proof.TargetRows.Select(Match).ToArray());
            ReportFlowSamePageRow Match(PageFlowNonflow.Match match) => new(
                Endpoint(match.OriginalRow, match.Row, 0, true)!, Endpoint(match.OriginalCounterpart, match.Counterpart, 0, true)!);
        }
        ReportFlowAmbiguity? AmbiguityProof(int index)
        {
            var proof = Plan!.Ambiguity.Proofs.SingleOrDefault(p => p.CandidateIndex == index);
            return proof is null ? null : new(proof.Boundary.Side == PageSpace.A ? "a" : "b", proof.Boundary.Page,
                proof.SelectedDy is null ? "all_alternatives_same_page" : "complete_crossing_rows", "globally_aligned", proof.SelectedDy,
                proof.Shifts, proof.CrossingRows.Select(Row).ToArray(), proof.Alternatives.Select(a => new ReportFlowAlternative(a.Dy,
                    Endpoint(a.OriginalSource, a.Source, 0, true)!, Endpoint(a.OriginalTarget, a.Target, 0, true)!,
                    a.SourceRows.Select(Row).ToArray(), a.TargetRows.Select(Row).ToArray())).ToArray(), false);
            ReportFlowBoundaryRow Row(PageFlowAmbiguity.Row row) => new(row.Text,
                Endpoint(row.Match.OriginalRow, row.Match.Row, 0, true)!, Endpoint(row.Match.OriginalCounterpart, row.Match.Counterpart, 0, true)!);
        }
        ComparisonParameters ProofParameters(int page)
        {
            var p = settings.ForPage(page, dpi);
            return p with { Exclude = [], Regions = p.Regions.Where(r => r.Mode == "compare").ToArray() };
        }
        static ReportFlowReference Ref(PageFlowAggregation.Reference reference) => new(reference.Page, reference.StructuralChangeId);
    }
}
