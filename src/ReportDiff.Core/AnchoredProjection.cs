using OpenCvSharp;
using System.Runtime.InteropServices;

namespace ReportDiff.Core;

internal static class AnchoredProjection
{
    internal readonly record struct Destination(int Top, int End, int Offset, ContentDisplaySides Sides);
    internal static IEnumerable<Destination> Destinations(AnchoredContentPiece piece, AnchoredDisplayPlan display)
    {
        for (var top = piece.Top; top < piece.Top + piece.Height;)
        {
            var a = Map(piece.A, top); var b = Map(piece.B, top); var end = Math.Min(a.End, b.End);
            if (a.Offset is { } ax && b.Offset == ax) yield return new(top, end, ax, ContentDisplaySides.A | ContentDisplaySides.B);
            else
            {
                if (a.Offset is { } ao) yield return new(top, end, ao, ContentDisplaySides.A);
                if (b.Offset is { } bo) yield return new(top, end, bo, ContentDisplaySides.B);
            }
            top = end;
        }
        (int? Offset, int End) Map(OriginalRowSpan? source, int top)
        {
            if (source is null || source.Page.Page != display.Page) return (null, piece.Top + piece.Height);
            var oy = source.Top + top - piece.Top;
            foreach (var segment in display.Segments)
            {
                var span = source.Page.Side == PageSpace.A ? segment.Band.A : segment.Band.B;
                if (span is null || oy < span.Top || oy >= span.Bottom) continue;
                return (segment.Band.Top - span.Top + source.Top - piece.Top,
                    Math.Min(piece.Top + piece.Height, piece.Top + span.Bottom - source.Top));
            }
            throw new InvalidOperationException("元行の表示先がありません。");
        }
    }

    internal static void Parts(AnchoredContentSurface surface, IReadOnlyList<AnchoredDisplayPlan> displays,
        AnchoredContentResult content, AnchoredBuffer<ContentDisplayPart> output)
    {
        foreach (var cluster in content.Clusters)
        {
            var anchorPixels = 0;
            foreach (var piece in surface.Pieces)
            foreach (var display in displays)
            foreach (var destination in Destinations(piece, display))
            {
                int pixels = 0, left = int.MaxValue, top = int.MaxValue, right = 0, bottom = 0;
                foreach (var run in content.Runs)
                {
                    if (run.ClusterId != cluster.Id || run.Y < destination.Top || run.Y >= destination.End) continue;
                    pixels += run.Length; left = Math.Min(left, run.X); right = Math.Max(right, run.X + run.Length);
                    top = Math.Min(top, run.Y); bottom = Math.Max(bottom, run.Y + 1);
                }
                if (pixels == 0) continue;
                var c = new Rect(left, top, right - left, bottom - top);
                output.Add(new(new(surface.Id, cluster.Id), display.Page, c,
                    new(c.X, c.Y + destination.Offset, c.Width, c.Height), Source(piece.A), Source(piece.B), destination.Sides, pixels));
                if (display.Page == surface.Id.OwnerPage && destination.Sides.HasFlag(surface.Anchor.Side == PageSpace.A
                    ? ContentDisplaySides.A : ContentDisplaySides.B)) anchorPixels += pixels;
                OriginalPixelBounds? Source(OriginalRowSpan? span) => span is null ? null
                    : new(span.Page, new(c.X, c.Y + span.Top - piece.Top, c.Width, c.Height));
            }
            if (anchorPixels != cluster.Pixels) throw new InvalidOperationException("保持側の内容画素数が変わりました。");
        }
    }

    internal static void Annotations(AnchoredContentSurface surface, IReadOnlyList<AnchoredDisplayPlan> displays,
        PageComparison content, IReadOnlyList<ContentDisplayPart> parts, AnchoredBuffer<AnchoredDisplayAnnotation> output)
    {
        foreach (var cluster in content.Clusters)
        foreach (var display in displays)
        {
            if (!parts.Any(p => p.Content.ClusterId == cluster.Id && p.DisplayPage == display.Page)) continue;
            var shift = cluster.ShiftPx; string? reason = null;
            if (cluster.Kind == "moved")
            {
                if (content.ProjectionData?.Movements.TryGetValue(cluster.Id, out var proof) == true)
                {
                    shift = ProjectMovement(surface, displays, proof);
                    if (shift is null) reason = "nonuniform_display_mapping";
                }
                else { shift = null; reason = "movement_proof_unavailable"; }
            }
            output.Add(new(new(surface.Id, cluster.Id), display.Page, reason is null ? cluster.Kind : "changed", shift,
                reason is null ? cluster.RelatedClusterIds : [], reason));
        }
    }

    internal static MovementShift? ProjectMovement(AnchoredContentSurface surface, IReadOnlyList<AnchoredDisplayPlan> displays,
        MovementProjectionProof proof)
    {
        (int Page, int Offset)? source = Offset(proof.Template.Source), target = Offset(proof.Template.Destination);
        if (source is null || target is null || source.Value.Page != target.Value.Page) return null;
        foreach (var window in proof.ValidationWindows)
            if (Offset(window.Source) != source || Offset(window.Destination) != target) return null;
        return new(proof.Shift.Dx, checked(proof.Shift.Dy + target.Value.Offset - source.Value.Offset));

        (int Page, int Offset)? Offset(Rect rect)
        {
            if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0 || rect.Right > surface.Size.Width || rect.Bottom > surface.Size.Height)
                return null;
            (int Page, int Offset)? result = null; var covered = 0;
            foreach (var piece in surface.Pieces)
            {
                var lo = Math.Max(piece.Top, rect.Top); var hi = Math.Min(piece.Top + piece.Height, rect.Bottom);
                if (lo >= hi) continue;
                var local = 0;
                foreach (var display in displays)
                foreach (var destination in Destinations(piece, display))
                {
                    var length = Math.Min(hi, destination.End) - Math.Max(lo, destination.Top); if (length <= 0) continue;
                    var value = (display.Page, destination.Offset);
                    if (result is not null && result.Value != value) return null;
                    result = value; local += length;
                }
                // A/B別の別投影や欠落を許さない。
                if (local != hi - lo) return null;
                covered += local;
            }
            return covered == rect.Height ? result : null;
        }
    }

    internal static AnchoredDisplayResult Display(AnchoredContentPlan plan, int page, IReadOnlyList<AnchoredContentResult> content,
        IReadOnlyList<AnchoredStructure> structures)
    {
        plan.CheckCurrent(); var display = plan.Displays.Single(d => d.Page == page);
        Mat? raw = null, label = null, removal = null; var reserve = new AnchoredReservations(plan.Budget); var transferred = false;
        try
        {
            reserve.Add(AnchoredAllocation.Reference, content.Sum(c => (long)c.Parts.Count + c.Annotations.Count) + structures.Count);
            reserve.Add(AnchoredAllocation.RowBuffer, display.Size.Width);
            var white = new byte[display.Size.Width]; Array.Fill(white, (byte)255);
            raw = new(display.Size, MatType.CV_8UC1, Scalar.Black); label = new(display.Size, MatType.CV_8UC1, Scalar.Black);
            removal = new(display.Size, MatType.CV_8UC1, Scalar.Black);
            foreach (var c in content)
            {
                ObjectDisposedException.ThrowIf(c.IsDisposed, c);
                var surface = plan.Surfaces.Single(s => s.Id == c.Id);
                foreach (var piece in surface.Pieces)
                foreach (var d in Destinations(piece, display))
                foreach (var run in c.Runs)
                {
                    if (run.Y < d.Top || run.Y >= d.End) continue;
                    var y = run.Y + d.Offset;
                    if (run.Flags.HasFlag(AnchoredMaskFlags.Raw)) Fill(raw);
                    if (run.Flags.HasFlag(AnchoredMaskFlags.Label)) Fill(label);
                    if (run.Flags.HasFlag(AnchoredMaskFlags.Removal)) Fill(removal);
                    void Fill(Mat mask) => Marshal.Copy(white, 0, IntPtr.Add(mask.Ptr(y), run.X), run.Length);
                }
            }
            var result = new AnchoredDisplayResult(page, raw, label, removal,
                Array.AsReadOnly(content.SelectMany(c => c.Parts).Where(p => p.DisplayPage == page).ToArray()),
                Array.AsReadOnly(content.SelectMany(c => c.Annotations).Where(p => p.DisplayPage == page).ToArray()), Array.AsReadOnly(structures.Where(s => s.Structure.Reference.Page == page).ToArray()), reserve);
            raw = label = removal = null; transferred = true; return result;
        }
        finally { raw?.Dispose(); label?.Dispose(); removal?.Dispose(); if (!transferred) reserve.Dispose(); }
    }
}
