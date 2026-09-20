using System.Globalization;
using System.Text;
using SkiaSharp;

namespace ReportDiff.Tests;

internal static class PdfFixture
{
    public static readonly SKColor[] PageColors = [SKColors.Red, new(0, 255, 0), SKColors.Blue];

    /// <summary>1 ページ目は固定、2 ページ目だけ本文と出力日時相当の領域を変更する合成帳票。</summary>
    public static byte[] CreateComparisonReport(bool revised)
    {
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            for (var page = 1; page <= 2; page++)
            {
                // 216 × 288 pt = 76.2 × 101.6 mm。フォントの違いが結果に影響しない図形だけを使う。
                using var canvas = document.BeginPage(216, 288);
                using var ink = new SKPaint { Color = new SKColor(40, 40, 40), IsAntialias = true };
                canvas.DrawRect(24, 24, 72, 12, ink);
                using var grid = new SKPaint { Color = SKColors.Gray, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
                canvas.DrawRect(24, 72, 168, 156, grid);
                for (var y = 96; y < 228; y += 48) canvas.DrawLine(24, y, 192, y, grid);
                canvas.DrawLine(108, 72, 108, 228, grid);
                var changed = page == 2 && revised;
                // 検出対象: x=12.7, y=38.1, w=12.7, h=6.35 mm。
                using var body = new SKPaint { Color = changed ? new SKColor(190, 45, 60) : new SKColor(45, 95, 180), IsAntialias = true };
                canvas.DrawRect(36, 108, 36, 18, body);
                // 除外対象: x=50.8, y=8.466..., w=12.7, h=4.233... mm。
                using var timestamp = new SKPaint { Color = changed ? new SKColor(80, 80, 80) : new SKColor(210, 210, 210), IsAntialias = true };
                canvas.DrawRect(144, 24, 36, 12, timestamp);
                document.EndPage();
            }
            document.Close();
        }
        return stream.ToArray();
    }

    /// <summary>追加・削除・色変更・背景変更を離れた行に置く、分類確認用の合成 PDF。</summary>
    public static byte[] CreateClassificationReport(bool revised)
    {
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            using var canvas = document.BeginPage(216, 216);
            using var ink = new SKPaint { Color = SKColors.Black };
            if (revised) canvas.DrawRect(24, 24, 24, 6, ink);
            if (!revised) canvas.DrawRect(24, 60, 24, 6, ink);
            ink.Color = revised ? new SKColor(160, 0, 0) : new SKColor(0, 0, 160);
            canvas.DrawRect(24, 96, 24, 6, ink);
            ink.Color = revised ? new SKColor(242, 242, 242) : SKColors.White;
            canvas.DrawRect(24, 132, 24, 12, ink);
            document.EndPage();
            document.Close();
        }
        return stream.ToArray();
    }

    /// <summary>別クラスタになる移動と、同じクラスタに収まる移動を持つ合成 PDF。</summary>
    public static byte[] CreateMovementReport(bool revised)
    {
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            using var canvas = document.BeginPage(216, 216);
            using var ink = new SKPaint { Color = SKColors.Black };
            foreach (var (y, shift) in new[] { (36f, 12f), (84f, 6f) })
            {
                var x = 36 + (revised ? shift : 0);
                canvas.DrawRect(x, y, 0.72f, 3.60f, ink);
                canvas.DrawRect(x, y + 2.88f, 2.16f, 0.72f, ink);
            }
            document.EndPage();
            document.Close();
        }
        return stream.ToArray();
    }

    public static byte[] CreatePages(params (float Width, float Height)[] sizes)
    {
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            for (var i = 0; i < sizes.Length; i++)
            {
                using var canvas = document.BeginPage(sizes[i].Width, sizes[i].Height);
                // 背景は描かず、PDF 読み込み側の白背景を検証する。
                using var rectangle = new SKPaint { Color = PageColors[i % PageColors.Length], IsAntialias = true };
                canvas.DrawRect(10, 10, 20, 20, rectangle);
                using var translucent = new SKPaint { Color = new SKColor(255, 0, 0, 128), IsAntialias = true };
                canvas.DrawRect(40, 10, 20, 20, translucent);
                using var line = new SKPaint { Color = SKColors.Black, StrokeWidth = 1, IsAntialias = true };
                canvas.DrawLine(10, 40, 60, 67, line);
                document.EndPage();
            }
            document.Close();
        }
        return stream.ToArray();
    }

    // SkiaSharp では作れない注釈・フォームの外観だけを持つ、フォントに依存しない合成 PDF。
    public static byte[] CreateAnnotationAndForm()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 8 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R 6 0 R] >>",
            "<< /Type /Annot /Subtype /Square /Rect [10 10 30 30] /F 4 /AP << /N 5 0 R >> >>",
            Appearance("0 1 0 rg 0 0 20 20 re f\n"),
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (test) /V (value) /Rect [50 10 70 30] /F 4 /P 3 0 R /AP << /N 7 0 R >> >>",
            Appearance("1 0 0 rg 0 0 20 20 re f\n"),
            "<< /Fields [6 0 R] /NeedAppearances false >>"
        ];
        return CreateDocument(objects);
    }

    public static byte[] CreateFractionalPage() => CreateDocument(
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100.25 150.75] /Resources << >> >>"
    ]);

    private static byte[] CreateDocument(string[] objects)
    {
        using var stream = new MemoryStream();
        void Write(string value) => stream.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(stream.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = stream.Position;
        Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return stream.ToArray();
    }

    private static string Appearance(string content) =>
        $"<< /Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 20 20] /Resources << >> /Length {content.Length} >>\nstream\n{content}endstream";
}

internal sealed class PdfTestFile : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"reportdiff-PDF 試験-{Guid.NewGuid():N}");
    public string FilePath { get; }

    public PdfTestFile(byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        // 内容の判定と日本語・空白パスを、すべての PDF テストで通す。
        FilePath = Path.Combine(directory, "帳票 新版.png");
        File.WriteAllBytes(FilePath, bytes);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
