using System.Runtime.Versioning;
using OpenCvSharp;
using PDFtoImage;
using ReportDiff.Pdf;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class PdfReaderTests
{
    private static readonly (float Width, float Height)[] PageSizes = [(144, 216), (216, 144), (101, 151)];

    public static IEnumerable<object[]> PageAndDpiCases =>
        from page in new[] { 1, 2, 3 }
        from dpi in new[] { 72, 150, 300, 1200 }
        select new object[] { page, dpi };

    [Theory]
    [MemberData(nameof(PageAndDpiCases))]
    public void SelectedPageHasExpectedSizeAndBgrPixels(int pageNumber, int dpi)
    {
        using var file = new PdfTestFile(PdfFixture.CreatePages(PageSizes));
        using var reader = PdfReader.Open(file.FilePath);
        Assert.Equal(3, reader.PageCount);
        using var image = reader.ReadPage(pageNumber, dpi);
        Assert.Equal(InputFormat.Pdf, image.Format);
        Assert.Equal(dpi, image.Dpi);
        Assert.Equal(MatType.CV_8UC3, image.Pixels.Type());
        var size = PageSizes[pageNumber - 1];
        Assert.InRange(Math.Abs(image.Pixels.Width - size.Width / 72.0 * dpi), 0, 1);
        Assert.InRange(Math.Abs(image.Pixels.Height - size.Height / 72.0 * dpi), 0, 1);
        var color = PdfFixture.PageColors[pageNumber - 1];
        Assert.Equal(new Vec3b(color.Blue, color.Green, color.Red), PixelAtPoints(image, 20, 20));
        Assert.Equal(new Vec3b(255, 255, 255), PixelAtPoints(image, 2, 2));
        var blended = PixelAtPoints(image, 50, 20);
        Assert.InRange(blended.Item0, (byte)126, (byte)128);
        Assert.InRange(blended.Item1, (byte)126, (byte)128);
        Assert.Equal((byte)255, blended.Item2);
    }

    [Fact]
    public void RepeatedOutOfOrderReadsRetainIndependentPixelsAndDefaultDpi()
    {
        using var file = new PdfTestFile(PdfFixture.CreatePages(PageSizes));
        using var reader = PdfReader.Open(file.FilePath);
        using var third = reader.ReadPage(3);
        using var first = reader.ReadPage(1, 72);
        using var thirdAgain = reader.ReadPage(3);
        Assert.Equal(300, third.Dpi);
        Assert.Equal(0, Cv2.Norm(third.Pixels, thirdAgain.Pixels, NormTypes.INF));
        reader.Dispose();
        reader.Dispose();
        File.Delete(file.FilePath); // Windows でも読み込み後にファイルを閉じる契約を確認する。
        Assert.Equal(new Vec3b(0, 0, 255), PixelAtPoints(first, 20, 20));
        Assert.Equal(new Vec3b(255, 0, 0), PixelAtPoints(third, 20, 20));
        Assert.Throws<ObjectDisposedException>(() => reader.ReadPage(1));
    }

    [Theory]
    [InlineData(72)]
    [InlineData(300)]
    [InlineData(1200)]
    public void FractionalMediaBoxUsesThePdfDimensions(int dpi)
    {
        // SkiaSharp は作成時にページ寸法を整数 pt に丸めるため、MediaBox を直接指定する。
        using var file = new PdfTestFile(PdfFixture.CreateFractionalPage());
        using var reader = PdfReader.Open(file.FilePath);
        using var image = reader.ReadPage(1, dpi);
        Assert.InRange(Math.Abs(image.Pixels.Width - 100.25 / 72 * dpi), 0, 1);
        Assert.InRange(Math.Abs(image.Pixels.Height - 150.75 / 72 * dpi), 0, 1);
    }

    [Theory]
    [InlineData(16001, 72, 72)]
    [InlineData(72, 16001, 72)]
    [InlineData(4000, 72, 300)]
    [InlineData(72, 4000, 300)]
    public void OversizedPageIsRejectedWithAdviceToReduceDpi(float width, float height, int dpi)
    {
        using var file = new PdfTestFile(PdfFixture.CreatePages((width, height)));
        using var reader = PdfReader.Open(file.FilePath);
        var error = Assert.Throws<PdfReadException>(() => reader.ReadPage(1, dpi));
        Assert.Contains("16000px", error.Message);
        Assert.Contains("dpi を下げて", error.Message);
    }

    [Theory]
    [InlineData(16000, 72)]
    [InlineData(72, 16000)]
    public void ExactlySixteenThousandPixelsIsAllowed(float width, float height)
    {
        using var file = new PdfTestFile(PdfFixture.CreatePages((width, height)));
        using var reader = PdfReader.Open(file.FilePath);
        using var image = reader.ReadPage(1, 72);
        Assert.Equal((int)width, image.Pixels.Width);
        Assert.Equal((int)height, image.Pixels.Height);
    }

    [Fact]
    public void UnselectedLargePageDoesNotBlockSmallPageAndDpiCanBeReduced()
    {
        using var file = new PdfTestFile(PdfFixture.CreatePages((4000, 72), (144, 216)));
        using var reader = PdfReader.Open(file.FilePath);
        using var small = reader.ReadPage(2);
        Assert.Equal(new Vec3b(0, 255, 0), PixelAtPoints(small, 20, 20));
        Assert.Throws<PdfReadException>(() => reader.ReadPage(1));
        using var reduced = reader.ReadPage(1, 72);
        Assert.Equal(4000, reduced.Pixels.Width);
    }

    [Fact]
    public void AnnotationsAndFormsAreBothRendered()
    {
        using var file = new PdfTestFile(PdfFixture.CreateAnnotationAndForm());
        using var reader = PdfReader.Open(file.FilePath);
        using var image = reader.ReadPage(1, 72);
        Assert.Equal(new Vec3b(0, 255, 0), image.Pixels.At<Vec3b>(80, 20));
        Assert.Equal(new Vec3b(0, 0, 255), image.Pixels.At<Vec3b>(80, 60));
        // この合成データが通常のページ内容ではなく、各描画オプションを検証していることも確かめる。
        using var stream = File.OpenRead(file.FilePath);
        using var without = Conversion.ToImage(stream, options: new RenderOptions(Dpi: 72));
        Assert.Equal(SkiaSharp.SKColors.White, without.GetPixel(20, 80));
        Assert.Equal(SkiaSharp.SKColors.White, without.GetPixel(60, 80));
    }

    [Fact]
    public void DiagonalPathsAreAntialiased()
    {
        using var file = new PdfTestFile(PdfFixture.CreatePages((72, 72)));
        using var reader = PdfReader.Open(file.FilePath);
        using var image = reader.ReadPage(1, 72);
        var intermediatePixels = 0;
        for (var y = 39; y < 69; y++)
        for (var x = 9; x < 62; x++)
        {
            var pixel = image.Pixels.At<Vec3b>(y, x);
            if (pixel.Item0 is > 0 and < 255 && pixel.Item0 == pixel.Item1 && pixel.Item1 == pixel.Item2)
                intermediatePixels++;
        }
        Assert.True(intermediatePixels > 20, "斜線の端にアンチエイリアスの中間色が必要です。");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void InvalidPageNumbersHaveJapaneseErrors(int page)
    {
        using var file = new PdfTestFile(PdfFixture.CreatePages(PageSizes));
        using var reader = PdfReader.Open(file.FilePath);
        Assert.Contains("ページ番号", Assert.Throws<PdfReadException>(() => reader.ReadPage(page)).Message);
    }

    [Theory]
    [InlineData(71)]
    [InlineData(1201)]
    public void InvalidDpiHasJapaneseError(int dpi)
    {
        using var file = new PdfTestFile(PdfFixture.CreatePages((72, 72)));
        using var reader = PdfReader.Open(file.FilePath);
        Assert.Contains("DPI", Assert.Throws<PdfReadException>(() => reader.ReadPage(1, dpi)).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%PDF-1.7\nnot a document")]
    [InlineData("not a PDF")]
    public void InvalidFilesHaveJapaneseErrorsAndReleaseTheHandle(string content)
    {
        using var file = new PdfTestFile(System.Text.Encoding.ASCII.GetBytes(content));
        Assert.Contains("PDF", Assert.Throws<PdfReadException>(() => PdfReader.Open(file.FilePath)).Message);
        using var exclusive = new FileStream(file.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void MissingFileHasJapaneseError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"存在しない帳票-{Guid.NewGuid():N}.pdf");
        Assert.Contains("PDF ファイル", Assert.Throws<PdfReadException>(() => PdfReader.Open(path)).Message);
    }

    [Fact]
    public async Task CallsAcrossReadersReturnCorrectPages()
    {
        using var file = new PdfTestFile(PdfFixture.CreatePages(PageSizes));
        await Task.WhenAll(Enumerable.Range(0, 6).Select(index => Task.Run(() =>
        {
            using var reader = PdfReader.Open(file.FilePath);
            var page = index % 3 + 1;
            using var image = reader.ReadPage(page, 72);
            var color = PdfFixture.PageColors[page - 1];
            Assert.Equal(new Vec3b(color.Blue, color.Green, color.Red), PixelAtPoints(image, 20, 20));
        })));
    }

    private static Vec3b PixelAtPoints(LoadedImage image, int x, int y) =>
        image.Pixels.At<Vec3b>((int)(y / 72.0 * image.Dpi), (int)(x / 72.0 * image.Dpi));
}
