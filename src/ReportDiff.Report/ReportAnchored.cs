using System.Text.Json.Serialization;
using ReportDiff.Core;
using static ReportDiff.Report.ReportRows;

namespace ReportDiff.Report;

public sealed record ReportContentKey(int SurfaceId, int ClusterId);
public sealed record ReportOriginalBox(string Side, int Page, string CoordinateSystem, PixelBox BoundsPx);
public sealed record ReportContentPart(PixelBox ContentBboxPx, PixelBox DisplayBboxPx,
    ReportOriginalBox? SourceA, ReportOriginalBox? SourceB, IReadOnlyList<string> DisplaySides, int Pixels);
public sealed record ReportContentReference(ReportContentKey ContentRef, int OwnerPage, int OwnerClusterId,
    PixelBox BboxPx, IReadOnlyList<ReportContentPart> Parts, int DisplayPixels, ClusterCrops Crops);
public sealed record ReportContentDisplay(string CoordinateSystem, int RawPixels, int ReferenceCount);
public sealed record ReportAnchoredOrigin(string Side, int Page, int Top);
public sealed record ReportAnchoredPiece(int ContentStart, int Length, ReportAnchoredOrigin? A, ReportAnchoredOrigin? B);
public sealed record ReportAnchoredSurface(int Id, int OwnerPage, ReportAnchoredOrigin Anchor, string CoordinateSystem,
    PixelSize SizePx, IReadOnlyList<ReportAnchoredPiece> Pieces);
public sealed record ReportAnchoredRow(string Text, ReportOriginalBox Original);
public sealed record ReportAnchoredMatch(ReportAnchoredRow Source, ReportAnchoredRow Counterpart);
public sealed record ReportAnchoredEvidence(int CandidateId, IReadOnlyList<ReportAnchoredRow> Causes,
    ReportOriginalBox Source, ReportOriginalBox Target, IReadOnlyList<ReportAnchoredMatch> BeforeSupport,
    IReadOnlyList<ReportAnchoredMatch> BetweenSupport, IReadOnlyList<ReportAnchoredMatch> NextPageSupport,
    IReadOnlyList<ReportAnchoredMatch> Crossing, bool PixelEqualityProven, bool UsedAsExactSupport, int OriginalComparisons);
public sealed record ReportAnchoredBudget(long LimitBytes, long PeakBytes, long SurfacePixels, long LimitPixels);
public sealed record ReportAnchoredContent(string Status, string? Reason, int ProofVersion, string Fingerprint,
    IReadOnlyList<int> Pages, ReportAnchoredBudget Budget, ReportAnchoredEvidence Evidence,
    IReadOnlyList<ReportOriginalBox> OmittedOriginalBands)
{
    public bool ContentOriginalRowsCoveredExactlyOnce { get; init; } = true;
    public bool DisplayOriginalRowsCoveredExactlyOnce { get; init; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<ReportAnchoredSurface>? Surfaces { get; init; }
}

public static class ReportAnchored
{
    public static ReportAnchoredContent Audit(AnchoredContentPlan plan, bool applied, string? reason, bool bandVerified)
    {
        var e = plan.Evidence; var width = plan.Surfaces[0].Size.Width;
        ReportOriginalBox Band(OriginalRowSpan s) => new(Side(s.Page.Side), s.Page.Page, "original_top_left", new(0, s.Top, width, s.Height));
        ReportAnchoredRow Row(AnchoredRowReference r) => new(r.Line.Text, Band(r.Span));
        ReportAnchoredMatch[] Matches(IReadOnlyList<AnchoredRowMatch> rows) => rows.Select(r => new ReportAnchoredMatch(Row(r.Source), Row(r.Counterpart))).ToArray();
        return new(applied ? "applied" : "skipped", reason, 1, plan.Fingerprint, [1, 2],
            new(PageFlowLimits.MaximumDescriptorBytes, plan.Budget.PeakBytes, plan.SurfacePixels, PageFlowLimits.MaximumPixels),
            new(e.CandidateId, e.Causes.Select(Row).ToArray(), Band(e.Source), Band(e.Target), Matches(e.BeforeSupport),
                Matches(e.BetweenSupport), Matches(e.NextPageSupport), Matches(e.Crossing), bandVerified, false, bandVerified ? 12 : 0),
            applied ? e.Causes.Select(r => Band(r.Span)).ToArray() : [])
        { Surfaces = applied ? plan.Surfaces.Select(s => new ReportAnchoredSurface(s.Id.OwnerPage, s.Id.OwnerPage,
            new(Side(s.Anchor.Side), s.Anchor.Page, 0), "content_top_left", Size(s.Size), s.Pieces.Select(p =>
                new ReportAnchoredPiece(p.Top, p.Height, Origin(p.A), Origin(p.B))).ToArray())).ToArray() : null };
    }
    internal static string Side(PageSpace side) => side == PageSpace.A ? "a" : "b";
    private static ReportAnchoredOrigin? Origin(OriginalRowSpan? s) => s is null ? null : new(Side(s.Page.Side), s.Page.Page, s.Top);
    internal static ReportContentKey Key(ContentClusterKey key) => new(key.Surface.OwnerPage, key.ClusterId);
    internal static ReportContentPart Part(ContentDisplayPart p) => new(Box(p.ContentBounds), Box(p.DisplayBounds),
        Original(p.SourceA), Original(p.SourceB), p.DisplaySides == (ContentDisplaySides.A | ContentDisplaySides.B)
            ? ["a", "b"] : p.DisplaySides == ContentDisplaySides.A ? ["a"] : ["b"], p.Pixels);
    private static ReportOriginalBox? Original(OriginalPixelBounds? b) => b is null ? null
        : new(Side(b.Page.Side), b.Page.Page, "original_top_left", Box(b.Bounds));
}
