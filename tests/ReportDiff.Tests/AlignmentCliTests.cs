using System.Runtime.Versioning;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class AlignmentCliTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task PdfCorrectionPreservesTextChangesAndSavesOriginalEvenWhenSame(bool changed, bool reverse)
    {
        using var files = new TextFile(CreatePage(false, false));
        var b = Path.Combine(files.DirectoryPath, "新版 日本語.pdf"); File.WriteAllBytes(b, CreatePage(changed, true));
        var output = Path.Combine(files.DirectoryPath, "結果");
        var config = WriteConfig(files.DirectoryPath);
        var run = await CliProcess.Run("compare", reverse ? b : files.Path, reverse ? files.Path : b,
            "--config", config, "--out", output);
        Assert.Equal(changed ? 1 : 0, run.Code); Assert.Empty(run.Error);
        var report = Read(output); var page = Assert.Single(report.Pages);
        Assert.Equal("applied", page.Alignment.Status);
        Assert.Equal(new PixelShift(reverse ? 6 : -6, reverse ? -4 : 4), page.GlobalShiftPx);
        Assert.Equal("GLOBAL_SHIFT_APPLIED", Assert.Single(report.Warnings).Code);
        Assert.True(report.Config.Align.Enabled);
        Assert.NotNull(page.Images.BOriginal);
        foreach (var path in new[] { page.Images.A, page.Images.B, page.Images.Overlay, page.Images.BOriginal })
            Assert.True(File.Exists(Path.Combine(output, path!)));
        using var reader = PdfReader.Open(reverse ? files.Path : b); using var image = reader.ReadPage(1, 144);
        using var saved = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(output, page.Images.BOriginal!)), ImreadModes.Color);
        Assert.Equal(0, Cv2.Norm(image.Pixels, saved, NormTypes.INF));
        if (changed)
        {
            var cluster = Assert.Single(page.Clusters);
            Assert.Equal(reverse ? "11128" : "11123", cluster.TextA);
            Assert.Equal(reverse ? "11123" : "11128", cluster.TextB);
        }
        else Assert.Empty(page.Clusters);
        var html = File.ReadAllText(Path.Combine(output, "report.html"));
        Assert.Contains("B · 補正前", html); Assert.Contains("B · 補正後", html);
        Assert.Contains("全体補正あり", html); Assert.Contains("B→A", html);
        Assert.Contains("補正後に残った A→B", html);
    }

    [Fact]
    public async Task MixedPdfImageAndNoHtmlStillReportCorrection()
    {
        using var files = new TextFile(CreatePage(false, false));
        var b = Path.Combine(files.DirectoryPath, "新版.pdf"); File.WriteAllBytes(b, CreatePage(true, true));
        var png = Path.Combine(files.DirectoryPath, "旧.png");
        using (var reader = PdfReader.Open(files.Path))
        using (var image = reader.ReadPage(1, 144)) File.WriteAllBytes(png, image.Pixels.ImEncode(".png"));
        var output = Path.Combine(files.DirectoryPath, "結果");
        var run = await CliProcess.Run("compare", png, b, "--out", output, "--config", WriteConfig(files.DirectoryPath), "--no-html", "--quiet");
        Assert.Equal(1, run.Code); Assert.Empty(run.Output); Assert.Empty(run.Error);
        var report = Read(output); var page = Assert.Single(report.Pages); var cluster = Assert.Single(page.Clusters);
        Assert.Equal(new PixelShift(-6, 4), page.GlobalShiftPx);
        Assert.Null(cluster.TextA); Assert.Equal("11128", cluster.TextB);
        Assert.Contains(report.Warnings, w => w.Code == "MIXED_INPUT_TYPES");
        Assert.Contains(report.Warnings, w => w.Code == "GLOBAL_SHIFT_APPLIED");
        Assert.False(File.Exists(Path.Combine(output, "report.html")));
    }

    [Fact]
    public async Task DefaultDisabledKeepsOriginalComparisonAndZeroRangeIsReported()
    {
        using var files = new TextFile(CreatePage(false, false));
        var b = Path.Combine(files.DirectoryPath, "新.pdf"); File.WriteAllBytes(b, CreatePage(false, true));
        using var pdfA = PdfReader.Open(files.Path); using var pdfB = PdfReader.Open(b);
        using var aImage = pdfA.ReadPage(1, 144); using var bImage = pdfB.ReadPage(1, 144);
        using var reference = PageComparer.Compare(aImage.Pixels, bImage.Pixels, new() { Dpi = 144 });
        foreach (var enabled in new[] { false, true })
        {
            var config = WriteConfig(files.DirectoryPath, enabled ? "align: {enabled: true, max_shift_mm: 0}" : "");
            var output = Path.Combine(files.DirectoryPath, enabled ? "距離ゼロ" : "無効");
            var run = await CliProcess.Run("compare", files.Path, b, "--out", output, "--config", config);
            Assert.Equal(1, run.Code); Assert.Empty(run.Error);
            var report = Read(output); var page = Assert.Single(report.Pages);
            Assert.Equal(enabled ? "zero_range" : "disabled", page.Alignment.Reason);
            Assert.Null(page.GlobalShiftPx); Assert.Null(page.Images.BOriginal); Assert.Empty(report.Warnings);
            Assert.Equal(reference.Status, page.Status); Assert.Equal(reference.RawPixels, page.RawPixels);
            Assert.Equal(reference.Clusters.Select(c => (c.Bounds.X, c.Bounds.Y, c.Bounds.Width, c.Bounds.Height, c.Pixels, c.Kind)),
                page.Clusters.Select(c => (c.BboxPx.X, c.BboxPx.Y, c.BboxPx.W, c.BboxPx.H, c.Pixels, c.Kind)));
        }
    }

    [Fact]
    public async Task SizeMismatchAndUnpairedPagesKeepExistingBehavior()
    {
        using var files = new TextFile(PdfFixture.CreateComparisonReport(false));
        var b = Path.Combine(files.DirectoryPath, "新.pdf"); File.WriteAllBytes(b, CreatePage(false, true));
        var output = Path.Combine(files.DirectoryPath, "結果");
        var run = await CliProcess.Run("compare", files.Path, b, "--out", output, "--config", WriteConfig(files.DirectoryPath));
        Assert.Equal(1, run.Code); Assert.Empty(run.Error);
        var report = Read(output); Assert.Equal(2, report.Pages.Count);
        Assert.Equal("size_mismatch", report.Pages[0].Alignment.Reason); Assert.True(report.Pages[0].SizeMismatch);
        Assert.Equal("unpaired_page", report.Pages[1].Alignment.Reason); Assert.Equal("only_in_a", report.Pages[1].Status);
        Assert.All(report.Pages, page => { Assert.Null(page.GlobalShiftPx); Assert.Null(page.Images.BOriginal); });
        Assert.DoesNotContain(report.Warnings, w => w.Code == "GLOBAL_SHIFT_APPLIED");
    }

    [Fact]
    public void ShiftedPdfWordUsesCorrectedExclusionAndClusterCoordinates()
    {
        using var files = new TextFile(PdfFixture.CreateTextPage([new("日本語", 100, 200)]));
        using var text = new PdfTextReader(files.Path);
        // 72dpi の元座標 (100,91.6) を左 50・上 40px 移した矩形。
        DifferenceCluster[] clusters = [new(1, new(50, 51, 22, 10), 1), new(2, new(100, 91, 22, 10), 1)];
        var shifted = text.Annotate(1, new(240, 300), 72, clusters, [], new(-50, -40));
        Assert.Empty(shifted.Warnings); Assert.Equal("日本語", shifted.TextByCluster[1]); Assert.Equal("", shifted.TextByCluster[2]);
        var excluded = text.Annotate(1, new(240, 300), 72, clusters,
            [new(Units.PixelsToMm(55, 72), Units.PixelsToMm(55, 72), 1, 1)], new(-50, -40));
        Assert.Equal("", excluded.TextByCluster[1]);
    }

    internal static byte[] CreatePage(bool change, bool shifted)
    {
        var runs = new List<TextRun>();
        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 3; col++)
            runs.Add(new($"{row}{col}{(change && row == 1 && col == 1 ? "128" : "123")}",
                20 + col * 75 + (shifted ? 3 : 0), 260 - row * 100 + (shifted ? 2 : 0)));
        return PdfFixture.CreateTextPage(runs);
    }
    private static string WriteConfig(string directory, string align = "align: {enabled: true}")
    {
        var path = Path.Combine(directory, "設定.yaml"); File.WriteAllText(path, "# 全体補正の確認\ndpi: 144\n" + align + "\n"); return path;
    }
    private static ReportDocument Read(string output) => JsonSerializer.Deserialize<ReportDocument>(
        File.ReadAllText(Path.Combine(output, "result.json")), ReportJson.Options)!;
}
