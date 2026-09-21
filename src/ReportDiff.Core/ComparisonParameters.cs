namespace ReportDiff.Core;

public sealed record InkOptions
{
    public double BackgroundRadiusMm { get; init; } = 1.5;
    public double ContrastThreshold { get; init; } = 25;
}

public sealed record DiffOptions
{
    public double MaxShiftMm { get; init; } = 0.15;
    public double ColorThreshold { get; init; } = 3.0;
    public double EdgeTolerance { get; init; } = 0.3;
}

public sealed record ClusterOptions
{
    public double MergeXMm { get; init; } = 3.0;
    public double MergeYMm { get; init; } = 1.0;
    public int MinPixels { get; init; } = 4;
    public int MaxClustersPerPage { get; init; } = 500;
    public double MaxDiffRatio { get; init; } = 0.30;
    public double ReadingBandMm { get; init; } = 5;
}

public sealed record MoveOptions
{
    public double SearchMm { get; init; } = 5.0;
    public double MinScore { get; init; } = 0.98;
    public double MinScoreGap { get; init; } = 0.02;
    public double TemplateMarginMm { get; init; } = 1;
}

public sealed record AlignOptions
{
    public bool Enabled { get; init; }
    public double MaxShiftMm { get; init; } = 5.0;
    public double MinScore { get; init; } = 0.98;
    public double MinScoreGap { get; init; } = 0.02;
    public double MinImprovement { get; init; } = 0.05;
    public int CoarseMaxSideSamples { get; init; } = 800;
    public int RefineRadiusSamples { get; init; } = 2;
    public int MinSupportCells { get; init; } = 3;
    public int MinSupportRows { get; init; } = 2;
    public int MinSupportColumns { get; init; } = 2;
    public double MinInkAreaMm2 { get; init; } = 1;
}

public sealed record RectMm(double X, double Y, double W, double H);

public sealed record ComparisonParameters
{
    public int Dpi { get; init; } = 300;
    public DiffOptions Diff { get; init; } = new();
    public InkOptions Ink { get; init; } = new();
    public ClusterOptions Cluster { get; init; } = new();
    public MoveOptions Move { get; init; } = new();
    public IReadOnlyList<RectMm> Exclude { get; init; } = [];
    public IReadOnlyList<ComparisonRegion> Regions { get; init; } = [];
}
