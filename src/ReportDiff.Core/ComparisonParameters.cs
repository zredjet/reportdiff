namespace ReportDiff.Core;

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
}

public sealed record MoveOptions
{
    public double SearchMm { get; init; } = 5.0;
    public double MinScore { get; init; } = 0.98;
    public double MinScoreGap { get; init; } = 0.02;
}

public sealed record AlignOptions
{
    public bool Enabled { get; init; }
    public double MaxShiftMm { get; init; } = 5.0;
    public double MinScore { get; init; } = 0.98;
    public double MinScoreGap { get; init; } = 0.02;
    public double MinImprovement { get; init; } = 0.05;
}

public sealed record RectMm(double X, double Y, double W, double H);

public sealed record ComparisonParameters
{
    public int Dpi { get; init; } = 300;
    public DiffOptions Diff { get; init; } = new();
    public ClusterOptions Cluster { get; init; } = new();
    public MoveOptions Move { get; init; } = new();
    public IReadOnlyList<RectMm> Exclude { get; init; } = [];
}
