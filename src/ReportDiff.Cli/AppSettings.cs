using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;

namespace ReportDiff.Cli;

public sealed record ExclusionSetting(int? Page, double X, double Y, double W, double H, string Note = "");
public sealed record ReportOptions
{
    public double CropMarginMm { get; init; } = 2.0;
    public double SnippetMarginMm { get; init; } = 1.0;
    public bool RawOverlay { get; init; }
    public string RawOverlayCommonColor { get; init; } = ReportColor.DefaultCommonColor;
}

public sealed record AppSettings
{
    public int Dpi { get; init; } = 300;
    public int ImageDpi { get; init; } = 300;
    public DiffOptions Diff { get; init; } = new();
    public InkOptions Ink { get; init; } = new();
    public ClusterOptions Cluster { get; init; } = new();
    public MoveOptions Move { get; init; } = new();
    public AlignOptions Align { get; init; } = new();
    public TextOptions Text { get; init; } = new();
    public IReadOnlyList<ExclusionSetting> Exclude { get; init; } = [];
    public IReadOnlyList<RegionSetting> Regions { get; init; } = [];
    public RegionAudit? RegionAudit { get; init; }
    public ReportOptions Report { get; init; } = new();

    public ReportConfiguration ToReportConfiguration() => new(Dpi, ImageDpi, Diff, Cluster,
        Exclude.Select(e => new ReportExclusion(e.Page, e.X, e.Y, e.W, e.H, e.Note)).ToArray(),
        new ReportOutputOptions(Report.CropMarginMm) { SnippetMarginMm = Report.SnippetMarginMm, RawOverlay = Report.RawOverlay,
            RawOverlayCommonColor = Report.RawOverlayCommonColor })
        { Ink = Ink, Move = Move, Align = Align, Text = Text,
            Regions = Regions.Count > 0 || RegionAudit is not null ? Regions.Select(r => r.ToReport(Diff)).ToArray() : null,
            RegionAudit = RegionAudit };

    public ComparisonParameters ForPage(int page, int dpi) => new()
    {
        Dpi = dpi, Diff = Diff, Ink = Ink, Cluster = Cluster, Move = Move,
        Exclude = Exclude.Where(e => e.Page is null || e.Page == page)
            .Select(e => new RectMm(e.X, e.Y, e.W, e.H))
            .Concat(Regions.Where(r => r.Mode == "exclude" && (r.Page is null || r.Page == page)).Select(r => r.Bounds)).ToArray(),
        Regions = Regions.Select((r, i) => (r, i)).Where(x => x.r.Page is null || x.r.Page == page)
            .Select(x => new ComparisonRegion(x.i, x.r.Name, x.r.Bounds, x.r.Mode, x.r.Resolve(Diff))).ToArray()
    };
}

public sealed class ConfigurationException(string message, Exception? inner = null) : Exception(message, inner);
