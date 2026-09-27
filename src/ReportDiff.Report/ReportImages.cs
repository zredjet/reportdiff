using System.Globalization;
using OpenCvSharp;
using ReportDiff.Core;

namespace ReportDiff.Report;

internal static class ReportImages
{
    private static readonly Scalar Red = new(0, 0, 255);

    public static Mat Overlay(Mat b, PageComparison comparison, IEnumerable<ReportExclusion> exclusions, int dpi,
        IReadOnlyList<Rect>? pixelExclusions = null)
    {
        var output = b.Clone();
        try
        {
            using var excluded = new Mat(b.Size(), MatType.CV_8UC1, Scalar.All(0));
            foreach (var box in pixelExclusions ?? exclusions.Select(e => PageMap.CanvasRectangle(new(e.X, e.Y, e.W, e.H), dpi, b.Size())).ToArray())
            {
                if (box.Width > 0 && box.Height > 0) Cv2.Rectangle(excluded, box, Scalar.All(255), -1);
            }
            Tint(output, excluded, new Scalar(0, 255, 255));
            if (comparison.Regional is { } regional)
            {
                Tint(output, regional.SuppressedMask, new Scalar(170, 210, 255));
                RegionDrawing.Draw(output, regional, dpi);
            }
            output.SetTo(Red, comparison.RawMask);
            Cv2.FindContours(comparison.LabelMask, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
            Cv2.DrawContours(output, contours, -1, Red, 1, LineTypes.Link8);
            foreach (var cluster in comparison.Clusters)
            {
                if (cluster.Row is not { } row) DrawNumber(output, cluster.Id.ToString(CultureInfo.InvariantCulture), cluster.Bounds, dpi, Red);
                else foreach (var part in row.Parts) DrawNumber(output, cluster.Id.ToString(CultureInfo.InvariantCulture), part.DisplayBounds, dpi, Red);
            }
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    public static Mat RowOverlay(Mat b, RowDisplayProjection projection, int dpi)
    {
        var output = Overlay(b, projection.Comparison, [], dpi, projection.ExcludedBounds);
        try
        {
            foreach (var change in projection.StructuralChanges)
            {
                var color = change.Excluded ? Scalar.All(128) : new Scalar(190, 0, 190);
                Cv2.Rectangle(output, change.DisplayBounds, color, 1, LineTypes.Link8);
                DrawNumber(output, "S" + change.Id.ToString(CultureInfo.InvariantCulture), change.DisplayBounds, dpi, color);
            }
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    public static Mat DifferenceCrop(Mat b, Mat raw, Rect bounds, Mat? removal = null)
    {
        using var source = new Mat(b, bounds);
        using var mask = new Mat(raw, bounds);
        var output = source.Clone();
        try
        {
            output.SetTo(Red, mask);
            if (removal is not null)
            {
                using var removed = new Mat(removal, bounds);
                output.SetTo(new Scalar(0, 255, 0), removed);
            }
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    public static Rect CropBounds(Rect bounds, Size size, double marginMm, int dpi) =>
        PageMap.CropRectangle(bounds, size, marginMm, dpi);

    private static void Tint(Mat image, Mat mask, Scalar color)
    {
        if (Cv2.CountNonZero(mask) == 0) return;
        using var painted = image.Clone();
        painted.SetTo(color, mask);
        Cv2.AddWeighted(image, 0.5, painted, 0.5, 0, image);
    }

    private static void DrawNumber(Mat image, string text, Rect bounds, int dpi, Scalar color)
    {
        var scale = Math.Clamp(dpi / 300.0 * 0.65, 0.45, 2.6);
        var size = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, scale, 1, out var baseline);
        var width = Math.Min(image.Width, size.Width + 4);
        var height = Math.Min(image.Height, size.Height + baseline + 4);
        var x = Math.Clamp(bounds.X, 0, image.Width - width);
        var y = bounds.Y >= height + 2 ? bounds.Y - height - 2 : bounds.Bottom + 2;
        y = Math.Clamp(y, 0, image.Height - height);
        Cv2.Rectangle(image, new Rect(x, y, width, height), Scalar.All(255), -1);
        Cv2.PutText(image, text, new Point(x + 2, y + size.Height + 2), HersheyFonts.HersheySimplex, scale, color, 1, LineTypes.AntiAlias);
    }
}
