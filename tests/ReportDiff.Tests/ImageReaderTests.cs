using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using Xunit;

namespace ReportDiff.Tests;

public sealed class ImageReaderTests
{
    [Theory]
    [InlineData(".png", InputFormat.Png)]
    [InlineData(".jpg", InputFormat.Jpeg)]
    [InlineData(".bmp", InputFormat.Bmp)]
    [InlineData(".tiff", InputFormat.Tiff)]
    public void SupportedFormatsDecodeToBgr(string extension, InputFormat format)
    {
        using var source = new Mat(24, 32, MatType.CV_8UC3, new Scalar(20, 90, 210));
        var data = Encode(extension, source);
        Assert.Equal(format, InputFormatDetector.Detect(data));
        using var image = ImageReader.Decode(data, 200);
        Assert.Equal(format, image.Format); Assert.Equal(200, image.Dpi);
        Assert.Equal(source.Size(), image.Pixels.Size()); Assert.Equal(MatType.CV_8UC3, image.Pixels.Type());
        var tolerance = format == InputFormat.Jpeg ? 3 : 0;
        Assert.InRange(Cv2.Norm(source, image.Pixels, NormTypes.INF), 0, tolerance);
        Assert.Equal(25.4, Units.PixelsToMm(200, image.Dpi), 10);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".bmp")]
    [InlineData(".tiff")]
    public void GrayscaleBecomesThreeEqualChannels(string extension)
    {
        using var gray = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(123));
        using var image = ImageReader.Decode(Encode(extension, gray));
        Assert.Equal(MatType.CV_8UC3, image.Pixels.Type());
        Assert.Equal(new Vec3b(123, 123, 123), image.Pixels.At<Vec3b>(8, 8));
        Assert.Equal(300, image.Dpi);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void PngAlphaCompositesOnWhiteIncludingTransparentHiddenColors(int depth)
    {
        byte[] encoded;
        if (depth == 8)
        {
            byte[] pixels = [50, 100, 200, 0, 50, 100, 200, 128, 50, 100, 200, 255];
            using var source = Mat.FromPixelData(1, 3, MatType.CV_8UC4, pixels);
            encoded = Encode(".png", source);
        }
        else
        {
            ushort[] pixels = [12850, 25700, 51400, 0, 12850, 25700, 51400, 32768, 12850, 25700, 51400, 65535];
            using var source = Mat.FromPixelData(1, 3, MatType.CV_16UC4, pixels);
            encoded = Encode(".png", source);
        }
        using var image = ImageReader.Decode(encoded);
        AssertAlphaPixels(image.Pixels);
    }

    [Theory]
    [InlineData(".png", 1)]
    [InlineData(".png", 3)]
    [InlineData(".tiff", 1)]
    [InlineData(".tiff", 3)]
    public void SixteenBitImagesRetainTheirRangeWhenConvertedToEightBit(string extension, int channels)
    {
        ushort[] values = channels == 1 ? [0, 32768, 65535] : [0, 32768, 65535, 32768, 65535, 0, 65535, 0, 32768];
        using var source = Mat.FromPixelData(1, 3, MatType.CV_16UC(channels), values);
        using var image = ImageReader.Decode(Encode(extension, source));
        Assert.Equal(MatType.CV_8UC3, image.Pixels.Type());
        if (channels == 1)
        {
            Assert.Equal(new Vec3b(0, 0, 0), image.Pixels.At<Vec3b>(0, 0));
            Assert.Equal(new Vec3b(128, 128, 128), image.Pixels.At<Vec3b>(0, 1));
            Assert.Equal(new Vec3b(255, 255, 255), image.Pixels.At<Vec3b>(0, 2));
        }
        else
        {
            Assert.Equal(new Vec3b(0, 128, 255), image.Pixels.At<Vec3b>(0, 0));
            Assert.Equal(new Vec3b(128, 255, 0), image.Pixels.At<Vec3b>(0, 1));
            Assert.Equal(new Vec3b(255, 0, 128), image.Pixels.At<Vec3b>(0, 2));
        }
    }

    public static IEnumerable<object[]> TiffAlphaCases =>
        from little in new[] { true, false }
        from depth in new[] { 8, 16 }
        from associated in new[] { true, false }
        from big in new[] { true, false }
        select new object[] { little, depth, associated, big };

    [Theory]
    [MemberData(nameof(TiffAlphaCases))]
    public void TiffAlphaCompositesStraightAndAssociatedSamples(bool little, int depth, bool associated, bool big)
    {
        var scale = depth == 8 ? 1 : 257;
        var alpha = depth == 8 ? 128 : 32768;
        ushort[] rgba =
        [
            0, 0, 0, 0,
            (ushort)((associated ? 100 : 200) * scale), (ushort)((associated ? 50 : 100) * scale),
            (ushort)((associated ? 25 : 50) * scale), (ushort)alpha,
            (ushort)(200 * scale), (ushort)(100 * scale), (ushort)(50 * scale), (ushort)(255 * scale)
        ];
        var data = TiffFixture.Create(little, depth, 4, [rgba], associated ? (ushort)1 : (ushort)2, big);
        using var image = ImageReader.Decode(data);
        Assert.Equal(InputFormat.Tiff, image.Format);
        AssertAlphaPixels(image.Pixels);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void TiffLoadsOnlyFirstPage(bool little, bool big)
    {
        var data = TiffFixture.Create(little, 8, 1, [[10, 20], [210, 220]], bigTiff: big);
        using var image = ImageReader.Decode(data);
        Assert.Equal(new Size(2, 1), image.Pixels.Size());
        Assert.Equal(new Vec3b(10, 10, 10), image.Pixels.At<Vec3b>(0, 0));
        Assert.Equal(new Vec3b(20, 20, 20), image.Pixels.At<Vec3b>(0, 1));
    }

    [Theory]
    [InlineData(".png", InputFormat.Png)]
    [InlineData(".jpg", InputFormat.Jpeg)]
    [InlineData(".bmp", InputFormat.Bmp)]
    [InlineData(".tiff", InputFormat.Tiff)]
    public void JapanesePathsAndMisleadingExtensionsUseFileContents(string extension, InputFormat format)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"reportdiff-画像 試験-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "帳票 新版.pdf");
        try
        {
            using var source = new Mat(8, 9, MatType.CV_8UC3, new Scalar(30, 100, 220));
            File.WriteAllBytes(path, Encode(extension, source));
            using var image = ImageReader.Read(path);
            Assert.Equal(format, image.Format);
            File.Delete(path); // 読み終えたファイルのハンドルを保持しない。
            Assert.Equal(source.Size(), image.Pixels.Size());
            Assert.InRange(Cv2.Norm(source, image.Pixels, NormTypes.INF), 0, format == InputFormat.Jpeg ? 3 : 0);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("89504E470D0A1A0A", InputFormat.Png)]
    [InlineData("FFD8FFE0", InputFormat.Jpeg)]
    [InlineData("424D", InputFormat.Bmp)]
    [InlineData("49492A00", InputFormat.Tiff)]
    [InlineData("4D4D002A", InputFormat.Tiff)]
    [InlineData("255044462D312E37", InputFormat.Pdf)]
    [InlineData("", InputFormat.Unknown)]
    [InlineData("89504E47", InputFormat.Unknown)]
    [InlineData("FFD8", InputFormat.Unknown)]
    [InlineData("474946383961", InputFormat.Unknown)]
    [InlineData("3C68746D6C3E", InputFormat.Unknown)]
    public void SignaturesDoNotDependOnExtensions(string hex, InputFormat expected) =>
        Assert.Equal(expected, InputFormatDetector.Detect(Convert.FromHexString(hex)));

    [Theory]
    [InlineData("")]
    [InlineData("89504E470D0A1A0A")]
    [InlineData("FFD8FF")]
    [InlineData("424D000000")]
    [InlineData("49492A0000000000")]
    [InlineData("255044462D312E37")]
    [InlineData("474946383961")]
    public void InvalidOrUnsupportedDataHasJapaneseError(string hex)
    {
        var error = Assert.Throws<ImageReadException>(() => ImageReader.Decode(Convert.FromHexString(hex)));
        Assert.Contains("画像", error.Message);
    }

    [Fact]
    public void MissingFileAndInvalidDpiHaveJapaneseErrors()
    {
        var path = Path.Combine(Path.GetTempPath(), $"存在しない帳票-{Guid.NewGuid():N}.png");
        Assert.Contains("画像ファイル", Assert.Throws<ImageReadException>(() => ImageReader.Read(path)).Message);
        using var source = new Mat(2, 2, MatType.CV_8UC3, Scalar.All(0));
        var data = Encode(".png", source);
        Assert.Throws<ImageReadException>(() => ImageReader.Decode(data, 71));
        Assert.Throws<ImageReadException>(() => ImageReader.Decode(data, 1201));
    }

    [Fact]
    public void FloatingPointTiffIsRejectedWithoutGuessingItsIntensityRange()
    {
        using var source = new Mat(2, 2, MatType.CV_32FC1, Scalar.All(0.5));
        Assert.Contains("画素形式", Assert.Throws<ImageReadException>(() => ImageReader.Decode(Encode(".tiff", source))).Message);
    }

    private static byte[] Encode(string extension, Mat image)
    {
        Assert.True(Cv2.ImEncode(extension, image, out var bytes));
        return bytes;
    }

    private static void AssertAlphaPixels(Mat pixels)
    {
        Assert.Equal(MatType.CV_8UC3, pixels.Type());
        Assert.Equal(new Vec3b(255, 255, 255), pixels.At<Vec3b>(0, 0));
        Assert.Equal(new Vec3b(152, 177, 227), pixels.At<Vec3b>(0, 1));
        Assert.Equal(new Vec3b(50, 100, 200), pixels.At<Vec3b>(0, 2));
    }
}
