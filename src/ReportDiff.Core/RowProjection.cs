using OpenCvSharp;

namespace ReportDiff.Core;

public sealed record RowClusterProjection(int ContentId, Rect ContentBounds, double ContentFillRatio,
    IReadOnlyList<RowMaskFragment> Parts, RowSourceBounds? SourceA, RowSourceBounds? SourceB);
public sealed record RowAnnotationOmission(IReadOnlyList<int> ClusterIds, string Reason);
public sealed record RowRegionProjection(int Index, RectMm SourceBounds, Rect DisplayBounds, Rect ContentBounds, string ContentStatus);

/// <summary>表示面のマスクを所有する。画素数・ノイズ・上限・抑制数は内容比較面で決めた値。</summary>
public sealed class RowDisplayProjection : IDisposable
{
    public PageComparison Comparison { get; }
    public IReadOnlyList<RowStructuralChange> StructuralChanges { get; }
    public IReadOnlyList<RowAnnotationOmission> AnnotationOmissions { get; }
    public IReadOnlyList<RowRegionProjection> Regions { get; }
    public IReadOnlyList<Rect> ExcludedBounds { get; }
    public RowOmissionAudit OmissionAudit { get; }
    public int StructuralChangeCount => StructuralChanges.Count(c => !c.Excluded);
    public int DifferenceCount => Comparison.Clusters.Count + StructuralChangeCount;
    public bool DifferenceCountComplete => Comparison.Status != "too_different" && !Comparison.Warnings.Contains("CLUSTER_LIMIT");

    internal RowDisplayProjection(PageComparison comparison, IReadOnlyList<RowStructuralChange> structures,
        IReadOnlyList<RowAnnotationOmission> omissions, IReadOnlyList<RowRegionProjection> regions, IReadOnlyList<Rect> exclusions, RowOmissionAudit audit)
    {
        Comparison = comparison; StructuralChanges = structures; AnnotationOmissions = omissions;
        Regions = regions; ExcludedBounds = exclusions; OmissionAudit = audit;
    }
    public void Dispose() => Comparison.Dispose();
}

internal static class RowProjection
{
    internal static RowDisplayProjection Create(PageComparison content, RowComparisonSurface surface, Mat a, Mat b,
        ComparisonParameters parameters, IReadOnlyList<RowEquivalentPosition>? equivalents = null, PageMap? globalMap = null)
    {
        if (a.Size() != surface.DisplayMap.SizeA || b.Size() != surface.DisplayMap.SizeB || content.RawMask.Size() != surface.ContentMap.CanvasSize
            || globalMap is not null && (globalMap.CanvasSize != a.Size() || globalMap.CanvasSize != b.Size()))
            throw new ArgumentException("内容比較・表示写像・元画像の寸法が一致していません。");
        var data = content.ProjectionData;
        if (content.Clusters.Count > 0 && (data is null || data.Size != surface.ContentMap.CanvasSize
            || data.RawClusterIds.Length != checked(data.Size.Width * data.Size.Height)))
            throw new ArgumentException("表示へ戻す比較ではクラスタ画素の所属を保持してください。");
        var displaySettings = RegionMap.ForRowDisplay(surface.DisplayMap, parameters);
        var (structures, audit) = RowStructures.Build(a, b, surface, displaySettings, equivalents ?? [], globalMap);
        var clusters = ProjectClusters();
        var readingBand = Math.Max(1, Units.RoundPixels(parameters.Cluster.ReadingBandMm, parameters.Dpi));
        clusters = clusters.OrderBy(c => c.Bounds.Y / readingBand).ThenBy(c => c.Bounds.X).ThenBy(c => c.Id).ToArray();
        var ids = clusters.Select((c, i) => (c.Id, DisplayId: i + 1)).ToDictionary(p => p.Id, p => p.DisplayId);
        var omissions = new List<RowAnnotationOmission>();
        var movement = new Dictionary<int, MovementShift?>();
        foreach (var cluster in clusters.Where(c => c.Kind == "moved"))
        {
            if (movement.ContainsKey(cluster.Id)) continue;
            if (data!.Movements.TryGetValue(cluster.Id, out var proof))
            {
                var shift = ProjectMovement(surface, proof);
                foreach (var id in proof.ClusterIds) movement[id] = shift;
                if (shift is null) omissions.Add(new(proof.ClusterIds.Select(id => ids[id]).Order().ToArray(), "nonuniform_display_mapping"));
            }
            else
            {
                movement[cluster.Id] = null;
                omissions.Add(new([ids[cluster.Id]], "movement_proof_unavailable"));
            }
        }
        clusters = clusters.Select(c =>
        {
            var shift = c.Kind == "moved" ? movement[c.Id] : c.ShiftPx;
            return c with { Id = ids[c.Id], Kind = c.Kind == "moved" && shift is null ? "changed" : c.Kind, ShiftPx = shift,
                RelatedClusterIds = shift is null ? [] : c.RelatedClusterIds.Select(id => ids[id]).Order().ToArray() };
        }).ToArray();
        Mat? raw = null, labels = null, removal = null; RegionalComparison? regional = null;
        try
        {
            raw = surface.ToDisplay(content.RawMask); labels = surface.ToDisplay(content.LabelMask);
            if (content.RemovalMask is { } removed) removal = surface.ToDisplay(removed);
            if (Cv2.CountNonZero(raw) != content.RawPixels) throw new InvalidOperationException("表示転写で生差分の画素数が変わりました。");
            var regions = new List<RowRegionProjection>();
            if (content.Regional is { } original)
            {
                var results = original.Regions.Select((r, i) =>
                {
                    var bounds = displaySettings.Bounds[i];
                    regions.Add(new(r.Index, parameters.Regions[i].Bounds, bounds, r.Bounds, r.Status));
                    return r with { Bounds = bounds, Status = r.Status == "outside_page" && bounds.Width > 0 && bounds.Height > 0
                        ? "omitted_from_content" : r.Status };
                }).ToArray();
                regional = new(results, original.Runs, surface.ToDisplay(original.SuppressedMask), original.SuppressedPixels,
                    original.SuppressedComponents, original.ExcludedPixels);
            }
            var status = content.Status == "same" && structures.Any(s => !s.Excluded) ? "different" : content.Status;
            var comparison = new PageComparison(status, clusters, content.RawPixels, content.NoiseDropped, content.AbsorbedGroups,
                content.MaxShiftPx, raw, labels, content.Warnings, removal) { Regional = regional };
            var result = new RowDisplayProjection(comparison, structures, omissions.AsReadOnly(), regions.AsReadOnly(),
                Array.AsReadOnly(displaySettings.ExclusionBounds), audit);
            raw = labels = removal = null; regional = null;
            return result;
        }
        finally { raw?.Dispose(); labels?.Dispose(); removal?.Dispose(); regional?.Dispose(); }

        DifferenceCluster[] ProjectClusters()
        {
            if (content.Clusters.Count == 0) return [];
            var indexById = content.Clusters.Select((c, i) => (c.Id, i)).ToDictionary(p => p.Id, p => p.i);
            var parts = content.Clusters.Select(_ => new List<RowMaskFragment>()).ToArray();
            var width = surface.ContentMap.CanvasSize.Width;
            foreach (var piece in surface.Pieces)
            {
                var boxes = Enumerable.Range(0, parts.Length).Select(_ => new PixelBounds()).ToArray();
                for (var y = piece.ContentStart; y < piece.ContentStart + piece.Length; y++)
                for (var x = 0; x < width; x++)
                {
                    var id = data!.RawClusterIds[y * width + x];
                    if (id != 0) boxes[indexById[id]].Add(x, y);
                }
                for (var i = 0; i < boxes.Length; i++)
                {
                    var box = boxes[i]; if (box.Count == 0) continue;
                    var c = box.Bounds; var d = new Rect(c.X, c.Y + piece.DisplayStart - piece.ContentStart, c.Width, c.Height);
                    var sa = RowGeometry.Source(surface.ContentMap, c, PageSpace.A, globalMap);
                    var sb = RowGeometry.Source(surface.ContentMap, c, PageSpace.B, globalMap);
                    parts[i].Add(new(c, d, sa?.Bounds, sb?.Bounds, box.Count));
                }
            }
            return content.Clusters.Select((c, i) =>
            {
                if (parts[i].Sum(p => p.Pixels) != c.Pixels) throw new InvalidOperationException("クラスタの所属画素数が一致していません。");
                var info = new RowClusterProjection(c.Id, c.Bounds, c.FillRatio, parts[i].AsReadOnly(),
                    RowGeometry.Source(parts[i].Where(p => p.SourceA is not null).Select(p => p.SourceA!.Value)),
                    RowGeometry.Source(parts[i].Where(p => p.SourceB is not null).Select(p => p.SourceB!.Value)));
                return c with { Bounds = RowGeometry.Union(parts[i].Select(p => p.DisplayBounds)), Row = info };
            }).ToArray();
        }
    }

    internal static MovementShift? ProjectMovement(RowComparisonSurface surface, MovementProjectionProof proof)
    {
        var source = Offset(proof.Template.Source); var target = Offset(proof.Template.Destination);
        if (source is null || target is null || proof.ValidationWindows.Any(w => Offset(w.Source) is null || Offset(w.Destination) is null)) return null;
        return new(proof.Shift.Dx, checked(proof.Shift.Dy + target.Value - source.Value));

        int? Offset(Rect area)
        {
            if (area.Width <= 0 || area.Height <= 0 || area.X < 0 || area.Y < 0 || area.Right > surface.ContentMap.CanvasSize.Width
                || area.Bottom > surface.ContentMap.CanvasSize.Height) return null;
            int? offset = null;
            foreach (var piece in surface.Pieces)
            {
                if (area.Top >= piece.ContentStart + piece.Length || area.Bottom <= piece.ContentStart) continue;
                var value = piece.DisplayStart - piece.ContentStart;
                if (offset is not null && offset != value) return null;
                offset = value;
            }
            return offset;
        }
    }

    private sealed class PixelBounds
    {
        private int left = int.MaxValue, top = int.MaxValue, right, bottom;
        internal int Count { get; private set; }
        internal Rect Bounds => new(left, top, right - left + 1, bottom - top + 1);
        internal void Add(int x, int y)
        { Count++; left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
    }
}
