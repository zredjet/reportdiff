using OpenCvSharp;
using ReportDiff.Core;

namespace ReportDiff.Tests;

internal static class AlignmentFixture
{
    public static Mat Render(int dpi, bool change = false, bool repetitive = false)
    {
        var image = new Mat(Units.RoundPixels(297, dpi), Units.RoundPixels(210, dpi), MatType.CV_8UC3, Scalar.All(255));
        for (var row = 0; row < 30; row++)
        for (var column = 0; column < 20; column++)
            Cv2.PutText(image, change && row == 15 && column == 10 ? "1" : repetitive ? "0"
                : ((row * row + 7 * column + 3 * row * column) % 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Point(Units.RoundPixels(12 + column * 9.5, dpi), Units.RoundPixels(14 + row * 9.2, dpi)),
                HersheyFonts.HersheySimplex, dpi * 1.3 / 300, Scalar.All(0), Math.Max(1, dpi / 150), LineTypes.AntiAlias);
        return image;
    }

    // 製品のコピー処理とは独立してテスト入力を生成する。
    public static Mat Shift(Mat source, double dx, double dy)
    {
        using var transform = Mat.FromArray(new double[,] { { 1, 0, dx }, { 0, 1, dy } });
        var result = new Mat();
        try
        {
            Cv2.WarpAffine(source, result, transform, source.Size(), InterpolationFlags.Linear,
                BorderTypes.Constant, Scalar.All(255));
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
