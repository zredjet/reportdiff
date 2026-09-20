namespace ReportDiff.Core;

public static class Units
{
    public static double MmToPixels(double mm, int dpi) => mm / 25.4 * dpi;
    public static double PixelsToMm(double pixels, int dpi) => pixels / dpi * 25.4;
    public static double PointsToPixels(double points, int dpi) => points / 72 * dpi;
    public static int RoundPixels(double mm, int dpi) => checked((int)Math.Round(MmToPixels(mm, dpi)));
}
