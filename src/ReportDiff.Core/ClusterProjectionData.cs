using OpenCvSharp;

namespace ReportDiff.Core;

internal sealed record MovementWindowPair(Rect Source, Rect Destination);
internal sealed record MovementProjectionProof(MovementShift Shift, MovementWindowPair Template,
    IReadOnlyList<MovementWindowPair> ValidationWindows, IReadOnlyList<int> ClusterIds);

/// <summary>再クラスタ化せず表示面へ戻すための、生差分画素の所属IDと移動検証範囲。</summary>
internal sealed record ClusterProjectionData(Size Size, int[] RawClusterIds,
    IReadOnlyDictionary<int, MovementProjectionProof> Movements);
