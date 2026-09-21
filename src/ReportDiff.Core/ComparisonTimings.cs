namespace ReportDiff.Core;

internal sealed class ComparisonTimings
{
    public double PreparationMs { get; set; }
    public int FeatureWorkers { get; set; }
    public long FeatureWorkerTemporaryBytes { get; set; }
    public double CandidatesMs { get; set; }
    public double GroupingMs { get; set; }
    public double ShiftsMs { get; set; }
    // GroupIndexMsはShiftsMsの内数。公開レポートには出さない。
    public double GroupIndexMs { get; set; }
    public int SearchGroups { get; set; }
    public int SearchRuns { get; set; }
    public int SearchWorkers { get; set; }
    public bool UsedRectangleSearch { get; set; }
    public double MovementMs { get; set; }
    public long MovementManagedBytes { get; set; }
    public double ClassificationMs { get; set; }
    public long ClassificationManagedBytes { get; set; }
    public long RetainedRemovalMaskBytes { get; set; }
    public double ClusteringMs { get; set; }
}
