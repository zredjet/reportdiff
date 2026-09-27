using ReportDiff.Core;

namespace ReportDiff.Report;

public sealed record ReportFlowReference(int Page, int StructuralChangeId);
public sealed record ReportFlowEndpoint(string Side, int Page, string CoordinateSystem, PixelBox BoundsPx,
    MillimeterBox BoundsMm, string? Image, IReadOnlyList<ReportFlowReference> Structures);
public sealed record ReportFlowVerification(ComparisonParameters Source, ComparisonParameters Target, bool ExactBandPixelsRequired, bool ExclusionsUsed);
public sealed record ReportFlowSamePageRow(ReportFlowEndpoint Row, ReportFlowEndpoint Counterpart);
public sealed record ReportFlowNonflow(bool DocumentTextUniqueAndOrdered, bool NoCommonRowCrossesBoundary, bool PixelEqualityProven,
    IReadOnlyList<ReportFlowSamePageRow> SourceRows, IReadOnlyList<ReportFlowSamePageRow> TargetRows);
public sealed record ReportFlowBoundaryRow(string Text, ReportFlowEndpoint Row, ReportFlowEndpoint Counterpart);
public sealed record ReportFlowAlternative(int Dy, ReportFlowEndpoint Source, ReportFlowEndpoint Target,
    IReadOnlyList<ReportFlowBoundaryRow> SourceRows, IReadOnlyList<ReportFlowBoundaryRow> TargetRows);
public sealed record ReportFlowAmbiguity(string SourceSide, int BoundaryPage, string Status, string CoordinateSystem,
    int? SelectedDy, IReadOnlyList<PageFlowAmbiguity.Shift> Shifts, IReadOnlyList<ReportFlowBoundaryRow> CrossingRows,
    IReadOnlyList<ReportFlowAlternative> Alternatives, bool PixelEqualityProven);
public sealed record ReportFlowLink(int Id, string Status, string ImageStatus, string? Reason,
    ReportFlowEndpoint? Source, ReportFlowEndpoint? Target, int SupportLines, int OriginalComparisons, ReportFlowVerification? Verification = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ReportFlowNonflow? Nonflow = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ReportFlowAmbiguity? Ambiguity = null);
public sealed record ReportFlowPage(int Page, string Choice, string? MapReason, bool OnlyVerifiedBandsAndFixedParts,
    PageFlowAdoptionResult? Adoption);
public sealed record ReportFlowGroup(int Id, ReportFlowReference Cause, IReadOnlyList<ReportFlowReference> Structures,
    IReadOnlyList<int> Links, IReadOnlyList<ReportFlowEndpoint> AuxiliaryBands, IReadOnlyList<PageFlowAggregation.Balance> Balance);
public sealed record ReportFlowAggregation(string Status, string? Reason, int DifferenceCount, int? AggregatedDifferenceCount,
    bool DifferenceCountComplete, bool? AggregatedDifferenceCountComplete, IReadOnlyList<ReportFlowGroup> Groups,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ReportFlowSharedComponent>? SharedComponents = null);
public sealed record ReportFlowSharedCause(ReportFlowReference Reference, int Rows, int DeltaPx);
public sealed record ReportFlowSharedMovement(ReportFlowReference Structure, IReadOnlyList<ReportFlowReference> Causes, int Dy);
public sealed record ReportFlowSharedLink(int Link, IReadOnlyList<ReportFlowReference> Structures, IReadOnlyList<ReportFlowReference> Causes, int Rows,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ReportFlowEndpoint>? AuxiliaryBands = null);
public sealed record ReportFlowSharedComponent(int Id, string CoordinateSystem, IReadOnlyList<int> Pages,
    IReadOnlyList<ReportFlowSharedCause> Causes, IReadOnlyList<ReportFlowReference> Structures,
    IReadOnlyList<ReportFlowSharedMovement> Movements, IReadOnlyList<ReportFlowSharedLink> Links,
    IReadOnlyList<PageFlowAggregation.Balance> Balance,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ReportFlowEndpoint>? AuxiliaryBands = null);
public sealed record ReportFlowNumericRow(string Text, ReportFlowEndpoint Original);
public sealed record ReportFlowNumericAnchor(ReportFlowNumericRow A, ReportFlowNumericRow B);
public sealed record ReportFlowNumericMatch(int Id, string Status, string Reason, bool PixelEqualityProven, bool UsedAsExactSupport,
    ReportFlowNumericRow A, ReportFlowNumericRow B, IReadOnlyList<int> ChangedTokenIndices, IReadOnlyList<ReportFlowNumericAnchor> Anchors);
public sealed record ReportPageFlow(string Status, IReadOnlyList<string> Reasons, bool RangeReady, bool SelectionLimited,
    IReadOnlyList<ReportFlowLink> Links, IReadOnlyList<ReportFlowPage> Pages, ReportFlowAggregation Aggregation,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ReportFlowNumericMatch>? NumericMatches = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReportAnchoredContent? AnchoredContent { get; init; }
}
