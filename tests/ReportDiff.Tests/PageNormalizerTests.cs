using System.Runtime.Versioning;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using Xunit;

namespace ReportDiff.Tests;

public sealed class PageNormalizerTests
{
    [Theory]
    [InlineData(4, 3, 7, 5)]
    [InlineData(7, 5, 4, 3)]
    [InlineData(6, 2, 3, 7)]
    [InlineData(4, 5, 7, 5)]
    [InlineData(7, 3, 7, 5)]
    [InlineData(7, 5, 7, 5)]
    public void OnlyRightAndBottomArePaddedWithoutResizing(int widthA, int heightA, int widthB, int heightB)
    {
        using var a = Pattern(widthA, heightA);
        using var b = Pattern(widthB, heightB);
        using var originalA = a.Clone();
        using var originalB = b.Clone();
        using var pair = PageNormalizer.Normalize(a, b);
        var target = new Size(Math.Max(widthA, widthB), Math.Max(heightA, heightB));
        Assert.Equal(target, pair.A.Size()); Assert.Equal(target, pair.B.Size());
        Assert.Equal(a.Size(), pair.OriginalSizeA); Assert.Equal(b.Size(), pair.OriginalSizeB);
        Assert.Equal(a.Size() != b.Size(), pair.SizeMismatch);
        Assert.Equal(pair.SizeMismatch ? ["SIZE_MISMATCH"] : Array.Empty<string>(), pair.Warnings);
        AssertPixelsAndPadding(originalA, pair.A);
        AssertPixelsAndPadding(originalB, pair.B);
        Assert.Equal(0, Cv2.Norm(originalA, a, NormTypes.INF));
        Assert.Equal(0, Cv2.Norm(originalB, b, NormTypes.INF));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputAndInputsHaveIndependentOwnership(bool differentSize)
    {
        using var a = Pattern(4, 3);
        using var b = Pattern(differentSize ? 6 : 4, 3);
        using var pair = PageNormalizer.Normalize(a, b);
        var originalB = b.At<Vec3b>(0, 0);
        pair.B.Set(0, 0, new Vec3b(200, 201, 202));
        Assert.Equal(originalB, b.At<Vec3b>(0, 0));
        var expectedA = pair.A.At<Vec3b>(0, 0);
        a.SetTo(Scalar.All(0));
        a.Dispose(); b.Dispose();
        Assert.Equal(expectedA, pair.A.At<Vec3b>(0, 0));
        pair.Dispose(); pair.Dispose();
        Assert.True(pair.A.IsDisposed); Assert.True(pair.B.IsDisposed);
    }

    [Fact]
    public void SubmatrixPaddingDoesNotCopyPixelsOutsideTheInput()
    {
        using var parent = new Mat(8, 9, MatType.CV_8UC3, new Scalar(2, 3, 4));
        using var a = new Mat(parent, new Rect(2, 3, 3, 2));
        a.SetTo(new Scalar(30, 60, 90));
        using var b = Pattern(6, 5);
        using var pair = PageNormalizer.Normalize(a, b);
        AssertPixelsAndPadding(a, pair.A);
        AssertPixelsAndPadding(b, pair.B);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EmptyWrongTypeAndDisposedInputsAreRejected(int kind)
    {
        using var valid = Pattern(4, 3);
        using var invalid = kind switch
        {
            0 => new Mat(),
            1 => new Mat(2, 2, MatType.CV_8UC1),
            2 => new Mat(2, 2, MatType.CV_8UC4),
            3 => new Mat(2, 2, MatType.CV_16UC3),
            _ => Pattern(2, 2)
        };
        if (kind == 4) invalid.Dispose();
        Assert.Contains("BGR", Assert.Throws<ArgumentException>(() => PageNormalizer.Normalize(invalid, valid)).Message);
        Assert.Throws<ArgumentException>(() => PageNormalizer.Normalize(valid, invalid));
    }

    [Fact]
    public void WhitePaddingRemainsReportedWhenPixelComparisonIsSame()
    {
        using var a = new Mat(8, 10, MatType.CV_8UC3, Scalar.All(255));
        using var b = new Mat(12, 15, MatType.CV_8UC3, Scalar.All(255));
        Cv2.Rectangle(a, new Rect(2, 2, 3, 3), Scalar.All(0), -1);
        Cv2.Rectangle(b, new Rect(2, 2, 3, 3), Scalar.All(0), -1);
        Assert.True(Cv2.ImEncode(".png", a, out var bytesA));
        Assert.True(Cv2.ImEncode(".png", b, out var bytesB));
        using var imageA = ImageReader.Decode(bytesA, 200);
        using var imageB = ImageReader.Decode(bytesB, 200);
        var plan = PagePairing.Create(1, 1);
        Assert.True(Assert.Single(plan.Pages).CanCompare);
        using var pair = PageNormalizer.Normalize(imageA.Pixels, imageB.Pixels);
        using var compared = PageComparer.Compare(pair.A, pair.B, new() { Dpi = imageA.Dpi });
        Assert.Equal("same", compared.Status);
        Assert.True(pair.SizeMismatch);
        Assert.Equal(new[] { "SIZE_MISMATCH" }, pair.Warnings);
        Assert.Equal(200, imageA.Dpi); Assert.Equal(200, imageB.Dpi);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "2-3")]
    [InlineData(true, "2-3")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    public void PdfPagesFlowThroughPairingNormalizationAndComparison(bool reverse, string? selection)
    {
        using var longer = new PdfTestFile(PdfFixture.CreatePages((72, 72), (100, 80), (90, 100)));
        using var shorter = new PdfTestFile(PdfFixture.CreatePages((72, 72), (80, 100)));
        using var a = PdfReader.Open(reverse ? shorter.FilePath : longer.FilePath);
        using var b = PdfReader.Open(reverse ? longer.FilePath : shorter.FilePath);
        var plan = PagePairing.Create(a.PageCount, b.PageCount, selection);
        Assert.Equal(selection is null ? new[] { 1, 2, 3 } : [2, 3], plan.Pages.Select(p => p.PageNumber));
        Assert.Equal(new[] { "PAGE_COUNT_MISMATCH" }, plan.Warnings);
        foreach (var page in plan.Pages)
        {
            if (!page.CanCompare)
            {
                Assert.Equal(3, page.PageNumber);
                Assert.Equal(reverse ? "only_in_b" : "only_in_a", page.UnpairedStatus);
                using var unpaired = (page.HasA ? a : b).ReadPage(page.PageNumber, 72);
                Assert.Equal(new Size(90, 100), unpaired.Pixels.Size());
                continue;
            }
            using var imageA = a.ReadPage(page.PageNumber, 72);
            using var imageB = b.ReadPage(page.PageNumber, 72);
            using var pair = PageNormalizer.Normalize(imageA.Pixels, imageB.Pixels);
            Assert.Equal(page.PageNumber == 2, pair.SizeMismatch);
            Assert.Equal(page.PageNumber == 2 ? new Size(100, 100) : new Size(72, 72), pair.A.Size());
            using var compared = PageComparer.Compare(pair.A, pair.B, new() { Dpi = 72 });
            Assert.Equal("same", compared.Status);
        }
    }

    private static Mat Pattern(int width, int height)
    {
        var image = new Mat(height, width, MatType.CV_8UC3);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++) image.Set(y, x, new Vec3b((byte)(x * 20), (byte)(y * 30), (byte)(x + y + 80)));
        return image;
    }

    private static void AssertPixelsAndPadding(Mat original, Mat normalized)
    {
        Assert.Equal(MatType.CV_8UC3, normalized.Type());
        var originalSize = original.Size();
        var size = normalized.Size();
        for (var y = 0; y < size.Height; y++)
        for (var x = 0; x < size.Width; x++)
        {
            var expected = x < originalSize.Width && y < originalSize.Height ? original.At<Vec3b>(y, x) : new Vec3b(255, 255, 255);
            Assert.Equal(expected, normalized.At<Vec3b>(y, x));
        }
    }
}
