using System.Globalization;
using OpenCvSharp;
using ReportDiff.Core;

namespace ReportDiff.Report;

internal static class ReportImages
{
    private static readonly Scalar Red = new(0, 0, 255);

    public static Mat Overlay(Mat b, PageComparison comparison, IEnumerable<ReportExclusion> exclusions, int dpi)
    {
        var output = b.Clone();
        try
        {
            using var excluded = new Mat(b.Size(), MatType.CV_8UC1, Scalar.All(0));
            foreach (var e in exclusions)
            {
                var left = (int)Math.Clamp(Math.Floor(Units.MmToPixels(e.X, dpi)), 0, b.Width);
                var top = (int)Math.Clamp(Math.Floor(Units.MmToPixels(e.Y, dpi)), 0, b.Height);
                var right = (int)Math.Clamp(Math.Ceiling(Units.MmToPixels(e.X + e.W, dpi)), 0, b.Width);
                var bottom = (int)Math.Clamp(Math.Ceiling(Units.MmToPixels(e.Y + e.H, dpi)), 0, b.Height);
                if (right > left && bottom > top) Cv2.Rectangle(excluded, new Rect(left, top, right - left, bottom - top), Scalar.All(255), -1);
            }
            Tint(output, excluded, new Scalar(0, 255, 255));
            output.SetTo(Red, comparison.RawMask);
            Cv2.FindContours(comparison.LabelMask, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
            Cv2.DrawContours(output, contours, -1, Red, 1, LineTypes.Link8);
            foreach (var cluster in comparison.Clusters) DrawNumber(output, cluster, dpi);
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

    public static Rect CropBounds(Rect bounds, Size size, double marginMm, int dpi)
    {
        var margin = Units.MmToPixels(marginMm, dpi);
        var left = (int)Math.Max(0, Math.Floor(bounds.X - margin));
        var top = (int)Math.Max(0, Math.Floor(bounds.Y - margin));
        var right = (int)Math.Min(size.Width, Math.Ceiling(bounds.Right + margin));
        var bottom = (int)Math.Min(size.Height, Math.Ceiling(bounds.Bottom + margin));
        return new(left, top, right - left, bottom - top);
    }

    private static void Tint(Mat image, Mat mask, Scalar color)
    {
        if (Cv2.CountNonZero(mask) == 0) return;
        using var painted = image.Clone();
        painted.SetTo(color, mask);
        Cv2.AddWeighted(image, 0.5, painted, 0.5, 0, image);
    }

    private static void DrawNumber(Mat image, DifferenceCluster cluster, int dpi)
    {
        var text = cluster.Id.ToString(CultureInfo.InvariantCulture);
        var scale = Math.Clamp(dpi / 300.0 * 0.65, 0.45, 2.6);
        var size = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, scale, 1, out var baseline);
        var width = Math.Min(image.Width, size.Width + 4);
        var height = Math.Min(image.Height, size.Height + baseline + 4);
        var x = Math.Clamp(cluster.Bounds.X, 0, image.Width - width);
        var y = cluster.Bounds.Y >= height + 2 ? cluster.Bounds.Y - height - 2 : cluster.Bounds.Bottom + 2;
        y = Math.Clamp(y, 0, image.Height - height);
        Cv2.Rectangle(image, new Rect(x, y, width, height), Scalar.All(255), -1);
        Cv2.PutText(image, text, new Point(x + 2, y + size.Height + 2), HersheyFonts.HersheySimplex, scale, Red, 1, LineTypes.AntiAlias);
    }
}
