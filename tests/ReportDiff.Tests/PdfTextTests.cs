using System.Text;
using System.Runtime.Versioning;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class PdfTextTests
{
    [Theory]
    [InlineData(72)] [InlineData(300)] [InlineData(400)]
    public void EmbeddedUnicodeTextMapsToCroppedOriginalPage(int dpi)
    {
        using var file = new TextFile(PdfFixture.CreateTextPage([new("旧値 123 日本😀", 50, 220)], crop: "30 40 210 260"));
        using var pdf = PdfReader.Open(file.Path);
        using var image = pdf.ReadPage(1, dpi);
        using var text = new PdfTextReader(file.Path);
        var box = new Rect(Units.RoundPixels(19 * 25.4 / 72, dpi), Units.RoundPixels(30 * 25.4 / 72, dpi),
            Units.RoundPixels(120 * 25.4 / 72, dpi), Units.RoundPixels(12 * 25.4 / 72, dpi));
        var result = text.Annotate(1, image.Pixels.Size(), dpi, [new(1, box, 1)], []);
        Assert.Empty(result.Warnings);
        Assert.Equal("旧値 123 日本😀", result.TextByCluster[1]);
        using var crop = new Mat(image.Pixels, box); using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.MinMaxLoc(gray, out double minimum, out _);
        Assert.True(minimum < 128);
    }

    [Theory]
    [InlineData("20 30 240 300", "30 40 210 260", 20)]
    [InlineData("20 30 240 300", "0 0 210 260", 30)]
    [InlineData("-20 -30 240 300", "-10 -10 210 260", 60)]
    public void CropAndMediaOriginsAreAppliedOnce(string media, string crop, int left)
    {
        using var file = new TextFile(PdfFixture.CreateTextPage([new("ABC", 50, 220)], media, crop));
        using var pdf = PdfReader.Open(file.Path); using var image = pdf.ReadPage(1, 72);
        using var reader = new PdfTextReader(file.Path);
        var result = reader.Annotate(1, image.Pixels.Size(), 72, [new(1, new(left, 30, 22, 12), 1)], []);
        Assert.Empty(result.Warnings); Assert.Equal("ABC", result.TextByCluster[1]);
    }

    [Theory]
    [InlineData(90, 1)] [InlineData(180, 1)] [InlineData(270, 1)] [InlineData(0, 2)]
    public void UnsupportedCoordinatesAreSkipped(int rotation, int unit)
    {
        using var file = new TextFile(PdfFixture.CreateTextPage([new("ABC", 50, 220)], rotation: rotation, userUnit: unit));
        using var pdf = PdfReader.Open(file.Path); using var image = pdf.ReadPage(1);
        using var reader = new PdfTextReader(file.Path);
        var result = reader.Annotate(1, image.Pixels.Size(), 300, [new(1, new(0, 0, 100, 100), 1)], []);
        Assert.Empty(result.TextByCluster); Assert.Equal("TEXT_ANNOTATION_SKIPPED", Assert.Single(result.Warnings).Code);
    }

    [Fact]
    public void NoTextAndCoordinatesOutsideCropAreEmpty()
    {
        using var file = new TextFile(PdfFixture.CreateTextPage([new("OUTSIDE", 5, 5)], crop: "30 40 210 260"));
        using var pdf = PdfReader.Open(file.Path); using var image = pdf.ReadPage(1);
        using var reader = new PdfTextReader(file.Path);
        var result = reader.Annotate(1, image.Pixels.Size(), 300, [new(1, new(0, 0, image.Pixels.Width, image.Pixels.Height), 1)], []);
        Assert.Empty(result.Warnings); Assert.Equal("", result.TextByCluster[1]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PageSizeLimitsSkipRatherThanReturnPartialText(bool words)
    {
        var runs = words
            ? Enumerable.Range(0, new TextOptions().MaxWordsPerPage + 1).Select(i => new TextRun("A", i % 100 * 2, 290 - i / 100, 1)).ToArray()
            : new[] { new TextRun(new string('A', new TextOptions().MaxLettersPerPage + 1), 0, 0, 1) };
        using var file = new TextFile(PdfFixture.CreateTextPage(runs));
        using var reader = new PdfTextReader(file.Path);
        var result = reader.Annotate(1, new(240, 300), 72, [new(1, new(0, 0, 240, 300), 1)], []);
        Assert.Empty(result.TextByCluster);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("TEXT_ANNOTATION_SKIPPED", warning.Code);
        Assert.Contains(words ? "単語" : "文字要素", warning.Message);
    }

    [Fact]
    public void LongExtractedWordIsTruncatedAndDisposedReaderCannotBeReused()
    {
        using var file = new TextFile(PdfFixture.CreateTextPage([new(new string('A', 2001), 0, 100, 0.1)]));
        using var reader = new PdfTextReader(file.Path);
        var result = reader.Annotate(1, new(240, 300), 72, [new(1, new(0, 0, 240, 300), 1)], []);
        Assert.Equal(new string('A', 1999) + "…", result.TextByCluster[1]);
        Assert.Equal("TEXT_ANNOTATION_TRUNCATED", Assert.Single(result.Warnings).Code);
        reader.Dispose(); reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.Annotate(1, new(240, 300), 72, [], []));
    }

    [Fact]
    public void ReadingOrderComesFromGeometryRatherThanContentOrder()
    {
        using var file = new TextFile(PdfFixture.CreateTextPage([new("下段", 40, 100), new("右", 100, 200), new("左", 40, 200)]));
        using var reader = new PdfTextReader(file.Path);
        var result = reader.Annotate(1, new(240, 300), 72, [new(1, new(0, 0, 240, 300), 1)], []);
        Assert.Empty(result.Warnings); Assert.Equal("左 右\n下段", result.TextByCluster[1]);
    }

    [Fact]
    public void InheritedPageRotationIsAlsoSkipped()
    {
        using var file = new TextFile(PdfFixture.CreateTextPage([new("123", 50, 220)], rotation: 90, inheritedRotation: true));
        using var reader = new PdfTextReader(file.Path);
        var result = reader.Annotate(1, new(300, 240), 72, [new(1, new(0, 0, 300, 240), 1)], []);
        Assert.Empty(result.TextByCluster); Assert.Contains("90 度", Assert.Single(result.Warnings).Message);
    }

    [Fact]
    public void InvalidSizeAndBadPageAreWarnings()
    {
        using var file = new TextFile(PdfFixture.CreateTextPage([new("ABC", 50, 220)]));
        using var reader = new PdfTextReader(file.Path);
        DifferenceCluster[] clusters = [new(1, new(0, 0, 10, 10), 1)];
        Assert.Equal("TEXT_ANNOTATION_SKIPPED", Assert.Single(reader.Annotate(1, new(10, 10), 300, clusters, []).Warnings).Code);
        Assert.Equal("TEXT_EXTRACTION_FAILED", Assert.Single(reader.Annotate(2, new(10, 10), 300, clusters, []).Warnings).Code);
    }

    [Fact]
    public void OpenFailureAndNoClustersDoNotThrowOrExposeInput()
    {
        using var reader = new PdfTextReader("秘匿する存在しないファイル");
        Assert.Empty(reader.Annotate(1, new(100, 100), 72, [], []).Warnings);
        foreach (var page in new[] { 1, 2 })
        {
            var result = reader.Annotate(page, new(100, 100), 72, [new(1, new(0, 0, 10, 10), 1)], []);
            Assert.Empty(result.TextByCluster);
            Assert.DoesNotContain("秘匿", Assert.Single(result.Warnings).Message);
        }
    }

    [Fact]
    public void WordOverlapExclusionDeduplicationAndOrder()
    {
        TextWord[] words = [new("下", new(0, 20, 10, 10)), new("右", new(20, 1, 10, 10)),
            new("左", new(0, 0, 10, 10)), new("左", new(0, 0, 10, 10)),
            new("境界", new(40, 0, 10, 10)), new("除外", new(31, 0, 8, 10))];
        var result = TextAnnotations.Create(words, [new(1, new(0, 0, 40, 35), 1), new(2, new(9, 0, 1, 10), 1)],
            [new(Units.PixelsToMm(35, 300), 0, 1, 1)], 300);
        Assert.Equal("左 右\n下", result.TextByCluster[1]);
        Assert.Equal("左", result.TextByCluster[2]); Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData(1999, false)] [InlineData(2000, false)] [InlineData(2001, true)]
    public void UnicodeLimitIsExactAndDoesNotSplitSurrogatePairs(int count, bool truncated)
    {
        var content = string.Concat(Enumerable.Repeat("😀", count));
        var result = TextAnnotations.Create([new(content, new(0, 0, 10, 10))], [new(1, new(0, 0, 10, 10), 1)], [], 300);
        var text = result.TextByCluster[1];
        Assert.Equal(Math.Min(count, 2000), text.EnumerateRunes().Count());
        Assert.DoesNotContain("�", text);
        Assert.Equal(truncated, text.EndsWith('…')); Assert.Equal(truncated ? 1 : 0, result.Warnings.Count);
    }

    [Fact]
    public void WhitespaceIsNormalizedWithoutChangingFullwidthOrSymbols()
    {
        var result = TextAnnotations.Create([new(" １２３\t<&>\n項目\0 ", new(0, 0, 10, 10))], [new(1, new(0, 0, 10, 10), 1)], [], 300);
        Assert.Equal("１２３ <&> 項目", result.TextByCluster[1]);
    }
}

internal sealed class TextFile : IDisposable
{
    public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reportdiff-text-" + Guid.NewGuid());
    public string Path => System.IO.Path.Combine(DirectoryPath, "日本語 文字.pdf");
    public TextFile(byte[] bytes) { Directory.CreateDirectory(DirectoryPath); File.WriteAllBytes(Path, bytes); }
    public void Dispose() => Directory.Delete(DirectoryPath, true);
}
