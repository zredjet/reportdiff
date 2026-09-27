using OpenCvSharp;
using ReportDiff.Core;

internal static class ProbeMasks
{
    internal static RectMm Band(int y, int width, int height, Size canvas)
    {
        // 既存の floor / ceil を通した結果が、指定した整数の帯と厳密に一致する mm 値。
        // px→mm→px の丸め誤差で、外側の 1px まで除外してしまうことを防ぐ。
        var mm = new RectMm(0, Units.PixelsToMm(y + 0.25, 300),
            Units.PixelsToMm(width, 300), Units.PixelsToMm(height - 0.5, 300));
        if (PageMap.CanvasRectangle(mm, 300, canvas) != new Rect(0, y, width, height))
            throw new InvalidOperationException("除外マスクが指定した整数の帯と一致しません。");
        return mm;
    }
}
