using System.Runtime.Versioning;
using System.Text.Json;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class PdfFontCliTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamePageStillWarnsOnBothInputsWithoutChangingExitCode(bool noHtml)
    {
        using var files = new TextFile(PdfFixture.CreateFontReport());
        var output = Path.Combine(files.DirectoryPath, "結果");
        var args = new List<string> { "compare", files.Path, files.Path, "--out", output };
        if (noHtml) args.Add("--no-html");
        var process = await CliProcess.Run(args.ToArray());
        Assert.Equal(0, process.Code); Assert.Empty(process.Error);
        var result = Read(output);
        Assert.Equal("same", result.Summary.Status); Assert.Empty(Assert.Single(result.Pages).Clusters);
        Assert.Equal(2, result.Warnings.Count);
        Assert.All(result.Warnings, w => Assert.Equal("NON_EMBEDDED_FONT", w.Code));
        Assert.StartsWith("A・1 ページ:", result.Warnings[0].Message);
        Assert.StartsWith("B・1 ページ:", result.Warnings[1].Message);
        Assert.Equal(!noHtml, File.Exists(Path.Combine(output, "report.html")));
        if (!noHtml) Assert.Contains("NON_EMBEDDED_FONT", File.ReadAllText(Path.Combine(output, "report.html")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferenceAndTooDifferentKeepCoreResultsAndWarnings(bool tooDifferent)
    {
        var text = PdfFixture.FontText.Replace("(A)", "(AAA)");
        using var files = new TextFile(PdfFixture.CreateFontReport(contents: [text]));
        var b = Path.Combine(files.DirectoryPath, "新 日本語.pdf");
        File.WriteAllBytes(b, PdfFixture.CreateFontReport(contents: [text + "0 0 0 rg 10 10 60 20 re f\n"]));
        var config = Path.Combine(files.DirectoryPath, "設定.yaml");
        File.WriteAllText(config, tooDifferent ? "cluster:\n  max_diff_ratio: 0.001\n" : "text:\n  max_letters_per_page: 1\n");
        var output = Path.Combine(files.DirectoryPath, "結果");
        var process = await CliProcess.Run("compare", files.Path, b, "--out", output, "--config", config);
        Assert.Equal(1, process.Code); Assert.Empty(process.Error);
        var result = Read(output); var page = Assert.Single(result.Pages);
        Assert.Equal(2, result.Warnings.Count(w => w.Code == "NON_EMBEDDED_FONT"));
        Assert.Equal(tooDifferent ? "too_different" : "different", page.Status);
        if (!tooDifferent) Assert.Equal(2, result.Warnings.Count(w => w.Code == "TEXT_ANNOTATION_SKIPPED"));
        using var readerA = PdfReader.Open(files.Path); using var readerB = PdfReader.Open(b);
        using var imageA = readerA.ReadPage(1); using var imageB = readerB.ReadPage(1);
        using var core = PageComparer.Compare(imageA.Pixels, imageB.Pixels,
            new() { Cluster = new() { MaxDiffRatio = tooDifferent ? 0.001 : 0.3 } });
        Assert.Equal(core.Status, page.Status); Assert.Equal(core.RawPixels, page.RawPixels);
        Assert.Equal(core.NoiseDropped, page.NoiseDropped); Assert.Equal(core.AbsorbedGroups, page.AbsorbedGroups);
        Assert.Equal(core.MaxShiftPx, page.MaxShiftPx);
        Assert.Equal(core.Clusters.Select(c => (c.Id, c.Bounds.X, c.Bounds.Y, c.Bounds.Width, c.Bounds.Height, c.Pixels, c.Kind)),
            page.Clusters.Select(c => (c.Id, c.BboxPx.X, c.BboxPx.Y, c.BboxPx.W, c.BboxPx.H, c.Pixels, c.Kind)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedUnpairedPagesWarnOnlyForExistingInput(bool reverse)
    {
        using var files = new TextFile(PdfFixture.CreateFontReport());
        var longer = Path.Combine(files.DirectoryPath, "複数.pdf");
        File.WriteAllBytes(longer, PdfFixture.CreateFontReport(contents: [PdfFixture.FontText, PdfFixture.FontText]));
        var output = Path.Combine(files.DirectoryPath, "結果");
        var process = await CliProcess.Run("compare", reverse ? longer : files.Path, reverse ? files.Path : longer, "--out", output, "--pages", "2");
        Assert.Equal(1, process.Code); Assert.Empty(process.Error);
        var result = Read(output);
        Assert.Equal(reverse ? "only_in_a" : "only_in_b", Assert.Single(result.Pages).Status);
        var warning = Assert.Single(result.Warnings, w => w.Code == "NON_EMBEDDED_FONT");
        Assert.StartsWith($"{(reverse ? "A" : "B")}・2 ページ:", warning.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedInputOnlyInspectsPdf(bool reverse)
    {
        using var files = new TextFile(PdfFixture.CreateFontReport());
        var png = Path.Combine(files.DirectoryPath, "元の画像.png");
        using (var reader = PdfReader.Open(files.Path))
        using (var image = reader.ReadPage(1)) File.WriteAllBytes(png, image.Pixels.ImEncode(".png"));
        var output = Path.Combine(files.DirectoryPath, "結果");
        var process = await CliProcess.Run("compare", reverse ? png : files.Path, reverse ? files.Path : png, "--out", output);
        Assert.Equal(0, process.Code);
        var warning = Assert.Single(Read(output).Warnings, w => w.Code == "NON_EMBEDDED_FONT");
        Assert.StartsWith($"{(reverse ? "B" : "A")}・1 ページ:", warning.Message);
    }

    [Fact]
    public async Task SelectedEmbeddedPageDoesNotWarnForUnselectedFontPage()
    {
        using var files = new TextFile(PdfFixture.CreateFontReport(font: PdfFixture.EmbeddedTrueType,
            contents: [PdfFixture.FontText, "BT /F2 12 Tf (A) Tj ET"]));
        var output = Path.Combine(files.DirectoryPath, "結果");
        var process = await CliProcess.Run("compare", files.Path, files.Path, "--out", output, "--pages", "1");
        Assert.Equal(0, process.Code); Assert.Empty(Read(output).Warnings);
    }

    [Fact]
    public async Task AppearanceLimitationIsReportedEvenWithNoBodyText()
    {
        using var files = new TextFile(PdfFixture.CreateFontReport(contents: [""], annotation: true));
        var output = Path.Combine(files.DirectoryPath, "結果");
        var process = await CliProcess.Run("compare", files.Path, files.Path, "--out", output);
        Assert.Equal(0, process.Code);
        var report = Read(output); Assert.Equal(2, report.Warnings.Count);
        Assert.All(report.Warnings, w => { Assert.Equal("FONT_INSPECTION_INCOMPLETE", w.Code); Assert.Contains("外観", w.Message); });
        Assert.Contains("FONT_INSPECTION_INCOMPLETE", File.ReadAllText(Path.Combine(output, "report.html")));
    }

    [Fact]
    public async Task ParsingFailureOnSamePdfIsAWarningAndNotAnError()
    {
        using var files = new TextFile(PdfFixture.CreateTextPage([new("A", 20, 40)], brokenText: true));
        var output = Path.Combine(files.DirectoryPath, "結果");
        var process = await CliProcess.Run("compare", files.Path, files.Path, "--out", output);
        Assert.Equal(0, process.Code); Assert.Empty(process.Error);
        var report = Read(output); Assert.Equal(2, report.Warnings.Count);
        Assert.All(report.Warnings, w => Assert.Equal("FONT_INSPECTION_INCOMPLETE", w.Code));
    }

    [Fact]
    public async Task SharedFontIsReportedOnEachSelectedPage()
    {
        using var files = new TextFile(PdfFixture.CreateFontReport(contents: [PdfFixture.FontText, PdfFixture.FontText]));
        var output = Path.Combine(files.DirectoryPath, "結果");
        var process = await CliProcess.Run("compare", files.Path, files.Path, "--out", output);
        Assert.Equal(0, process.Code);
        var warnings = Read(output).Warnings;
        Assert.Equal(4, warnings.Count);
        foreach (var side in new[] { "A", "B" })
            foreach (var page in new[] { 1, 2 })
                Assert.Single(warnings, w => w.Code == "NON_EMBEDDED_FONT" && w.Message.StartsWith($"{side}・{page} ページ:"));
    }

    [Fact]
    public async Task CyclicUnusedFormResourcesTerminateAndKeepConfirmedWarnings()
    {
        // 実プロセスのタイムアウトも使い、辞書を解決し続ける回帰を検出する。
        using var files = new TextFile(PdfFixture.CreateFontResourceCycle());
        var output = Path.Combine(files.DirectoryPath, "結果");
        var process = await CliProcess.Run("compare", files.Path, files.Path, "--out", output);
        Assert.Equal(0, process.Code); Assert.Empty(process.Error);
        var warnings = Read(output).Warnings;
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Equal("NON_EMBEDDED_FONT", w.Code));
    }

    [Fact]
    public async Task LongFontNameWithMarkupRemainsLiteralInJsonAndHtml()
    {
        var name = "Original<script>globalThis.injected=1</script>&" + new string('A', 300);
        var pdfName = string.Concat(System.Text.Encoding.ASCII.GetBytes(name).Select(b => $"#{b:X2}"));
        using var files = new TextFile(PdfFixture.CreateFontReport(font: PdfFixture.StandardFont.Replace("Helvetica", pdfName)));
        var output = Path.Combine(files.DirectoryPath, "結果");
        var process = await CliProcess.Run("compare", files.Path, files.Path, "--out", output);
        Assert.Equal(0, process.Code);
        Assert.All(Read(output).Warnings, w => Assert.Contains(name, w.Message));
        var html = File.ReadAllText(Path.Combine(output, "report.html"));
        Assert.DoesNotContain("<script>globalThis.injected", html);
        Assert.Contains("&lt;script&gt;globalThis.injected", html);
    }

    private static ReportDocument Read(string output) => JsonSerializer.Deserialize<ReportDocument>(File.ReadAllText(Path.Combine(output, "result.json")), ReportJson.Options)!;
}
