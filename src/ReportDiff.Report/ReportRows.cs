using OpenCvSharp;
using System.Text.Json.Serialization;
using ReportDiff.Core;
using ReportDiff.Pdf;

namespace ReportDiff.Report;

public sealed record StructureCounts(int Inserted, int Deleted, int BlockMoved)
{
    [JsonIgnore] public int Total => Inserted + Deleted + BlockMoved;
    [JsonIgnore] public string Description => string.Join("、", new[] { (Inserted, "行の挿入"), (Deleted, "行の削除"), (BlockMoved, "ブロック移動") }
        .Where(x => x.Item1 > 0).Select(x => $"{x.Item2} {x.Item1}"));
    public static StructureCounts From(IEnumerable<ReportStructuralChange> structures)
    {
        var kept = structures.Where(s => !s.Excluded).ToArray();
        return new(kept.Count(s => s.Kind == "inserted"), kept.Count(s => s.Kind == "deleted"), kept.Count(s => s.Kind == "block_moved"));
    }
}
public sealed record ContinuousBox(double X, double Y, double W, double H);
public sealed record ReportSourceBounds(string CoordinateSystem, ContinuousBox BoundsPx, IReadOnlyList<ContinuousBox> PartsPx);
public sealed record ReportRowFragment(PixelBox ContentBoundsPx, PixelBox DisplayBoundsPx, ContinuousBox? SourceA, ContinuousBox? SourceB, int Pixels);
public sealed record ReportRowSegment(int CanvasStart, int Length, int? AStart, int? BStart, long? AdditionalDyPx, string Kind);
public sealed record ReportContentPiece(int ContentStart, int DisplayStart, int Length, int? AStart, int? BStart, string Kind);
public sealed record ReportContentCanvas(string CoordinateSystem, PixelSize SizePx, IReadOnlyList<ReportContentPiece> Pieces);
public sealed record ReportEquivalentPosition(string Side, int FirstStart, int LastStart, int Step, int Length, string CoordinateSystem);
public sealed record ReportStructuralChange(int Id, string Kind, PixelBox BboxPx, MillimeterBox BboxMm,
    ReportSourceBounds? SourceA, ReportSourceBounds? SourceB, int BandCount, PixelShift? DisplacementPx,
    bool Excluded, string? ExclusionReason, long ContentPixelsA, long ContentPixelsB, long ExcludedContentPixelsA, long ExcludedContentPixelsB,
    IReadOnlyList<ReportEquivalentPosition> EquivalentPositions, string? TextA, string? TextB);
public sealed record ReportRowRegion(int Index, MillimeterBox SourceBoundsMm, PixelBox DisplayBoundsPx, PixelBox ContentBoundsPx, string ContentStatus);
public sealed record ReportRowAlignment(string Status, string Reason, string? Detail, string? Source,
    double? Score, double? ScoreGap, int? Hypotheses, IReadOnlyList<int>? SupportBands,
    int? BaselineRawPixels, int? CandidateRawPixels, double? Improvement,
    PixelSize? OriginalSizeA, PixelSize? OriginalSizeB, PixelSize? AlignedSizePx,
    IReadOnlyList<ReportRowSegment> Segments, ReportContentCanvas? ContentCanvas,
    IReadOnlyList<ReportStructuralChange> StructuralChanges, RowOmissionAudit? OmissionAudit,
    IReadOnlyList<RowAnnotationOmission> AnnotationOmissions, IReadOnlyList<ReportRowRegion> Regions, IReadOnlyList<PixelBox> ExclusionsPx)
{
    public static ReportRowAlignment Disabled { get; } = Skipped("disabled", null, false);
    public static ReportRowAlignment Skipped(string reason, string? source, bool enabled = true) =>
        new(enabled ? "skipped" : "disabled", reason, null, source, null, null, null, null, null, null, null,
            null, null, null, [], null, [], null, [], [], []);
}

/// <summary>呼び出し元が所有する行比較と画像を、保存が終わるまで借りる。</summary>
public sealed record RowReportContext(RowComparison Result, Mat? DisplayA = null, Mat? DisplayB = null,
    PageTextAnnotations? StructuralTextA = null, PageTextAnnotations? StructuralTextB = null);

internal static class ReportRows
{
    internal static ReportRowAlignment Create(RowReportContext? context, ReportConfiguration config, ReportInputs inputs,
        NormalizedPagePair original, int dpi)
    {
        var source = inputs.A.Type == "pdf" && inputs.B.Type == "pdf" ? "pdf_text" : null;
        if (context is null) return ReportRowAlignment.Skipped(config.Rows.Enabled ? "not_compared" : "disabled", source, config.Rows.Enabled);
        var result = context.Result; var a = result.Alignment; var surface = result.Surface; var display = result.Display;
        var structural = display?.StructuralChanges.Select(s => new ReportStructuralChange(s.Id, s.Kind, Box(s.DisplayBounds), Mm(s.DisplayBounds, dpi),
            Source(s.SourceA), Source(s.SourceB), s.BandCount, s.DisplacementPx is { } shift ? new(shift.Dx, shift.Dy) : null,
            s.Excluded, s.ExclusionReason, s.ContentPixelsA, s.ContentPixelsB, s.ExcludedContentPixelsA, s.ExcludedContentPixelsB,
            s.EquivalentPositions.Select(p => new ReportEquivalentPosition(p.Side == PageSpace.A ? "a" : "b",
                p.FirstStart, p.LastStart, p.Step, p.Length, "aligned_top_left")).ToArray(),
            context.StructuralTextA?.TextByCluster.GetValueOrDefault(s.Id), context.StructuralTextB?.TextByCluster.GetValueOrDefault(s.Id))).ToArray() ?? [];
        return new(a.Status, a.Reason, a.Detail, source, a.Score, a.ScoreGap, a.Hypotheses, a.SupportBands,
            a.BaselineRawPixels, a.CandidateRawPixels, a.Improvement, Size(original.OriginalSizeA), Size(original.OriginalSizeB), Size(original.A.Size()),
            surface?.DisplayMap.Segments.Select(s => new ReportRowSegment(s.CanvasStart, s.Length, s.AStart, s.BStart, s.Dy,
                surface.OmittedBands.Contains(s) ? "structural" : s.AStart is null || s.BStart is null ? "white_space" : "paired")).ToArray() ?? [],
            surface is null ? null : new("content_top_left", Size(surface.ContentMap.CanvasSize),
                surface.Pieces.Select(p => new ReportContentPiece(p.ContentStart, p.DisplayStart, p.Length, p.AStart, p.BStart,
                    p.Kind == RowBandKind.WhiteSpace ? "white_space" : "paired")).ToArray()),
            structural, display?.OmissionAudit, display?.AnnotationOmissions ?? [],
            display?.Regions.Select(r => new ReportRowRegion(r.Index, new(r.SourceBounds.X, r.SourceBounds.Y, r.SourceBounds.W, r.SourceBounds.H),
                Box(r.DisplayBounds), Box(r.ContentBounds), r.ContentStatus)).ToArray() ?? [],
            display?.ExcludedBounds.Select(Box).ToArray() ?? []);
    }

    internal static ReportCluster Project(ReportCluster cluster, DifferenceCluster source, int dpi)
    {
        if (source.Row is not { } row) return cluster;
        return cluster with { DisplayPartsPx = row.Parts.Select(p => Box(p.DisplayBounds)).ToArray(), ContentBboxPx = Box(row.ContentBounds),
            ContentBboxMm = Mm(row.ContentBounds, dpi), ContentFillRatio = row.ContentFillRatio, SourceA = Source(row.SourceA), SourceB = Source(row.SourceB),
            RowParts = row.Parts.Select(p => new ReportRowFragment(Box(p.ContentBounds), Box(p.DisplayBounds),
                p.SourceA is { } a ? Box(a) : null, p.SourceB is { } b ? Box(b) : null, p.Pixels)).ToArray() };
    }
    internal static PixelSize Size(Size size) => new(size.Width, size.Height);
    internal static PixelBox Box(Rect r) => new(r.X, r.Y, r.Width, r.Height);
    internal static ContinuousBox Box(PageBounds b) => new(b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top);
    internal static MillimeterBox Mm(Rect r, int dpi)
    { var mm = PageMap.CanvasMillimeters(r, dpi); return new(mm.X, mm.Y, mm.W, mm.H); }
    private static ReportSourceBounds? Source(RowSourceBounds? source) => source is null ? null
        : new("original_top_left", Box(source.Bounds), source.Parts.Select(Box).ToArray());
}
