using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>全ページ採用後の内容・構造を所有する。Dマスクと画像は一面ずつ別途取得する。</summary>
public sealed class AnchoredComparisonResult : IDisposable
{
    private readonly AnchoredContentPlan plan;
    private readonly AnchoredReservations reserve;
    private readonly AnchoredContentBudget.Reservation? aggregationReservation;
    private bool disposed;
    public IReadOnlyList<AnchoredContentResult> Contents { get; }
    public IReadOnlyList<PageFlowAdoptionResult> Adoption { get; }
    public IReadOnlyList<AnchoredStructure> Structures { get; }
    public PageFlowBandVerifier.Verification BandVerification { get; }
    public PageFlowAggregation.Decision Aggregation { get; }
    public long OmittedBandPixels { get; }
    public long OmittedNonwhitePixels { get; }
    internal AnchoredComparisonResult(AnchoredContentPlan plan, AnchoredContentResult[] contents, PageFlowAdoptionResult[] adoption,
        AnchoredStructure[] structures, PageFlowBandVerifier.Verification verification, PageFlowAggregation.Decision aggregation,
        long omittedNonwhite, AnchoredReservations reserve, AnchoredContentBudget.Reservation? aggregationReservation)
    {
        this.plan = plan; this.reserve = reserve; this.aggregationReservation = aggregationReservation;
        Contents = Array.AsReadOnly(contents); Adoption = Array.AsReadOnly(adoption); Structures = Array.AsReadOnly(structures);
        BandVerification = verification; Aggregation = aggregation; OmittedNonwhitePixels = omittedNonwhite;
        OmittedBandPixels = plan.Evidence.Causes.Sum(c => (long)c.Span.Height * plan.Descriptor(c.Span.Page).Size.Width);
    }
    public AnchoredDisplayResult ProjectDisplay(int page)
    {
        ObjectDisposedException.ThrowIf(disposed, this); plan.CheckCurrent();
        // 配列の予約はDisplay内で行う。構造はこの結果の寿命まで借用する。
        return AnchoredProjection.Display(plan, page, Contents, Structures);
    }
    public void Dispose()
    { disposed = true; foreach (var c in Contents) c.Dispose(); aggregationReservation?.Dispose(); reserve.Dispose(); }
}

public sealed partial class AnchoredContentPlan
{
    /// <summary>元単語を再取得し既存の列・支持条件も検査する。一ページの不成立でも全内容を破棄する。
    /// 画像・文字変更、I/O、実確保失敗は処理エラー。read/readTextの入力・設定同一性は呼出側でも固定する。</summary>
    public AnchoredComparisonResult? Compare(Func<PageFlowPageKey, Mat> read, Func<PageFlowPageKey, RowTextResult> readText, out string? reason)
    {
        CheckCurrent(); ArgumentNullException.ThrowIfNull(read); ArgumentNullException.ThrowIfNull(readText); reason = null;
        var reserve = new AnchoredReservations(Budget); var contents = new AnchoredContentResult[2];
        AnchoredContentBudget.Reservation? aggregationReservation = null; var transferred = false;
        try
        {
            reserve.Add(AnchoredAllocation.Reference, checked(64L + document!.Usage.Lines * 32L));
            reserve.Add(AnchoredAllocation.DisplayPart, Displays.Sum(d => 2L * d.Segments.Count));
            reserve.Add(AnchoredAllocation.RowBuffer, Surfaces[0].Size.Width);
            var candidate = new PageFlowInference.Proposal(Evidence.Source.ToBand(), Evidence.Target.ToBand(),
                Array.AsReadOnly(Evidence.Crossing.Select(c => c.Source.Line.Text).ToArray()), 0, "candidate", null);
            PageFlowBandVerifier.Verification verified;
            using (var source = ReadVerified(Evidence.Source.Page, read))
            using (var target = ReadVerified(Evidence.Target.Page, read))
                verified = PageFlowBandVerifier.Verify(candidate, Descriptor(Evidence.Source.Page), Descriptor(Evidence.Target.Page),
                    source, target, Settings.Comparison);
            if (verified.Proposal.Status != "band_verified") { reason = verified.Proposal.Reason; return null; }
            var adoption = new PageFlowAdoptionResult[2];
            for (var i = 0; i < Surfaces.Count; i++)
            {
                using (var images = RenderContent(i + 1, read))
                using (var comparisonReserve = new AnchoredReservations(Budget))
                using (var comparison = PageComparer.Compare(images.A, images.B, Settings.Comparison, true,
                    retainProjection: true, projectionBudget: comparisonReserve))
                {
                    // 打切り済みのCを採用すると、未分類画素を構造削除やD上の再集計で隠してしまう。
                    if (comparison.Status == "too_different" || comparison.Warnings.Contains("CLUSTER_LIMIT"))
                    { reason = "anchored_content_incomplete"; return null; }
                    contents[i] = new(this, Surfaces[i], comparison);
                }
                adoption[i] = AnchoredAdoption.Evaluate(this, i + 1, contents[i].RawPixels, read, readText);
                if (!adoption[i].Accepted) { reason = $"page_{i + 1}_{adoption[i].Reason}"; return null; }
            }
            var structures = BuildStructures();
            long omitted = 0;
            using (var changed = ReadVerified(Evidence.Causes[0].Span.Page, read))
            foreach (var cause in Evidence.Causes)
            {
                using var band = new Mat(changed, new Rect(0, cause.Span.Top, changed.Width, cause.Span.Height));
                using var white = new Mat(); Cv2.InRange(band, Scalar.White, Scalar.White, white);
                omitted += (long)band.Width * band.Height - Cv2.CountNonZero(white);
            }
            var rows = previous!.Inference.Layouts.A.Concat(previous.Inference.Layouts.B).SelectMany(l => l.Body.Select((r, i) =>
                new PageFlowAggregation.Row(l.Page.Key.Side, l.Page.Key.Page, l.BodyStart + i * l.Pitch, l.Pitch, r.Text))).ToArray();
            var pages = contents.Select(c =>
            {
                var ss = structures.Where(s => s.Structure.Reference.Page == c.Id.OwnerPage).Select(s => s.Structure).ToArray();
                return new PageFlowAggregation.Page(c.Id.OwnerPage, true, c.Clusters.Count, c.Clusters.Count + ss.Length,
                    c.Status != "too_different" && !c.Warnings.Contains("CLUSTER_LIMIT"), false, Array.AsReadOnly(ss));
            }).ToArray();
            var aggregate = PageFlowAggregation.Evaluate(new(true, false, rows, [verified.Proposal], pages),
                descriptorBytesRemaining: Budget.RemainingBytes);
            // 集約器が確保前に確認した既存式の量を、保持中の予約へ引き継ぐ。
            if (!Budget.TryReserveBytes(aggregate.SharedDescriptorBytes, out aggregationReservation))
                throw new InvalidOperationException("集約の予約残量が変わりました。");
            CheckCurrent();
            var result = new AnchoredComparisonResult(this, contents, adoption, structures, verified, aggregate, omitted, reserve, aggregationReservation);
            transferred = true; return result;
        }
        catch (AnchoredContentResourceLimitException) { reason = "flow_descriptor_limit"; return null; }
        catch (RowResourceLimitException) { reason = "resource_limit"; return null; }
        finally
        {
            if (!transferred) { foreach (var c in contents) c?.Dispose(); aggregationReservation?.Dispose(); reserve.Dispose(); }
        }
    }

    private AnchoredStructure[] BuildStructures()
    {
        var result = new List<AnchoredStructure>();
        foreach (var display in Displays)
        {
            var id = 0;
            for (var i = 0; i < display.Segments.Count;)
            {
                var s = display.Segments[i]; var kind = Kind(s);
                if (kind is null) { i++; continue; }
                var start = i++; var length = s.Band.Height; var dy = Dy(s);
                while (i < display.Segments.Count && Kind(display.Segments[i]) == kind
                    && display.Segments[i].Role == s.Role && Dy(display.Segments[i]) == dy)
                    length += display.Segments[i++].Band.Height;
                var a = Band(s.Band.A); var b = Band(s.Band.B);
                var role = kind == "block_moved" ? "movement" : s.Role == AnchoredBandRole.Cause ? "cause" : "carry";
                result.Add(new(new(new(display.Page, ++id), kind, a, b, kind == "block_moved" ? -dy : null, false),
                    new(0, display.Segments[start].Band.Top, display.Size.Width, length), role, role != "cause"));
                PageFlowBand? Band(OriginalRowSpan? span)
                {
                    if (span is null) return null;
                    if (!Enumerable.Range(span.Top, length).Any(Descriptor(span.Page).RowHasNonwhite))
                        throw new InvalidOperationException("純白の構造を作ろうとしました。");
                    return new(span.Page, span.Top, length);
                }
            }
        }
        return result.ToArray();
        static int? Dy(AnchoredDisplaySegment s) => s.Band.A is { } a && s.Band.B is { } b ? a.Top - b.Top : null;
        static string? Kind(AnchoredDisplaySegment s) => s.Role is AnchoredBandRole.Cause or AnchoredBandRole.Carry
            ? s.Band.A is null ? "inserted" : "deleted" : Dy(s) is { } dy && dy != 0 ? "block_moved" : null;
    }
}
