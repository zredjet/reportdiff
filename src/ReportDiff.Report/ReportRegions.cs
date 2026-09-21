using System.Text.Json.Serialization;
using ReportDiff.Core;

namespace ReportDiff.Report;

public sealed record RegionDiffOverride(double? MaxShiftMm = null, double? ColorThreshold = null, double? EdgeTolerance = null)
{
    public DiffOptions Apply(DiffOptions baseline) => baseline with
    {
        MaxShiftMm = MaxShiftMm ?? baseline.MaxShiftMm,
        ColorThreshold = ColorThreshold ?? baseline.ColorThreshold,
        EdgeTolerance = EdgeTolerance ?? baseline.EdgeTolerance
    };
}

public sealed record ReportRegion(string Name, [property: JsonConverter(typeof(ExclusionPageConverter))] int? Page,
    double X, double Y, double W, double H, string Mode, string? Profile, RegionDiffOverride? Diff, DiffOptions? EffectiveDiff);
public sealed record RegionAudit(bool Disabled, string Reason, IReadOnlyList<ReportRegion> Regions, IReadOnlyList<ReportExclusion> Exclude);
public sealed record PageRegion(int Index, string Name, string Mode, string Status, PixelBox BoundsPx,
    int EffectivePixels, int RawPixels, int SuppressedPixels, int SuppressedComponents, int ExcludedPixels, int? RunId);
public sealed record PageRegions(string CoordinateSystem, string SuppressionMethod, int SuppressedPixels,
    int SuppressedComponents, int ExcludedPixels, IReadOnlyList<RegionRun> Runs, IReadOnlyList<PageRegion> Items)
{
    public static PageRegions? Create(IReadOnlyList<ReportRegion>? declarations, int page, int dpi, PixelSize size, RegionalComparison? result)
    {
        if (declarations is null || declarations.Count == 0) return null;
        var items = declarations.Select((r, i) =>
        {
            var applied = result?.Regions.SingleOrDefault(x => x.Index == i);
            var box = applied?.Bounds ?? PageMap.CanvasRectangle(new(r.X, r.Y, r.W, r.H), dpi, new(size.W, size.H));
            return new PageRegion(i, r.Name, r.Mode, r.Page is not null && r.Page != page ? "not_applicable"
                : applied?.Status ?? "not_compared", new(box.X, box.Y, box.Width, box.Height), applied?.EffectivePixels ?? 0,
                applied?.RawPixels ?? 0, applied?.SuppressedPixels ?? 0, applied?.SuppressedComponents ?? 0,
                applied?.ExcludedPixels ?? 0, applied?.RunId);
        }).ToArray();
        return new("comparison_top_left", "baseline_raw_minus_composed_raw_8_connected", result?.SuppressedPixels ?? 0,
            result?.SuppressedComponents ?? 0, result?.ExcludedPixels ?? 0, result?.Runs ?? [], items);
    }
}
