using ReportDiff.Core;
using ReportDiff.Report;

namespace ReportDiff.Cli;

public sealed record ExclusionSetting(int? Page, double X, double Y, double W, double H, string Note = "");
public sealed record ReportOptions
{
    public double CropMarginMm { get; init; } = 2.0;
}

public sealed record AppSettings
{
    public int Dpi { get; init; } = 300;
    public int ImageDpi { get; init; } = 300;
    public DiffOptions Diff { get; init; } = new();
    public ClusterOptions Cluster { get; init; } = new();
    public MoveOptions Move { get; init; } = new();
    public AlignOptions Align { get; init; } = new();
    public IReadOnlyList<ExclusionSetting> Exclude { get; init; } = [];
    public ReportOptions Report { get; init; } = new();

    public ReportConfiguration ToReportConfiguration() => new(Dpi, ImageDpi, Diff, Cluster,
        Exclude.Select(e => new ReportExclusion(e.Page, e.X, e.Y, e.W, e.H, e.Note)).ToArray(),
        new ReportOutputOptions(Report.CropMarginMm)) { Move = Move, Align = Align };

    public ComparisonParameters ForPage(int page, int dpi) => new()
    {
        Dpi = dpi, Diff = Diff, Cluster = Cluster, Move = Move,
        Exclude = Exclude.Where(e => e.Page is null || e.Page == page)
            .Select(e => new RectMm(e.X, e.Y, e.W, e.H)).ToArray()
    };
}

public sealed class ConfigurationException(string message, Exception? inner = null) : Exception(message, inner);
