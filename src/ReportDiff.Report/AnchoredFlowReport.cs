using ReportDiff.Core;
using static ReportDiff.Report.ReportRows;

namespace ReportDiff.Report;

public static partial class AnchoredFlowReport
{
    public static ReportPageFlow Create(AnchoredContentPlan plan, AnchoredComparisonResult result,
        string sourceImage, string targetImage)
    {
        var evidence = plan.Evidence; var width = plan.Surfaces[0].Size.Width; var dpi = plan.Settings.Comparison.Dpi;
        ReportFlowReference Ref(PageFlowAggregation.Reference r) => new(r.Page, r.StructuralChangeId);
        ReportFlowEndpoint Endpoint(OriginalRowSpan band, string image) => new(ReportAnchored.Side(band.Page.Side), band.Page.Page,
            "original_top_left", new(0, band.Top, width, band.Height), Mm(new(0, band.Top, width, band.Height), dpi), image,
            result.Structures.Where(s => (band.Page.Side == PageSpace.A ? s.Structure.A : s.Structure.B) is { } b
                && b.Page == band.Page && b.Top == band.Top && b.Height == band.Height).Select(s => Ref(s.Structure.Reference)).ToArray());
        var decision = result.Aggregation;
        var shared = decision.SharedComponents?.Select((c, i) => new ReportFlowSharedComponent(i + 1, "original_top_left", c.Pages,
            c.Causes.Select(s => new ReportFlowSharedCause(Ref(s.Reference), s.Rows, s.Delta)).ToArray(), c.Structures.Select(Ref).ToArray(),
            c.Movements.Select(m => new ReportFlowSharedMovement(Ref(m.Structure), m.Causes.Select(Ref).ToArray(), m.Dy)).ToArray(),
            c.Links.Select(l => new ReportFlowSharedLink(evidence.CandidateId, l.Structures.Select(Ref).ToArray(), l.Causes.Select(Ref).ToArray(), l.Rows)).ToArray(),
            c.Balance)).ToArray();
        return new("applied", [], true, false,
            [new(evidence.CandidateId, "carried", "verified", null, Endpoint(evidence.Source, sourceImage), Endpoint(evidence.Target, targetImage),
                0, result.BandVerification.OriginalComparisons, new(plan.Settings.Comparison, plan.Settings.Comparison, true, false))],
            result.Adoption.Select((a, i) => new ReportFlowPage(i + 1, "candidate", "anchored_content", true, a)).ToArray(),
            new(decision.Status, decision.Reason, decision.DifferenceCount, decision.AggregatedDifferenceCount,
                decision.DifferenceCountComplete, decision.AggregatedDifferenceCountComplete, [], shared))
        { AnchoredContent = ReportAnchored.Audit(plan, true, null, true) };
    }

    /// <summary>公開前に所属、参照、構造件数と画像ファイルを照合する。</summary>
    public static void Validate(ReportDocument report, string directory)
    {
        foreach (var page in report.Pages)
        {
            if (page.ContentDisplay?.ReferenceCount != page.ContentReferences?.Count
                || page.Clusters.Any(c => c.ContentRef != new ReportContentKey(page.Page, c.Id))
                || page.ContentReferences!.Select(r => r.ContentRef).Distinct().Count() != page.ContentReferences!.Count
                || page.StructuralChangeCount != page.RowAlignment.StructuralChanges.Count)
                throw new ReportWriteException("内容の所属・参照・構造件数が一致しません。");
            foreach (var c in page.Clusters)
            {
                Parts(c.SourceParts!, page); Crops(c.Crops);
                if (c.SourceParts!.Count == 0) throw new ReportWriteException("所有内容に表示断片がありません。");
            }
            foreach (var r in page.ContentReferences)
            {
                var owner = report.Pages.SingleOrDefault(p => p.Page == r.OwnerPage);
                if (r.OwnerPage == page.Page || r.ContentRef != new ReportContentKey(r.OwnerPage, r.OwnerClusterId)
                    || owner?.Clusters.SingleOrDefault(c => c.Id == r.OwnerClusterId) is null || r.Parts.Count == 0
                    || r.DisplayPixels != r.Parts.Sum(p => p.Pixels)) throw new ReportWriteException("別ページの内容参照が解決できません。");
                Parts(r.Parts, page); Crops(r.Crops);
            }
            foreach (var path in new[] { page.Images.A, page.Images.B, page.Images.Overlay, page.Images.ContentA, page.Images.ContentB,
                page.RawEvidence?.A.Image, page.RawEvidence?.B.Image, page.RawEvidence?.Overlay }) File(path);
        }
        foreach (var link in report.PageFlow!.Links) { File(link.Source?.Image); File(link.Target?.Image); }
        var audit = report.PageFlow.AnchoredContent!;
        if (audit.OmittedOriginalBands.Count != 2 || report.Pages.Sum(p => p.RowAlignment.OmissionAudit!.OmittedBandPixels)
            != audit.OmittedOriginalBands.Sum(b => (long)b.BoundsPx.W * b.BoundsPx.H)
            || report.Summary.Clusters != report.Pages.Sum(p => p.Clusters.Count))
            throw new ReportWriteException("省略帯または内容の件数が一致しません。");
        void Parts(IReadOnlyList<ReportContentPart> parts, ReportPage page)
        {
            foreach (var part in parts)
            {
                Bounds(part.DisplayBboxPx, page.SizePx);
                foreach (var source in new[] { part.SourceA, part.SourceB })
                {
                    if (source is null) continue;
                    var original = report.Pages.SingleOrDefault(p => p.Page == source.Page)?.RowAlignment.AlignedSizePx;
                    if (original is null || source.CoordinateSystem != "original_top_left") throw new ReportWriteException("出自ページがありません。");
                    Bounds(source.BoundsPx, original);
                }
            }
        }
        static void Bounds(PixelBox b, PixelSize s)
        {
            if (b.X < 0 || b.Y < 0 || b.W <= 0 || b.H <= 0 || (long)b.X + b.W > s.W || (long)b.Y + b.H > s.H)
                throw new ReportWriteException("参照矩形がページ範囲を超えています。");
        }
        void Crops(ClusterCrops c) { File(c.A); File(c.B); File(c.Diff); }
        void File(string? path)
        {
            if (path is null) return;
            if (Path.IsPathRooted(path) || path.Split('/').Any(p => p is ".." or ".") || path.Contains('\\')
                || !System.IO.File.Exists(Path.Combine(directory, path))) throw new ReportWriteException("画像参照の保存を確認できません。");
        }
    }
}
