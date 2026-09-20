namespace ReportDiff.Core;

internal sealed class ComparisonTimings
{
    public double PreparationMs { get; set; }
    public double CandidatesMs { get; set; }
    public double GroupingMs { get; set; }
    public double ShiftsMs { get; set; }
    public double MovementMs { get; set; }
    public long MovementManagedBytes { get; set; }
    public double ClassificationMs { get; set; }
    public long ClassificationManagedBytes { get; set; }
    public long RetainedRemovalMaskBytes { get; set; }
    public double ClusteringMs { get; set; }
}
