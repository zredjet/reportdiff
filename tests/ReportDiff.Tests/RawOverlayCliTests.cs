using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Pdf;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class RawOverlayCliTests
{
    [Theory]
    [InlineData("same", "same", 0)]
    [InlineData("different", "different", 1)]
    [InlineData("excluded", "same", 0)]
    [InlineData("too_different", "too_different", 1)]
    [InlineData("size", "different", 1)]
    [InlineData("aligned", "different", 1)]
    public async Task ToggleOnlyAddsEvidenceAndKeepsExistingJsonPngAndExit(string scenario, string status, int exit)
    {
        using var files = new DirectoryTestFiles();
        string a, b;
        using (var imageA = new Mat(64, 64, MatType.CV_8UC3, Scalar.All(255)))
        using (var imageB = new Mat(scenario == "size" ? 70 : 64, scenario == "size" ? 70 : 64, MatType.CV_8UC3, Scalar.All(255)))
        {
            if (scenario != "same") Cv2.Rectangle(imageB, new Rect(20, 20, 10, 8), new Scalar(180, 95, 45), -1);
            a = files.Bytes("元 A.png", imageA.ImEncode(".png")); b = files.Bytes("元 B.png", imageB.ImEncode(".png"));
        }
        var yaml = "dpi: 144\n" + (scenario switch
        {
            "excluded" => "exclude: [{page: all, x: 0, y: 0, w: 100, h: 100}]\n",
            "too_different" => "cluster: {max_diff_ratio: 0.0001}\n",
            "aligned" => "align: {enabled: true}\n",
            _ => ""
        });
        if (scenario == "aligned")
        {
            a = files.Bytes("元 A.pdf", AlignmentCliTests.CreatePage(false, false));
            b = files.Bytes("元 B.pdf", AlignmentCliTests.CreatePage(true, true));
        }
        var config = files.Text("設定.yaml", yaml);
        var off = Path.Combine(files.Root, "無効"); var on = Path.Combine(files.Root, "有効");
        foreach (var (output, enabled) in new[] { (off, false), (on, true) })
        {
            var args = new List<string> { "compare", a, b, "--out", output, "--config", config };
            if (enabled) args.Add("--raw-overlay");
            var run = await CliProcess.Run(args.ToArray()); Assert.Equal(exit, run.Code); Assert.Empty(run.Error);
            Assert.Equal(status, Assert.Single(Read(output).Pages).Status);
        }
        AssertExistingOutputsEqual(off, on);
        var page = Assert.Single(Read(on).Pages); var evidence = Assert.IsType<RawEvidence>(page.RawEvidence);
        Assert.Equal("original_top_left", evidence.CoordinateSystem); Assert.Equal("grayscale_red_blue_v1", evidence.Method);
        Assert.Equal(144, evidence.Dpi); Assert.Equal(page.SizePx, evidence.CanvasSizePx);
        Assert.False(evidence.A.Missing); Assert.False(evidence.B.Missing);
        Assert.Equal(page.Images.A ?? "pages/p001_raw_a.png", evidence.A.Image);
        Assert.Equal(page.Images.BOriginal ?? page.Images.B ?? "pages/p001_raw_b.png", evidence.B.Image);
        using var savedA = Image(on, evidence.A.Image); using var savedB = Image(on, evidence.B.Image);
        using var expected = RawOverlay.Create(savedA, savedB); using var actual = Image(on, evidence.Overlay);
        Assert.Equal(0, Cv2.Norm(expected, actual, NormTypes.INF));
        if (scenario == "size")
        {
            Assert.Equal(new PixelSize(64, 64), evidence.A.OriginalSizePx);
            Assert.Equal(new RawEvidencePadding(6, 6), evidence.A.PaddingPx);
            Assert.Equal(new PixelSize(70, 70), evidence.B.OriginalSizePx);
            Assert.Equal(new Vec3b(255, 255, 255), savedA.At<Vec3b>(20, 68));
            Assert.Equal(new Vec3b(255, 255, 255), savedA.At<Vec3b>(68, 20));
        }
        if (scenario == "aligned")
        {
            Assert.Equal(new PixelShift(-6, 4), page.GlobalShiftPx);
            using var original = PdfReader.Open(b); using var rendered = original.ReadPage(1, 144);
            Assert.Equal(0, Cv2.Norm(savedB, rendered.Pixels, NormTypes.INF));
            using var corrected = Image(on, page.Images.B!);
            Assert.True(Cv2.Norm(savedB, corrected, NormTypes.INF) > 0);
        }
        else if (scenario != "same")
            Assert.Equal(new Vec3b(180, 95, 45), savedB.At<Vec3b>(20, 20));
        Assert.Contains("判定から独立", File.ReadAllText(Path.Combine(on, "report.html")));
    }

    [Fact]
    public async Task EvidencePngIsIndependentOfProfileExclusionAndGlobalAlignment()
    {
        using var files = new DirectoryTestFiles();
        var a = files.Bytes("A.pdf", AlignmentCliTests.CreatePage(false, false));
        var b = files.Bytes("B.pdf", AlignmentCliTests.CreatePage(true, true));
        byte[]? expected = null;
        foreach (var (profile, extra) in new[] { ("normal", ""), ("strict", ""), ("loose", ""),
            ("normal", "exclude: [{page: all, x: 0, y: 0, w: 1000, h: 1000}]"), ("normal", "align: {enabled: true}") })
        {
            var config = files.Text("設定.yaml", "dpi: 144\nreport: {raw_overlay: true}\n" + extra);
            var output = Path.Combine(files.Root, Guid.NewGuid().ToString("N"));
            var run = await CliProcess.Run("compare", a, b, "--out", output, "--config", config, "--profile", profile);
            Assert.Equal(extra.StartsWith("exclude") ? 0 : 1, run.Code); Assert.Empty(run.Error);
            var page = Assert.Single(Read(output).Pages);
            if (extra.StartsWith("align")) Assert.Equal("applied", page.Alignment.Status);
            var png = File.ReadAllBytes(Path.Combine(output, page.RawEvidence!.Overlay));
            expected ??= png; Assert.Equal(expected, png);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnpairedPageUsesWhiteMissingSideAndKeepsSelectionAndStatus(bool reverse)
    {
        using var files = new DirectoryTestFiles();
        var first = files.Bytes("A.pdf", PdfFixture.CreatePages((72, 80), (80, 100)));
        var second = files.Bytes("B.pdf", PdfFixture.CreatePages((90, 70)));
        var a = reverse ? second : first; var b = reverse ? first : second;
        var off = Path.Combine(files.Root, "off"); var on = Path.Combine(files.Root, "on");
        foreach (var (output, enabled) in new[] { (off, false), (on, true) })
        {
            var args = new List<string> { "compare", a, b, "--dpi", "72", "--pages", "2", "--no-html", "--out", output };
            if (enabled) args.Add("--raw-overlay");
            Assert.Equal(1, (await CliProcess.Run(args.ToArray())).Code);
        }
        AssertExistingOutputsEqual(off, on);
        var page = Assert.Single(Read(on).Pages); Assert.Equal(2, page.Page);
        Assert.Equal(reverse ? "only_in_b" : "only_in_a", page.Status);
        var evidence = page.RawEvidence!; var missing = reverse ? evidence.A : evidence.B;
        Assert.True(missing.Missing); Assert.Null(missing.OriginalSizePx); Assert.Null(missing.PaddingPx);
        Assert.Equal(new PixelSize(80, 100), evidence.CanvasSizePx); Assert.Equal(72, evidence.Dpi);
        using var white = Image(on, missing.Image); using var expectedWhite = new Mat(100, 80, MatType.CV_8UC3, Scalar.All(255));
        Assert.Equal(0, Cv2.Norm(white, expectedWhite, NormTypes.INF));
        using var overlay = Image(on, evidence.Overlay); var ink = overlay.At<Vec3b>(20, 20);
        Assert.Equal((byte)255, reverse ? ink.Item0 : ink.Item2);
        Assert.True((reverse ? ink.Item2 : ink.Item0) < 255);
        Assert.Equal(ink.Item1, reverse ? ink.Item2 : ink.Item0);
        Assert.False(File.Exists(Path.Combine(on, "report.html")));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MultiPagePdfSavesSameAndChangedPagesWithoutSaveAll(bool selectSameOnly)
    {
        using var files = new DirectoryTestFiles();
        var a = files.Bytes("A.pdf", PdfFixture.CreateComparisonReport(false));
        var b = files.Bytes("B.pdf", PdfFixture.CreateComparisonReport(true));
        var args = new List<string> { "compare", a, b, "--dpi", "72", "--out", files.Output, "--raw-overlay" };
        if (selectSameOnly) args.AddRange(["--pages", "1"]);
        Assert.Equal(selectSameOnly ? 0 : 1, (await CliProcess.Run(args.ToArray())).Code);
        var pages = Read(files.Output).Pages; Assert.Equal(selectSameOnly ? 1 : 2, pages.Count);
        Assert.Equal("same", pages[0].Status); Assert.Null(pages[0].Images.Overlay);
        foreach (var page in pages)
        {
            var evidence = Assert.IsType<RawEvidence>(page.RawEvidence);
            foreach (var path in new[] { evidence.A.Image, evidence.B.Image, evidence.Overlay })
                Assert.True(File.Exists(Path.Combine(files.Output, path)));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DirectoryCliRespectsPerFileSettingAndExplicitFlag(bool flag)
    {
        using var files = new DirectoryTestFiles();
        files.Image("A/通常.png"); files.Image("B/通常.png", true);
        files.Image("A/個別.png"); files.Image("B/個別.png");
        var common = files.Text("common.yaml", "report: {raw_overlay: true}");
        files.Text("selected.yaml", "report: {raw_overlay: false}");
        var rules = files.Text("rules.yaml", "schema_version: 1\nrules: [{pattern: 個別, config: selected.yaml}]");
        var args = new List<string> { "--config", common, "--rules", rules, "--no-html" };
        if (flag) args.Add("--raw-overlay");
        Assert.Equal(1, (await files.Run(args.ToArray())).Code);
        var index = files.Read(); Assert.Equal(flag, index.Configuration.Cli.RawOverlay);
        foreach (var file in index.Files)
        {
            var enabled = file.RelativePath.StartsWith("通常") || flag;
            var report = files.Child(file); Assert.Equal(enabled, report.Config.Report.RawOverlay);
            Assert.Equal(enabled, report.Pages[0].RawEvidence is not null); Assert.Null(file.Html);
        }
    }

    private static void AssertExistingOutputsEqual(string off, string on)
    {
        var before = JsonNode.Parse(File.ReadAllText(Path.Combine(off, "result.json")))!;
        var after = JsonNode.Parse(File.ReadAllText(Path.Combine(on, "result.json")))!;
        Assert.False(before["config"]!["report"]!["raw_overlay"]!.GetValue<bool>());
        Assert.True(after["config"]!["report"]!["raw_overlay"]!.GetValue<bool>());
        foreach (var doc in new[] { before, after })
        {
            doc.AsObject().Remove("generated_at"); doc["config"]!["report"]!.AsObject().Remove("raw_overlay");
            foreach (var page in doc["pages"]!.AsArray()) page!.AsObject().Remove("raw_evidence");
        }
        Assert.True(JsonNode.DeepEquals(before, after));
        foreach (var path in Directory.GetFiles(off, "*.png", SearchOption.AllDirectories))
            Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(Path.Combine(on, Path.GetRelativePath(off, path))));
        Assert.DoesNotContain(Directory.GetFiles(off, "*.png", SearchOption.AllDirectories), path => path.Contains("_raw_"));
    }

    private static ReportDocument Read(string output) => JsonSerializer.Deserialize<ReportDocument>(
        File.ReadAllText(Path.Combine(output, "result.json")), ReportJson.Options)!;
    private static Mat Image(string output, string path) => Cv2.ImDecode(File.ReadAllBytes(Path.Combine(output, path)), ImreadModes.Color);
}
