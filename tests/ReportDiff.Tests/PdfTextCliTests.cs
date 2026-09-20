using System.Runtime.Versioning;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class PdfTextCliTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TextChangeProducesWholeWordsWithoutChangingDetection(bool reverse)
    {
        using var files = new TextFile(PdfFixture.CreateTextPage([new("Total 123", 50, 220)]));
        var newPath = Path.Combine(files.DirectoryPath, "新 日本語.pdf");
        File.WriteAllBytes(newPath, PdfFixture.CreateTextPage([new("Total 128", 50, 220)]));
        var a = reverse ? newPath : files.Path; var b = reverse ? files.Path : newPath;
        var output = Path.Combine(files.DirectoryPath, "結果");
        var process = await CliProcess.Run("compare", a, b, "--out", output);
        Assert.Equal(1, process.Code); Assert.Empty(process.Error);
        var report = Read(output); Assert.Empty(report.Warnings);
        var cluster = Assert.Single(Assert.Single(report.Pages).Clusters);
        Assert.Equal(reverse ? "128" : "123", cluster.TextA);
        Assert.Equal(reverse ? "123" : "128", cluster.TextB);
        using var readerA = PdfReader.Open(a); using var readerB = PdfReader.Open(b);
        using var imageA = readerA.ReadPage(1); using var imageB = readerB.ReadPage(1);
        using var core = PageComparer.Compare(imageA.Pixels, imageB.Pixels, new());
        AssertDetection(core, report.Pages[0]);
        var html = File.ReadAllText(Path.Combine(output, "report.html"));
        Assert.Contains(">123</div>", html);
        Assert.Contains(">128</div>", html);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MixedInputAnnotatesOnlyPdfAndNoHtmlStillWritesText(bool reverse)
    {
        using var files = new TextFile(PdfFixture.CreateTextPage([new("123", 50, 220)]));
        using var reader = PdfReader.Open(files.Path); using var image = reader.ReadPage(1);
        var png = Path.Combine(files.DirectoryPath, "旧.png");
        File.WriteAllBytes(png, image.Pixels.ImEncode(".png"));
        var pdf = Path.Combine(files.DirectoryPath, "新.pdf");
        File.WriteAllBytes(pdf, PdfFixture.CreateTextPage([new("128", 50, 220)]));
        var output = Path.Combine(files.DirectoryPath, "結果");
        var result = await CliProcess.Run("compare", reverse ? pdf : png, reverse ? png : pdf, "--out", output, "--no-html");
        Assert.Equal(1, result.Code); Assert.Empty(result.Error);
        var report = Read(output); var cluster = Assert.Single(report.Pages[0].Clusters);
        Assert.Equal("MIXED_INPUT_TYPES", Assert.Single(report.Warnings).Code);
        Assert.Equal("128", reverse ? cluster.TextA : cluster.TextB);
        Assert.Null(reverse ? cluster.TextB : cluster.TextA);
        Assert.False(File.Exists(Path.Combine(output, "report.html")));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedExtractionOrRotationKeepsImageResults(bool rotation)
    {
        using var files = new TextFile(PdfFixture.CreateTextPage([new("123", 50, 220)]));
        var b = Path.Combine(files.DirectoryPath, "新.pdf");
        File.WriteAllBytes(b, PdfFixture.CreateTextPage([new("128", 50, 220)], rotation: rotation ? 180 : 0, brokenText: !rotation));
        var output = Path.Combine(files.DirectoryPath, "結果");
        var result = await CliProcess.Run("compare", files.Path, b, "--out", output);
        Assert.Equal(1, result.Code); Assert.Empty(result.Error);
        var report = Read(output);
        Assert.Contains(report.Warnings, w => w.Code == (rotation ? "TEXT_ANNOTATION_SKIPPED" : "TEXT_EXTRACTION_FAILED") && w.Message.StartsWith("B・1 ページ:"));
        Assert.All(report.Pages[0].Clusters, c => Assert.Null(c.TextB));
        using var readerA = PdfReader.Open(files.Path); using var readerB = PdfReader.Open(b);
        using var imageA = readerA.ReadPage(1); using var imageB = readerB.ReadPage(1);
        using var core = PageComparer.Compare(imageA.Pixels, imageB.Pixels, new());
        AssertDetection(core, report.Pages[0]);
    }

    [Fact]
    public async Task DifferentPageSizesDoNotStretchTextIntoPadding()
    {
        using var files = new TextFile(PdfFixture.CreateTextPage([new("123", 50, 120)], media: "0 0 120 200"));
        var b = Path.Combine(files.DirectoryPath, "新.pdf");
        File.WriteAllBytes(b, PdfFixture.CreateTextPage([new("128", 50, 220), new("EXTRA", 150, 120)]));
        var output = Path.Combine(files.DirectoryPath, "結果");
        var result = await CliProcess.Run("compare", files.Path, b, "--out", output);
        Assert.Equal(1, result.Code);
        var report = Read(output); Assert.True(report.Pages[0].SizeMismatch);
        Assert.Contains(report.Pages[0].Clusters, c => c.TextA == "123" && c.TextB == "128");
        Assert.Contains(report.Pages[0].Clusters, c => c.TextA == "" && c.TextB == "EXTRA");
    }

    [Fact]
    public async Task ExcludedPartOfAWordOmitsWholeWordButKeepsDetectedChanges()
    {
        using var files = new TextFile(PdfFixture.CreateTextPage([new("123", 50, 220)]));
        var b = Path.Combine(files.DirectoryPath, "新.pdf");
        File.WriteAllBytes(b, PdfFixture.CreateTextPage([new("828", 50, 220)]));
        var config = Path.Combine(files.DirectoryPath, "設定.yaml");
        // 中央の 2 の中だけを除外。両端の変更は検出対象のまま。
        File.WriteAllText(config, "exclude:\n  - { page: all, x: 20.5, y: 25.5, w: 0.2, h: 1 }\n");
        var output = Path.Combine(files.DirectoryPath, "結果");
        var result = await CliProcess.Run("compare", files.Path, b, "--out", output, "--config", config);
        Assert.Equal(1, result.Code); Assert.Empty(result.Error);
        var report = Read(output); Assert.Empty(report.Warnings); Assert.NotEmpty(report.Pages[0].Clusters);
        Assert.All(report.Pages[0].Clusters, c => { Assert.Equal("", c.TextA); Assert.Equal("", c.TextB); });
    }

    private static ReportDocument Read(string output) => JsonSerializer.Deserialize<ReportDocument>(File.ReadAllText(Path.Combine(output, "result.json")), ReportJson.Options)!;
    private static void AssertDetection(PageComparison core, ReportPage page)
    {
        Assert.Equal(core.Status, page.Status); Assert.Equal(core.RawPixels, page.RawPixels);
        Assert.Equal(core.NoiseDropped, page.NoiseDropped); Assert.Equal(core.AbsorbedGroups, page.AbsorbedGroups);
        Assert.Equal(core.MaxShiftPx, page.MaxShiftPx);
        Assert.Equal(core.Clusters.Select(c => (c.Id, c.Bounds.X, c.Bounds.Y, c.Bounds.Width, c.Bounds.Height, c.Pixels, c.Kind, c.ShiftPx)),
            page.Clusters.Select(c => (c.Id, c.BboxPx.X, c.BboxPx.Y, c.BboxPx.W, c.BboxPx.H, c.Pixels, c.Kind,
                c.ShiftPx is {} s ? new MovementShift(s.Dx, s.Dy) : null)));
        Assert.Equal(core.Clusters.Select(c => string.Join(',', c.RelatedClusterIds)), page.Clusters.Select(c => string.Join(',', c.RelatedClusterIds)));
    }
}
