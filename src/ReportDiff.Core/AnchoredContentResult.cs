using OpenCvSharp;
using System.Runtime.InteropServices;

namespace ReportDiff.Core;

[Flags] internal enum AnchoredMaskFlags { Raw = 1, Label = 2, Removal = 4 }
internal readonly record struct AnchoredMaskRun(int Y, int X, int Length, int ClusterId, AnchoredMaskFlags Flags);
public sealed record AnchoredDisplayAnnotation(ContentClusterKey Content, int DisplayPage, string? Kind,
    MovementShift? Shift, IReadOnlyList<int> RelatedClusterIds, string? Reason);
public sealed record AnchoredStructure(PageFlowAggregation.Structure Structure, Rect DisplayBounds, string Role, bool ComparedInContent);

/// <summary>Cの比較値と疎なマスクを所有する。画像を持たず、Dの表示画素数で検出値を書き換えない。</summary>
public sealed class AnchoredContentResult : IDisposable
{
    public ContentSurfaceId Id { get; }
    public string Status { get; }
    public string MaskSha256 { get; }
    public int RawPixels { get; }
    public int NoiseDropped { get; }
    public int AbsorbedGroups { get; }
    public int MaxShiftPx { get; }
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyList<DifferenceCluster> Clusters { get; }
    public IReadOnlyList<ContentDisplayPart> Parts => parts;
    public IReadOnlyList<AnchoredDisplayAnnotation> Annotations => annotations;
    internal AnchoredBuffer<AnchoredMaskRun> Runs { get; }
    private readonly AnchoredBuffer<ContentDisplayPart> parts;
    private readonly AnchoredBuffer<AnchoredDisplayAnnotation> annotations;
    private readonly AnchoredReservations reserve;
    internal bool IsDisposed { get; private set; }

    internal AnchoredContentResult(AnchoredContentPlan plan, AnchoredContentSurface surface, PageComparison comparison)
    {
        Id = surface.Id; Status = comparison.Status; RawPixels = comparison.RawPixels; NoiseDropped = comparison.NoiseDropped;
        AbsorbedGroups = comparison.AbsorbedGroups; MaxShiftPx = comparison.MaxShiftPx;
        reserve = new(plan.Budget); Runs = new(plan.Budget, AnchoredAllocation.MaskRun);
        parts = new(plan.Budget, AnchoredAllocation.DisplayPart); annotations = new(plan.Budget, AnchoredAllocation.DisplayPart);
        try
        {
            reserve.Add(AnchoredAllocation.Cluster, comparison.Clusters.Count);
            reserve.Add(AnchoredAllocation.Reference, comparison.Warnings.Count + comparison.Clusters.Sum(c => (long)c.RelatedClusterIds.Count));
            Clusters = Array.AsReadOnly(comparison.Clusters.ToArray()); Warnings = Array.AsReadOnly(comparison.Warnings.ToArray());
            using var rows = new AnchoredReservations(plan.Budget);
            rows.Add(AnchoredAllocation.RowBuffer, surface.Size.Width);
            Encode(comparison, surface.Size.Width);
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            Span<byte> encoded = stackalloc byte[20];
            foreach (var run in Runs)
            {
                var values = (run.Y, run.X, run.Length, run.ClusterId, (int)run.Flags);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(encoded, values.Item1);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(encoded[4..], values.Item2);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(encoded[8..], values.Item3);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(encoded[12..], values.Item4);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(encoded[16..], values.Item5);
                hash.AppendData(encoded);
            }
            MaskSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            AnchoredProjection.Parts(surface, plan.Displays, this, parts);
            AnchoredProjection.Annotations(surface, plan.Displays, comparison, parts, annotations);
        }
        catch { Dispose(); throw; }
    }

    private void Encode(PageComparison comparison, int width)
    {
        var ids = comparison.ProjectionData?.RawClusterIds; var height = comparison.RawMask.Height;
        if (comparison.Clusters.Count > 0 && (ids is null || ids.Length != checked(width * comparison.RawMask.Height)))
            throw new InvalidOperationException("内容画素の所属IDがありません。");
        var raw = new byte[width]; var label = new byte[width]; var removal = new byte[width];
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(comparison.RawMask.Ptr(y), raw, 0, width); Marshal.Copy(comparison.LabelMask.Ptr(y), label, 0, width);
            if (comparison.RemovalMask is { } mask) Marshal.Copy(mask.Ptr(y), removal, 0, width);
            for (var x = 0; x < width;)
            {
                var flags = Flags(x); if (flags == 0) { x++; continue; }
                var id = ids?[y * width + x] ?? 0; var end = x + 1;
                while (end < width && Flags(end) == flags && (ids?[y * width + end] ?? 0) == id) end++;
                Runs.Add(new(y, x, end - x, id, flags)); x = end;
            }
            AnchoredMaskFlags Flags(int x) => (raw[x] != 0 ? AnchoredMaskFlags.Raw : 0)
                | (label[x] != 0 ? AnchoredMaskFlags.Label : 0) | (removal[x] != 0 ? AnchoredMaskFlags.Removal : 0);
        }
    }
    public void Dispose() { IsDisposed = true; Runs.Dispose(); parts.Dispose(); annotations.Dispose(); reserve.Dispose(); }
}

/// <summary>一物理ページDのマスクだけを所有する。参照断片は内容件数へ加えない。</summary>
public sealed class AnchoredDisplayResult : IDisposable
{
    public int Page { get; }
    public Mat RawMask { get; }
    public Mat LabelMask { get; }
    public Mat RemovalMask { get; }
    public int DisplayRawPixels { get; }
    public IReadOnlyList<ContentDisplayPart> Parts { get; }
    public IReadOnlyList<AnchoredDisplayAnnotation> Annotations { get; }
    public IReadOnlyList<AnchoredStructure> Structures { get; }
    private readonly AnchoredReservations reserve;
    internal AnchoredDisplayResult(int page, Mat raw, Mat label, Mat removal, IReadOnlyList<ContentDisplayPart> parts,
        IReadOnlyList<AnchoredDisplayAnnotation> annotations, IReadOnlyList<AnchoredStructure> structures, AnchoredReservations reserve)
    {
        Page = page; RawMask = raw; LabelMask = label; RemovalMask = removal; Parts = parts;
        Annotations = annotations; Structures = structures; this.reserve = reserve; DisplayRawPixels = Cv2.CountNonZero(raw);
    }
    public void Dispose() { RawMask.Dispose(); LabelMask.Dispose(); RemovalMask.Dispose(); reserve.Dispose(); }
}
