using OpenCvSharp;

namespace ReportDiff.Core;

public sealed record DifferenceCluster(int Id, Rect Bounds, int Pixels)
{
    public string? Kind { get; init; }
    public double FillRatio => Pixels / ((double)Bounds.Width * Bounds.Height);
}

/// <summary>RawMask・LabelMask・RemovalMask の所有権を持つ。呼び出し側で破棄する。</summary>
public sealed class PageComparison(
    string status, IReadOnlyList<DifferenceCluster> clusters, int rawPixels, int noiseDropped,
    int absorbedGroups, int maxShiftPx, Mat rawMask, Mat labelMask, IReadOnlyList<string> warnings, Mat? removalMask = null) : IDisposable
{
    public string Status { get; } = status;
    public IReadOnlyList<DifferenceCluster> Clusters { get; } = clusters;
    public int RawPixels { get; } = rawPixels;
    public int NoiseDropped { get; } = noiseDropped;
    public int AbsorbedGroups { get; } = absorbedGroups;
    public int MaxShiftPx { get; } = maxShiftPx;
    public Mat RawMask { get; } = rawMask;
    public Mat LabelMask { get; } = labelMask;
    /// <summary>採用した差分のうち A だけにインクがある画素。該当なしの場合は null。</summary>
    public Mat? RemovalMask { get; } = removalMask;
    public IReadOnlyList<string> Warnings { get; } = warnings;
    public void Dispose() { RawMask.Dispose(); LabelMask.Dispose(); RemovalMask?.Dispose(); }
}
