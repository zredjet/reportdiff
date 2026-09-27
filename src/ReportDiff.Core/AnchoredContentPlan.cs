using OpenCvSharp;
using static ReportDiff.Core.PageFlowInference;

namespace ReportDiff.Core;

public sealed record AnchoredRowReference(OriginalRowSpan Span, int BodyIndex, PageFlowLine Line);
public sealed record AnchoredRowMatch(AnchoredRowReference Source, AnchoredRowReference Counterpart);

/// <summary>文字・配置から確定した対応。帯の画素一致や最終採用を表す証拠ではない。</summary>
public sealed class AnchoredContentEvidence
{
    public int CandidateIndex { get; }
    public int CandidateId => CandidateIndex + 1;
    public OriginalRowSpan Source { get; }
    public OriginalRowSpan Target { get; }
    public IReadOnlyList<AnchoredRowReference> Causes { get; }
    public IReadOnlyList<AnchoredRowMatch> CommonRows { get; }
    public IReadOnlyList<AnchoredRowMatch> BeforeSupport { get; }
    public IReadOnlyList<AnchoredRowMatch> BetweenSupport { get; }
    public IReadOnlyList<AnchoredRowMatch> Crossing { get; }
    public IReadOnlyList<AnchoredRowMatch> NextPageSupport { get; }
    public int SamePageDisplacementSupport => 0;
    public bool PixelEqualityProven => false;
    internal AnchoredContentEvidence(int index, OriginalRowSpan source, OriginalRowSpan target, AnchoredRowReference[] causes,
        AnchoredRowMatch[] common, AnchoredRowMatch[] before, AnchoredRowMatch[] between, AnchoredRowMatch[] crossing, AnchoredRowMatch[] next)
    {
        CandidateIndex = index; Source = source; Target = target;
        Causes = Array.AsReadOnly(causes); CommonRows = Array.AsReadOnly(common); BeforeSupport = Array.AsReadOnly(before);
        BetweenSupport = Array.AsReadOnly(between); Crossing = Array.AsReadOnly(crossing); NextPageSupport = Array.AsReadOnly(next);
    }
}

/// <summary>A1の不変計画。画像を持たず、比較・帯証明・採用・CLI接続は後段で行う。公開ビューはDisposeまで借用する。</summary>
public sealed partial class AnchoredContentPlan : IDisposable
{
    private PageFlowPlan? previous;
    private PageFlowDocumentDescriptor? document;
    private AnchoredContentEvidence? evidence;
    private IReadOnlyList<AnchoredContentSurface> surfaces;
    private IReadOnlyList<AnchoredDisplayPlan> displays;
    public AnchoredContentSettings Settings { get; }
    public AnchoredContentBudget Budget { get; }
    public AnchoredContentEvidence Evidence { get { CheckAlive(); return evidence!; } }
    public IReadOnlyList<AnchoredContentSurface> Surfaces { get { CheckAlive(); return surfaces; } }
    public IReadOnlyList<AnchoredDisplayPlan> Displays { get { CheckAlive(); return displays; } }
    public long SurfacePixels { get; }
    public string Fingerprint { get; }

    private AnchoredContentPlan(PageFlowPlan previous, PageFlowDocumentDescriptor document, AnchoredContentSettings settings,
        AnchoredContentBudget budget, AnchoredContentEvidence evidence, AnchoredContentSurface[] surfaces, AnchoredDisplayPlan[] displays, long pixels)
    {
        this.previous = previous; this.document = document; Settings = settings; Budget = budget; this.evidence = evidence;
        this.surfaces = Array.AsReadOnly(surfaces); this.displays = Array.AsReadOnly(displays); SurfacePixels = pixels;
        Fingerprint = ComputeFingerprint();
    }

    public static AnchoredContentPlan? Prepare(PageFlowPlan previous, AnchoredContentSettings settings, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(previous); ArgumentNullException.ThrowIfNull(settings);
        reason = settings.ScopeReason;
        if (reason is not null) return null;
        var inference = previous.Inference; var doc = inference.Document;
        if (doc is null || !ReferenceEquals(inference.BoundLayouts, inference.Layouts)
            || !ReferenceEquals(inference.BoundProposals, inference.Proposals)
            || inference.MaximumShiftPixels != Units.RoundPixels(settings.Rows.MaxShiftMm, settings.Comparison.Dpi)
            || inference.MinimumSupport != settings.Rows.MinSupportBands)
        { reason = "content_evidence_mismatch"; return null; }
        if (doc.Pages.Count != 4 || inference.Layouts.A.Count != 2 || inference.Layouts.B.Count != 2
            || inference.Layouts.A.Where((l, i) => l.Page.Key != new PageFlowPageKey(PageSpace.A, i + 1)).Any()
            || inference.Layouts.B.Where((l, i) => l.Page.Key != new PageFlowPageKey(PageSpace.B, i + 1)).Any())
        { reason = "two_paired_pages_required"; return null; }
        if (doc.Pages.Any(p => p.GlobalMap is not null)) { reason = "global_alignment_enabled"; return null; }
        if (previous.NumericRows.Proofs.Count != 0) { reason = "numeric_rows_not_supported"; return null; }
        if (inference.Proposals.Count > PageFlowLimits.MaximumCandidates) { reason = "flow_candidate_limit"; return null; }
        var used = previous.Usage.DescriptorBytes;
        if (used < 0 || used > PageFlowLimits.MaximumDescriptorBytes) { reason = "flow_descriptor_limit"; return null; }
        var budget = new AnchoredContentBudget(used); var transferred = false;
        try
        {
            // 行索引・二辞書・作業参照配列の容量を先に予約する。本文・画像は複製しない。
            var lines = doc.Pages.Sum(p => (long)p.Lines.Count);
            Reserve(checked(4096 + 256 * doc.Pages.Count + 64 * lines + 512 * inference.Proposals.Count));
            Reserve(checked(64 * 16 + 8 * (6 * lines + 24) + 64 * lines));
            var layouts = inference.Layouts.A.Concat(inference.Layouts.B).ToArray();
            if (inference.Layouts.Status != "prepared" || layouts.Any(l => !l.Regular || !doc.Pages.Contains(l.Page))
                || layouts.Select(l => (l.BodyStart, l.Pitch, l.Page.Size)).Distinct().Count() != 1)
                Reject("irregular_layout");
            var a = Rows(inference.Layouts.A); var b = Rows(inference.Layouts.B);
            var byA = Index(a); var byB = Index(b);
            var onlyA = a.Where(r => !byB.ContainsKey(r.Line.Text)).ToArray();
            var onlyB = b.Where(r => !byA.ContainsKey(r.Line.Text)).ToArray();
            if (!((onlyA.Length == 0 && onlyB.Length == 2) || (onlyB.Length == 0 && onlyA.Length == 2))) Reject("two_single_causes_required");
            var source = onlyA.Length == 0 ? a : b; var target = onlyA.Length == 0 ? b : a;
            var lookup = onlyA.Length == 0 ? byB : byA; var sourceLookup = onlyA.Length == 0 ? byA : byB;
            var causes = onlyA.Length == 0 ? onlyB : onlyA;
            if (!source.Select(r => r.Line.Text).SequenceEqual(target.Where(r => sourceLookup.ContainsKey(r.Line.Text)).Select(r => r.Line.Text)))
                Reject("reordered_common_rows");
            if (source.Any(r => Math.Abs((r.Line.Baseline - r.Top) - (lookup[r.Line.Text].Line.Baseline - lookup[r.Line.Text].Top)) > .5))
                Reject("row_phase_mismatch");
            var first = causes[0]; var last = causes[1]; var pitch = first.Layout.Pitch;
            if (first.Layout.Page.Key.Page != 1 || last.Layout != first.Layout || last.Index != last.Layout.Body.Count - 1)
                Reject("cause_not_exactly_at_body_end");
            var before = first.Index; var between = last.Index - first.Index - 1;
            if (before < settings.Rows.MinSupportBands || between < settings.Rows.MinSupportBands) Reject("local_cause_support");
            foreach (var cause in causes)
                if (!Enumerable.Range(cause.Top, cause.Layout.Pitch).Any(cause.Layout.Page.RowHasNonwhite)) Reject("white_cause");
            var origin = source[0].Layout;
            if (origin.Body.Count != first.Layout.Body.Count) Reject("unequal_body_capacity");
            var height = checked(2 * pitch);
            if (height > inference.MaximumShiftPixels) Reject("crossing_exceeds_maximum");
            var crossing = source.Where(r => lookup[r.Line.Text].Layout.Page.Key.Page != r.Layout.Page.Key.Page).ToArray();
            if (crossing.Length != 2 || height >= origin.BodyEnd - origin.BodyStart) Reject("crossing_not_whole_cumulative_displacement");
            var nextLayout = layouts.Single(l => l.Page.Key == new PageFlowPageKey(first.Layout.Page.Key.Side, 2));
            for (var i = 0; i < crossing.Length; i++)
            {
                var row = crossing[i]; var other = lookup[row.Line.Text];
                if (row.Layout != origin || row.Top != origin.BodyEnd - height + i * pitch || other.Layout != nextLayout
                    || other.Top != nextLayout.BodyStart + i * pitch || Math.Round(other.Line.Bounds.Left - row.Line.Bounds.Left) != 0)
                    Reject("crossing_not_suffix_prefix");
            }
            if (source.Where(r => r.Layout.Page.Key.Page == 1 && lookup[r.Line.Text].Layout.Page.Key.Page == 1).Any(r =>
                Math.Round(lookup[r.Line.Text].Line.Baseline - r.Line.Baseline) != (r.Index < first.Index ? 0 : pitch)
                || Math.Round(lookup[r.Line.Text].Line.Bounds.Left - r.Line.Bounds.Left) != 0)) Reject("local_displacement");
            var next = source.Where(r => r.Layout.Page.Key.Page == 2).ToArray();
            if (next.Length < settings.Rows.MinSupportBands || next.Any(r => lookup[r.Line.Text].Layout != nextLayout
                || lookup[r.Line.Text].Top < nextLayout.BodyStart + height
                || Math.Round(lookup[r.Line.Text].Line.Baseline - r.Line.Baseline) != height
                || Math.Round(lookup[r.Line.Text].Line.Bounds.Left - r.Line.Bounds.Left) != 0)) Reject("independent_next_page_support");
            // この入口は同一ページで累積変位を支持できなかった元候補一つだけを再構成する。
            if (inference.Proposals.Count != 1 || inference.Proposals[0].Source?.Page != origin.Page.Key
                || inference.Proposals[0].Target?.Page != nextLayout.Page.Key) Reject("unresolved_candidates");
            var all = source.Length; var references = checked(2L * (all + before + between + crossing.Length + next.Length) + 2);
            Reserve(checked(256 * references + 64 * 6 + 8 * (references + 4)));
            var common = source.Select(r => new AnchoredRowMatch(r.Reference(), lookup[r.Line.Text].Reference())).ToArray();
            var proof = new AnchoredContentEvidence(0, new(origin.Page.Key, origin.BodyEnd - height, height),
                new(nextLayout.Page.Key, nextLayout.BodyStart, height), causes.Select(r => r.Reference()).ToArray(), common,
                common.Take(before).ToArray(), common.Skip(before).Take(between).ToArray(),
                common.Where(r => r.Source.Span.Page.Page != r.Counterpart.Span.Page.Page).ToArray(),
                common.Where(r => r.Source.Span.Page.Page == 2).ToArray());
            Reserve(64 * 2 + 8 * 4);
            var content = new AnchoredContentSurface[2]; var display = new AnchoredDisplayPlan[2];
            for (var i = 0; i < 2; i++)
            {
                var s = layouts.Single(l => l.Page.Key == new PageFlowPageKey(origin.Page.Key.Side, i + 1));
                var t = layouts.Single(l => l.Page.Key == new PageFlowPageKey(first.Layout.Page.Key.Side, i + 1));
                var pieces = BuildPieces(s, t, lookup, Reserve);
                content[i] = new(new(i + 1), s.Page.Key, s.Page.Size, pieces);
                display[i] = BuildDisplay(inference.Layouts.A[i], inference.Layouts.B[i], byB, proof, Reserve);
            }
            ValidateCoverage(doc, content, proof, Reserve);
            if (!AnchoredContentBudget.TrySurfacePixels(doc.Usage.Pixels, content.Sum(s => (long)s.Size.Width * s.Size.Height),
                display.Sum(s => (long)s.Size.Width * s.Size.Height), out var pixels)) Reject("flow_pixel_limit");
            var plan = new AnchoredContentPlan(previous, doc, settings, budget, proof, content, display, pixels);
            transferred = true; reason = null; return plan;
        }
        catch (PlanRejected ex) { reason = ex.Reason; return null; }
        catch (OverflowException) { reason = "flow_descriptor_limit"; return null; }
        finally { if (!transferred) budget.Dispose(); }
        void Reserve(long bytes) { if (!budget.TryReserveBytes(bytes, out _)) Reject("flow_descriptor_limit"); }
    }

    /// <summary>確定後の他文書・他設定への転用は通常の見送りにせず、処理エラーにする。</summary>
    public void VerifyBinding(PageFlowPlan previous, AnchoredContentSettings settings)
    {
        CheckAlive();
        if (!ReferenceEquals(this.previous, previous) || !ReferenceEquals(Settings, settings)
            || !ReferenceEquals(document, previous.Inference.Document) || Fingerprint != ComputeFingerprint())
            throw new InvalidOperationException("anchored_content_changed: 計画・証拠・設定の結合が変わりました。");
    }

    public void Dispose()
    { previous = null; document = null; evidence = null; surfaces = []; displays = []; Budget.Dispose(); }
    private void CheckAlive() => ObjectDisposedException.ThrowIf(Budget.IsDisposed, this);
    private static void Reject(string reason) => throw new PlanRejected(reason);
    private sealed class PlanRejected(string reason) : Exception(reason) { internal string Reason => Message; }
    private readonly record struct RowLocation(Layout Layout, int Index)
    {
        internal PageFlowLine Line => Layout.Body[Index];
        internal int Top => checked(Layout.BodyStart + Index * Layout.Pitch);
        internal AnchoredRowReference Reference() => new(new(Layout.Page.Key, Top, Layout.Pitch), Index, Line);
    }
    private static RowLocation[] Rows(IReadOnlyList<Layout> layouts) => layouts.SelectMany(l =>
        Enumerable.Range(0, l.Body.Count).Select(i => new RowLocation(l, i))).ToArray();
    private static Dictionary<string, RowLocation> Index(RowLocation[] rows)
    {
        var result = new Dictionary<string, RowLocation>(rows.Length, StringComparer.Ordinal);
        foreach (var row in rows) if (!result.TryAdd(row.Line.Text, row)) Reject("nonunique_body");
        return result;
    }
}
