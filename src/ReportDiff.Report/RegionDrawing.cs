using OpenCvSharp;
using ReportDiff.Core;
using SkiaSharp;

namespace ReportDiff.Report;

internal static class RegionDrawing
{
    public static void Draw(Mat image, RegionalComparison regional, int dpi)
    {
        using var bgra = new Mat();
        Cv2.CvtColor(image, bgra, ColorConversionCodes.BGR2BGRA);
        using var bitmap = new SKBitmap();
        if (!bitmap.InstallPixels(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque),
            bgra.Data, checked((int)bgra.Step()))) throw new InvalidOperationException("領域名の描画領域を作成できません。");
        using var canvas = new SKCanvas(bitmap);
        using var dash = SKPathEffect.CreateDash([6, 4], 0);
        using var line = new SKPaint { Color = new SKColor(20, 100, 210), Style = SKPaintStyle.Stroke, StrokeWidth = 1, PathEffect = dash };
        using var text = new SKPaint { Color = new SKColor(20, 80, 170), IsAntialias = true };
        using var background = new SKPaint { Color = new SKColor(255, 255, 255, 235) };
        var size = (float)Math.Clamp(Units.MmToPixels(1.5, dpi), 12, 48);
        foreach (var region in regional.Regions.Where(r => r.Bounds.Width > 0 && r.Bounds.Height > 0))
        {
            var b = region.Bounds;
            canvas.DrawRect(b.X + 0.5f, b.Y + 0.5f, b.Width - 1, b.Height - 1, line);
            var x = (float)Math.Clamp(b.X + 2, 0, Math.Max(0, image.Width - size));
            var y = (float)Math.Clamp(b.Y >= size + 5 ? b.Y - 3 : b.Y + size + 3, size, image.Height);
            var label = $"R{region.Index + 1} {region.Name}";
            // 日本語を含む文字ごとに OS のフォールバックを使う。全文は HTML に残す。
            foreach (var rune in label.EnumerateRunes().Take(128))
            {
                using var typeface = SKFontManager.Default.MatchCharacter(rune.Value);
                using var font = new SKFont(typeface, size);
                var glyph = rune.ToString();
                var width = font.MeasureText(glyph);
                if (x + width + size > image.Width)
                {
                    glyph = "…"; width = font.MeasureText(glyph);
                    canvas.DrawRect(x, y - size, width, size + 3, background);
                    canvas.DrawText(glyph, x, y, SKTextAlign.Left, font, text);
                    break;
                }
                canvas.DrawRect(x, y - size, width, size + 3, background);
                canvas.DrawText(glyph, x, y, SKTextAlign.Left, font, text);
                x += width;
            }
        }
        canvas.Flush();
        Cv2.CvtColor(bgra, image, ColorConversionCodes.BGRA2BGR);
    }
}
