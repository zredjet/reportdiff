using OpenCvSharp;

namespace ReportDiff.Core;

// 検出と分類で共通の、SPEC 5.3 の局所背景に基づくインク定義。
internal static class ImageInk
{
    public static Mat ToLab(Mat image)
    {
        using var normalized = new Mat();
        image.ConvertTo(normalized, MatType.CV_32FC3, 1.0 / 255);
        var lab = new Mat();
        try { Cv2.CvtColor(normalized, lab, ColorConversionCodes.BGR2Lab); return lab; }
        catch { lab.Dispose(); throw; }
    }

    public static Mat FromLab(Mat lab, int dpi, InkOptions options)
    {
        var radius = Math.Max(1, Units.RoundPixels(options.BackgroundRadiusMm, dpi));
        using var lightness = new Mat();
        using var background = new Mat();
        using var difference = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * radius + 1, 2 * radius + 1));
        Cv2.ExtractChannel(lab, lightness, 0);
        Cv2.Dilate(lightness, background, kernel);
        Cv2.Subtract(background, lightness, difference);
        var mask = new Mat();
        try { Cv2.Compare(difference, options.ContrastThreshold, mask, CmpTypes.GT); return mask; }
        catch { mask.Dispose(); throw; }
    }
}
