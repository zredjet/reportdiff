using OpenCvSharp;

namespace ReportDiff.Core;

public sealed record RowSourceBounds(PageBounds Bounds, IReadOnlyList<PageBounds> Parts);
public sealed record RowStructuralChange(int Id, string Kind, Rect DisplayBounds, RowSourceBounds? SourceA, RowSourceBounds? SourceB,
    int BandCount, MovementShift? DisplacementPx, bool Excluded, string? ExclusionReason,
    long ContentPixelsA, long ContentPixelsB, long ExcludedContentPixelsA, long ExcludedContentPixelsB,
    IReadOnlyList<RowEquivalentPosition> EquivalentPositions);
public sealed record RowOmissionAudit(long OmittedBandPixels, long PreservedWhitePixels, long OmittedContentPixels,
    long UserExcludedOmittedContentPixels, int? OmittedCandidateRawPixels, string RawCountReason);

internal static class RowStructures
{
    internal static (IReadOnlyList<RowStructuralChange> Changes, RowOmissionAudit Audit) Build(Mat a, Mat b,
        RowComparisonSurface surface, RegionMap displaySettings, IReadOnlyList<RowEquivalentPosition> equivalents, PageMap? globalMap)
    {
        var da = MatBuffers.Bytes(a); var db = MatBuffers.Bytes(b); var width = a.Width;
        var structural = surface.OmittedBands.ToHashSet();
        var changes = new List<RowStructuralChange>();
        var pending = new List<PageSegment>(); string? kind = null;
        foreach (var band in surface.DisplayMap.Segments)
        {
            var next = structural.Contains(band) ? band.AStart is null ? "inserted" : "deleted"
                : band.Dy is { } dy && dy != 0 ? "block_moved" : null;
            if (pending.Count > 0 && (kind != next || pending[^1].CanvasStart + pending[^1].Length != band.CanvasStart
                || kind == "block_moved" && pending[^1].Dy != band.Dy)) Flush();
            if (next is null) continue;
            kind = next; pending.Add(band);
        }
        Flush();
        var omitted = changes.Where(c => c.Kind is "inserted" or "deleted").ToArray();
        return (changes.AsReadOnly(), new(surface.OmittedPixels, surface.PreservedWhitePixels,
            omitted.Sum(c => c.ContentPixelsA + c.ContentPixelsB),
            omitted.Sum(c => c.ExcludedContentPixelsA + c.ExcludedContentPixelsB), null, "not_compared_in_content_canvas"));

        void Flush()
        {
            if (pending.Count == 0) return;
            long pixelsA = 0, pixelsB = 0, excludedA = 0, excludedB = 0;
            foreach (var segment in pending)
            {
                Count(da, segment.AStart, segment, ref pixelsA, ref excludedA);
                Count(db, segment.BStart, segment, ref pixelsB, ref excludedB);
            }
            // 純白だけの対応区間をブロック移動として数えない。254以下の塗り・罫線は内容。
            if (pixelsA + pixelsB > 0)
            {
                var bounds = new Rect(0, pending[0].CanvasStart, width, pending.Sum(s => s.Length));
                var sourceA = RowGeometry.Source(surface.DisplayMap, bounds, PageSpace.A, globalMap);
                var sourceB = RowGeometry.Source(surface.DisplayMap, bounds, PageSpace.B, globalMap);
                var excluded = pixelsA == excludedA && pixelsB == excludedB;
                var positions = kind == "block_moved" ? [] : equivalents.Where(e => pending.Any(s =>
                    e.Side == PageSpace.A ? s.AStart == e.FirstStart : s.BStart == e.FirstStart)).ToArray();
                changes.Add(new(changes.Count + 1, kind!, bounds, sourceA, sourceB, pending.Count,
                    kind == "block_moved" ? new(0, checked((int)-pending[0].Dy!.Value)) : null,
                    excluded, excluded ? "all_content_excluded" : null, pixelsA, pixelsB, excludedA, excludedB, positions));
            }
            pending.Clear(); kind = null;
        }

        void Count(byte[] bytes, int? source, PageSegment segment, ref long count, ref long excluded)
        {
            if (source is not int start) return;
            for (var row = 0; row < segment.Length; row++)
            for (var x = 0; x < width; x++)
            {
                var pixel = ((start + row) * width + x) * 3;
                if (bytes[pixel] == 255 && bytes[pixel + 1] == 255 && bytes[pixel + 2] == 255) continue;
                count++;
                if (displaySettings.Excluded[(segment.CanvasStart + row) * width + x]) excluded++;
            }
        }
    }
}

internal static class RowGeometry
{
    internal static RowSourceBounds? Source(PageMap map, Rect bounds, PageSpace side, PageMap? globalMap)
    {
        var parts = map.MapBoundsParts(new(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), PageSpace.Canvas, side);
        if (globalMap is not null) parts = parts.SelectMany(p => globalMap.MapBoundsParts(p, PageSpace.Canvas, side)).ToArray();
        return Source(parts);
    }

    internal static RowSourceBounds? Source(IEnumerable<PageBounds> parts)
    {
        var all = parts.ToArray();
        return all.Length == 0 ? null : new(new(all.Min(b => b.Left), all.Min(b => b.Top), all.Max(b => b.Right), all.Max(b => b.Bottom)),
            Array.AsReadOnly(all));
    }

    internal static Rect Union(IEnumerable<Rect> parts)
    {
        var all = parts.Where(b => b.Width > 0 && b.Height > 0).ToArray();
        return all.Length == 0 ? new() : new(all.Min(b => b.Left), all.Min(b => b.Top),
            all.Max(b => b.Right) - all.Min(b => b.Left), all.Max(b => b.Bottom) - all.Min(b => b.Top));
    }
}
