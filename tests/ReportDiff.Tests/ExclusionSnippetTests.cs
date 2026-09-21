using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Report;
using SkiaSharp;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class ExclusionSnippetTests
{
    [Theory]
    [InlineData(0, "- {page: 7, x: 1, y: 2.5, w: 2.5, h: 0.5, note: \"\"}")]
    [InlineData(1, "- {page: 7, x: 0, y: 1.5, w: 4.5, h: 2.5, note: \"\"}")]
    [InlineData(20, "- {page: 7, x: 0, y: 0, w: 23.5, h: 23, note: \"\"}")]
    public void RoundsOutwardAndWritesCultureIndependentYaml(double margin, string expected)
    {
        var cluster = Cluster(new(10, 10, 20, 20), new(1.24, 2.76, 2.21, .18));
        var page = Page(new(1000, 1000), cluster) with { Page = 7 };
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var yaml = ExclusionSnippet.Create(page, cluster, 300, margin);
            Assert.Equal(expected, yaml);
            Assert.Equal(7, Assert.Single(ConfigurationLoader.Load("exclude:\n  " + yaml).Exclude).Page);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(72, 0)] [InlineData(72, 1)] [InlineData(72, 20)]
    [InlineData(300, 0)] [InlineData(300, 1)] [InlineData(300, 20)]
    [InlineData(1200, 0)] [InlineData(1200, 1)] [InlineData(1200, 20)]
    public void PageClippingStillExcludesEveryEdgePixel(int dpi, double margin)
    {
        const int width = 67, height = 59;
        using var a = new Mat(height, width, MatType.CV_8UC3, Scalar.All(255));
        foreach (var rect in new[] { new Rect(0, 0, 9, 7), new(width - 9, 0, 9, 7),
                     new Rect(0, height - 7, 9, 7), new(width - 9, height - 7, 9, 7) })
        {
            using var b = a.Clone();
            Cv2.Rectangle(b, rect, Scalar.All(0), -1);
            // ページ端の小図形をずれで吸収させず、除外矩形の画素境界だけを検証する。
            var parameters = new ComparisonParameters { Dpi = dpi, Diff = new() { MaxShiftMm = 0, EdgeTolerance = 0 } };
            using var before = PageComparer.Compare(a, b, parameters);
            var detected = Assert.Single(before.Clusters);
            var bounds = detected.Bounds;
            var cluster = Cluster(new(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                new(Units.PixelsToMm(bounds.X, dpi), Units.PixelsToMm(bounds.Y, dpi),
                    Units.PixelsToMm(bounds.Width, dpi), Units.PixelsToMm(bounds.Height, dpi)));
            var yaml = ExclusionSnippet.Create(Page(new(width, height), cluster), cluster, dpi, margin);
            var settings = ConfigurationLoader.Load($"dpi: {dpi}\nexclude:\n  {yaml}", profile: "strict");
            var box = Assert.Single(settings.Exclude);
            Assert.InRange(box.X, 0, Units.PixelsToMm(width, dpi));
            Assert.InRange(box.Y, 0, Units.PixelsToMm(height, dpi));
            Assert.True(box.X + box.W <= Units.PixelsToMm(width, dpi) + 1e-9);
            Assert.True(box.Y + box.H <= Units.PixelsToMm(height, dpi) + 1e-9);
            using var after = PageComparer.Compare(a, b, settings.ForPage(1, dpi));
            Assert.Equal("same", after.Status);
        }
    }

    [Theory]
    [InlineData("-0.1")] [InlineData("20.001")] [InlineData("NaN")]
    [InlineData(".inf")] [InlineData("true")] [InlineData("[]")]
    public void InvalidMarginNamesItsSetting(string value) => Assert.Contains("report.snippet_margin_mm",
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load("report: {snippet_margin_mm: " + value + "}")).Message);

    [Fact]
    public void MarginIsIndependentAndSurvivesLayeredSettings()
    {
        var defaults = ConfigurationLoader.Load();
        Assert.Equal(1, defaults.Report.SnippetMarginMm);
        Assert.Equal(2, defaults.Report.CropMarginMm);
        var settings = ConfigurationLoader.LoadLayered("report: {snippet_margin_mm: 20, crop_margin_mm: 5}",
            "report: {crop_margin_mm: 0}", "strict", 400);
        Assert.Equal(0, settings.Report.CropMarginMm);
        Assert.Equal(20, settings.Report.SnippetMarginMm);
        Assert.Equal(20, settings.ToReportConfiguration().Report.SnippetMarginMm);
        Assert.Equal(0, ConfigurationLoader.LoadLayered("report: {snippet_margin_mm: 20}",
            "report: {snippet_margin_mm: 0}").Report.SnippetMarginMm);
    }

    [Theory]
    [InlineData("pdf", 72)] [InlineData("png", 300)]
    public void HtmlUsesInputDpiAndDoesNotMoveCorrectedCoordinates(string type, int expectedDpi)
    {
        var cluster = Cluster(new(55, 50, 12, 9), new(Units.PixelsToMm(55, expectedDpi), Units.PixelsToMm(50, expectedDpi),
            Units.PixelsToMm(12, expectedDpi), Units.PixelsToMm(9, expectedDpi)));
        var page = Page(new(67, 59), cluster) with { GlobalShiftPx = new(-6, 4) };
        var report = new ReportDocument(1, ReportTool.Current, DateTimeOffset.UnixEpoch,
            new(new("A", type, 1, ""), new("B", type, 1, "")),
            ConfigurationLoader.Load("dpi: 72\nimage_dpi: 300\nreport: {snippet_margin_mm: 20}").ToReportConfiguration(),
            new("different", 1, 1, 1, 0), [], [page]);
        var html = HtmlReportWriter.Render(report);
        var snippet = Assert.Single(Snippets(html));
        var box = Assert.Single(ConfigurationLoader.Load("exclude:\n  " + snippet).Exclude);
        Assert.Equal(0, box.X); Assert.Equal(0, box.Y);
        Assert.Equal(Units.PixelsToMm(67, expectedDpi), box.W, 9);
        Assert.Equal(Units.PixelsToMm(59, expectedDpi), box.H, 9);
        Assert.Contains("readonly", html); Assert.Contains("YAML を選択", html);
        Assert.DoesNotContain("http://", html); Assert.DoesNotContain("https://", html);
        Assert.Empty(Snippets(HtmlReportWriter.Render(report with { Pages = [page with { Clusters = [], Status = "same" }] })));
    }

    [Fact]
    public async Task ImageSnippetRoundTripsAndMarginOnlyChangesTheNewSettingAndHtml()
    {
        using var files = new DirectoryTestFiles();
        var a = files.Image("旧.png"); var b = files.Image("新.png", true);
        var first = Path.Combine(files.Root, "既定結果"); var second = Path.Combine(files.Root, "余白結果");
        Assert.Equal(1, (await CliProcess.Run("compare", a, b, "--out", first)).Code);
        var config = files.Text("余白.yaml", "report: {snippet_margin_mm: 0}");
        Assert.Equal(1, (await CliProcess.Run("compare", a, b, "--out", second, "--config", config)).Code);
        var firstJson = JsonNode.Parse(File.ReadAllText(Path.Combine(first, "result.json")))!;
        var secondJson = JsonNode.Parse(File.ReadAllText(Path.Combine(second, "result.json")))!;
        Assert.Equal(1, firstJson["config"]!["report"]!["snippet_margin_mm"]!.GetValue<double>());
        Assert.Equal(0, secondJson["config"]!["report"]!["snippet_margin_mm"]!.GetValue<double>());
        foreach (var document in new[] { firstJson, secondJson })
        {
            document.AsObject().Remove("generated_at");
            document["config"]!["report"]!.AsObject().Remove("snippet_margin_mm");
        }
        Assert.True(JsonNode.DeepEquals(firstJson, secondJson));
        foreach (var png in Directory.GetFiles(first, "*.png", SearchOption.AllDirectories))
            Assert.Equal(File.ReadAllBytes(png), File.ReadAllBytes(Path.Combine(second, Path.GetRelativePath(first, png))));
        var line = Assert.Single(Snippets(File.ReadAllText(Path.Combine(second, "report.html"))));
        config = files.Text("除外.yaml", "exclude:\n  " + line + "\n");
        var third = Path.Combine(files.Root, "除外結果");
        Assert.Equal(0, (await CliProcess.Run("compare", a, b, "--out", third, "--config", config)).Code);
        Assert.Empty(Read(third).Pages.Single().Clusters);
    }

    [Fact]
    public async Task PdfSnippetUsesActualPageNumberAndLeavesOtherPagesDifferent()
    {
        using var files = new DirectoryTestFiles();
        var a = files.Bytes("旧.pdf", TwoPages(false)); var b = files.Bytes("新.pdf", TwoPages(true));
        var output = Path.Combine(files.Root, "初回");
        Assert.Equal(1, (await CliProcess.Run("compare", a, b, "--out", output)).Code);
        var snippets = Snippets(File.ReadAllText(Path.Combine(output, "report.html")));
        Assert.Equal(2, snippets.Length); Assert.Contains("page: 2,", snippets[1]);
        var config = files.Text("除外.yaml", "exclude:\n  " + snippets[1]);
        output = Path.Combine(files.Root, "除外後");
        Assert.Equal(1, (await CliProcess.Run("compare", a, b, "--out", output, "--config", config)).Code);
        var pages = Read(output).Pages;
        Assert.Single(pages[0].Clusters); Assert.Equal("same", pages[1].Status); Assert.Empty(pages[1].Clusters);
    }

    [Fact]
    public async Task AlignedPdfSnippetExcludesTheCorrectedDifference()
    {
        using var files = new DirectoryTestFiles();
        var a = files.Bytes("旧.pdf", AlignmentCliTests.CreatePage(false, false));
        var b = files.Bytes("新.pdf", AlignmentCliTests.CreatePage(true, true));
        const string basis = "dpi: 144\nalign: {enabled: true}\n";
        var config = files.Text("補正.yaml", basis);
        var first = Path.Combine(files.Root, "初回");
        Assert.Equal(1, (await CliProcess.Run("compare", a, b, "--out", first, "--config", config)).Code);
        var before = Read(first).Pages.Single();
        Assert.Equal(new PixelShift(-6, 4), before.GlobalShiftPx); Assert.Single(before.Clusters);
        var line = Assert.Single(Snippets(File.ReadAllText(Path.Combine(first, "report.html"))));
        config = files.Text("除外.yaml", basis + "exclude:\n  " + line);
        var second = Path.Combine(files.Root, "除外後");
        var run = await CliProcess.Run("compare", a, b, "--out", second, "--config", config);
        Assert.Equal(0, run.Code); Assert.Empty(run.Error);
        var after = Read(second).Pages.Single();
        Assert.Equal(before.GlobalShiftPx, after.GlobalShiftPx); Assert.Equal("same", after.Status);
    }

    private static string[] Snippets(string html) => Regex.Matches(html, "<textarea[^>]*>(.*?)</textarea>", RegexOptions.Singleline)
        .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToArray();
    private static ReportDocument Read(string output) => JsonSerializer.Deserialize<ReportDocument>(
        File.ReadAllText(Path.Combine(output, "result.json")), ReportJson.Options)!;
    private static ReportCluster Cluster(PixelBox pixels, MillimeterBox mm) => new(1, pixels, mm, 10, .1, "changed", null, null, null,
        new("crops/p001_c001_a.png", "crops/p001_c001_b.png", "crops/p001_c001_diff.png"));
    private static ReportPage Page(PixelSize size, ReportCluster cluster) => new(1, "different", size, false, 10, 0, 0, 0,
        new(null, null, null), [cluster]);
    private static byte[] TwoPages(bool changed)
    {
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            for (var page = 0; page < 2; page++)
            {
                using var canvas = document.BeginPage(100, 100);
                using var ink = new SKPaint { Color = changed ? SKColors.Black : SKColors.White };
                canvas.DrawRect(30, 30, 15, 10, ink);
                document.EndPage();
            }
            document.Close();
        }
        return stream.ToArray();
    }
}
