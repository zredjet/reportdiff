using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

public sealed class RawOverlayTests
{
    // RGB を明示し、OpenCV の BGR 順との取り違えも検出する。
    [Theory]
    [InlineData(255, 255, 255, 255, 255, 255, 255, 255, 255)]
    [InlineData(0, 0, 0, 0, 0, 0, 0, 0, 0)]
    [InlineData(128, 128, 128, 128, 128, 128, 128, 128, 128)]
    [InlineData(0, 0, 0, 255, 255, 255, 255, 0, 0)]
    [InlineData(255, 255, 255, 0, 0, 0, 0, 0, 255)]
    [InlineData(128, 128, 128, 255, 255, 255, 255, 128, 128)]
    [InlineData(255, 255, 255, 128, 128, 128, 128, 128, 255)]
    [InlineData(255, 0, 0, 0, 0, 255, 29, 29, 76)]
    [InlineData(0, 255, 0, 0, 0, 250, 29, 29, 150)]
    [InlineData(255, 0, 0, 76, 76, 76, 76, 76, 76)]
    public void FixedPixels(int ar, int ag, int ab, int br, int bg, int bb, int r, int g, int b)
    {
        using var a = new Mat(1, 1, MatType.CV_8UC3, new Scalar(ab, ag, ar));
        using var inputB = new Mat(1, 1, MatType.CV_8UC3, new Scalar(bb, bg, br));
        using var overlay = RawOverlay.Create(a, inputB, "#000000");
        Assert.Equal(new Vec3b((byte)b, (byte)g, (byte)r), overlay.At<Vec3b>(0, 0));
    }

    [Fact]
    public void AllGrayPairsRetainTonesAndSwappingOnlySwapsRedBlueOnNonContinuousImages()
    {
        using var parentA = new Mat(260, 260, MatType.CV_8UC3, Scalar.All(17));
        using var parentB = new Mat(260, 260, MatType.CV_8UC3, Scalar.All(239));
        using var a = new Mat(parentA, new Rect(2, 2, 256, 256));
        using var b = new Mat(parentB, new Rect(2, 2, 256, 256));
        Assert.False(a.IsContinuous());
        for (var y = 0; y < 256; y++)
        for (var x = 0; x < 256; x++)
        {
            a.Set(y, x, new Vec3b((byte)y, (byte)y, (byte)y));
            b.Set(y, x, new Vec3b((byte)x, (byte)x, (byte)x));
        }
        using var originalA = parentA.Clone(); using var originalB = parentB.Clone();
        using var overlay = RawOverlay.Create(a, b, "#000000"); using var reverse = RawOverlay.Create(b, a, "#000000");
        for (var y = 0; y < 256; y++)
        for (var x = 0; x < 256; x++)
        {
            var color = overlay.At<Vec3b>(y, x); var swapped = reverse.At<Vec3b>(y, x);
            Assert.Equal((byte)y, color.Item0); Assert.Equal((byte)x, color.Item2);
            Assert.Equal(new Vec3b(color.Item2, color.Item1, color.Item0), swapped);
            if (x == y) Assert.Equal(new Vec3b((byte)x, (byte)x, (byte)x), color);
        }
        Assert.Equal(0, Cv2.Norm(parentA, originalA, NormTypes.INF));
        Assert.Equal(0, Cv2.Norm(parentB, originalB, NormTypes.INF));
    }

    [Fact]
    public void OnePixelShiftShowsRedAndBlueEdgesWithCommonBlackAndAntialiasing()
    {
        using var a = new Mat(3, 7, MatType.CV_8UC3, Scalar.All(255));
        using var b = a.Clone();
        foreach (var (x, gray) in new[] { (1, 128), (2, 0), (3, 0), (4, 128) })
        {
            a.Set(1, x, new Vec3b((byte)gray, (byte)gray, (byte)gray));
            b.Set(1, x + 1, new Vec3b((byte)gray, (byte)gray, (byte)gray));
        }
        using var overlay = RawOverlay.Create(a, b, "#000000");
        Assert.Equal(new Vec3b(128, 128, 255), overlay.At<Vec3b>(1, 1));
        Assert.Equal(new Vec3b(0, 0, 128), overlay.At<Vec3b>(1, 2));
        Assert.Equal(new Vec3b(0, 0, 0), overlay.At<Vec3b>(1, 3));
        Assert.Equal(new Vec3b(128, 0, 0), overlay.At<Vec3b>(1, 4));
        Assert.Equal(new Vec3b(255, 128, 128), overlay.At<Vec3b>(1, 5));
    }

    [Theory]
    [InlineData("[]")] [InlineData("{}")]
    [InlineData("1")] [InlineData("yes")] [InlineData("null")]
    public void InvalidSettingNamesTheKey(string value) => Assert.Contains("report.raw_overlay",
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load("report: {raw_overlay: " + value + "}")).Message);

    [Fact]
    public void SettingsAndCliHaveExplicitPrecedence()
    {
        Assert.False(ConfigurationLoader.Load().Report.RawOverlay);
        Assert.True(ConfigurationLoader.LoadLayered("report: {raw_overlay: true}", "report: {crop_margin_mm: 3}").Report.RawOverlay);
        Assert.False(ConfigurationLoader.LoadLayered("report: {raw_overlay: true}", "report: {raw_overlay: false}").Report.RawOverlay);
        using var files = new DirectoryTestFiles();
        var common = files.Text("common.yaml", "report: {raw_overlay: true}");
        files.Text("selected.yaml", "report: {raw_overlay: false}");
        var rules = files.Text("rules.yaml", "schema_version: 1\nrules: [{pattern: invoice, config: selected.yaml}]");
        foreach (var flag in new[] { false, true })
        {
            var loaded = new DirectoryRules(files.Command with { Config = common, Rules = rules, RawOverlay = flag });
            Assert.True(loaded.Common.Report.RawOverlay);
            Assert.Equal(flag, Assert.Single(loaded.Match("invoice.pdf", "invoice.pdf")).Settings.Report.RawOverlay);
            Assert.Equal(flag, loaded.Description.Cli.RawOverlay);
        }
        Assert.True(CommandLine.Parse(["compare", "a", "b", "--out", "out", "--raw-overlay"]).RawOverlay);
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["compare", "a", "b", "--out", "out", "--raw-overlay", "--raw-overlay"]));
        Assert.True(ConfigurationLoader.Load("report: {raw_overlay: true}").ToReportConfiguration().Report.RawOverlay);
    }

    [Theory]
    [InlineData("a")] [InlineData("b")] [InlineData("overlay")]
    public void RawPngFailureFaultsWriterAndPreservesPreviousOutput(string target)
    {
        using var files = new DirectoryTestFiles();
        Directory.CreateDirectory(files.Output);
        var sentinel = Path.Combine(files.Output, "result.json"); File.WriteAllText(sentinel, "旧結果");
        var settings = ConfigurationLoader.Load("report: {raw_overlay: true}");
        using (var workspace = new OutputWorkspace(files.Output, true, []))
        {
            var writer = new ReportWriter(workspace.StagingPath, Inputs(), settings.ToReportConfiguration());
            Directory.CreateDirectory(Path.Combine(workspace.StagingPath, "pages", "p001_raw_" + target + ".png"));
            using var image = new Mat(20, 20, MatType.CV_8UC3, Scalar.All(255));
            using var pair = PageNormalizer.Normalize(image, image);
            using var comparison = PageComparer.Compare(pair.A, pair.B, settings.ForPage(1, 300));
            Assert.Throws<ReportWriteException>(() => writer.AddComparedPage(1, pair, comparison, 300));
            Assert.Throws<ReportWriteException>(() => writer.Complete());
        }
        Assert.Equal("旧結果", File.ReadAllText(sentinel));
        Assert.Single(Directory.GetFiles(files.Output));
    }

    [Fact]
    public void EvidenceHtmlRejectsNonLocalImagePaths()
    {
        using var files = new ReportTestDirectory();
        var settings = ConfigurationLoader.Load("report: {raw_overlay: true}");
        var writer = new ReportWriter(files.Output, Inputs(), settings.ToReportConfiguration());
        using var a = new Mat(20, 20, MatType.CV_8UC3, Scalar.All(255));
        using var pair = PageNormalizer.Normalize(a, a);
        using var comparison = PageComparer.Compare(pair.A, pair.B, settings.ForPage(1, 300));
        var page = writer.AddComparedPage(1, pair, comparison, 300);
        var doc = writer.Complete();
        Assert.Throws<ReportWriteException>(() => HtmlReportWriter.Render(doc with
        { Pages = [page with { RawEvidence = page.RawEvidence! with { Overlay = "https://example.com/image.png" } }] }));
        var json = JsonSerializer.Serialize(doc, ReportJson.Options);
        Assert.Equal(page.RawEvidence, JsonSerializer.Deserialize<ReportDocument>(json, ReportJson.Options)!.Pages[0].RawEvidence);
    }

    [Fact]
    public void RawWriteFailureAbortsWholeDirectoryReplacement()
    {
        using var files = new DirectoryTestFiles();
        files.Image("A/a.png"); files.Image("B/a.png");
        var sentinel = files.Text("比較 結果/old.txt", "旧結果");
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        Assert.Throws<ReportWriteException>(() => DirectoryComparison.Run(files.Command with { RawOverlay = true, Force = true },
            stdout, stderr, compare: (_, settings, output) =>
            {
                var writer = new ReportWriter(output, Inputs(), settings.ToReportConfiguration());
                Directory.CreateDirectory(Path.Combine(output, "pages", "p001_raw_overlay.png"));
                using var image = new Mat(20, 20, MatType.CV_8UC3, Scalar.All(255));
                using var pair = PageNormalizer.Normalize(image, image);
                using var comparison = PageComparer.Compare(pair.A, pair.B, settings.ForPage(1, 300));
                writer.AddComparedPage(1, pair, comparison, 300);
                return writer.Complete();
            }));
        Assert.Equal("旧結果", File.ReadAllText(sentinel));
        Assert.Single(Directory.GetFiles(files.Output));
        Assert.Empty(Directory.EnumerateDirectories(files.Root, ".reportdiff-*"));
    }

    private static ReportInputs Inputs() => new(new("A.png", "png", 1, ""), new("B.png", "png", 1, ""));
}
