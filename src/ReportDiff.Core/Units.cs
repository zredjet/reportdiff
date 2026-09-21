namespace ReportDiff.Core;

public static class Units
{
    public static OpenCvSharp.Rect ClipRectangle(RectMm r, int dpi, int width, int height)
    {
        var left = (int)Math.Clamp(Math.Floor(MmToPixels(r.X, dpi)), 0, width);
        var top = (int)Math.Clamp(Math.Floor(MmToPixels(r.Y, dpi)), 0, height);
        var right = (int)Math.Clamp(Math.Ceiling(MmToPixels(r.X + r.W, dpi)), 0, width);
        var bottom = (int)Math.Clamp(Math.Ceiling(MmToPixels(r.Y + r.H, dpi)), 0, height);
        return new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
    public static double MmToPixels(double mm, int dpi) => mm / 25.4 * dpi;
    public static double PixelsToMm(double pixels, int dpi) => pixels / dpi * 25.4;
    public static double SquareMmToPixels(double areaMm2, int dpi) => areaMm2 * Math.Pow(MmToPixels(1, dpi), 2);
    public static double PointsToPixels(double points, int dpi) => points / 72 * dpi;
    public static int RoundPixels(double mm, int dpi) => checked((int)Math.Round(MmToPixels(mm, dpi)));
}
