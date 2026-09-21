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
public sealed class RegionIntegrationTests
{
    [Fact]
    public async Task SuppressedPageIsSavedAndAuditRestoresDifferencesWhileRawSourcesStayIdentical()
    {
        using var files = new DirectoryTestFiles();
        using var a = RegionCoreTests.White(); using var b = RegionCoreTests.White();
        RegionCoreTests.Draw(a, 100, 100); RegionCoreTests.Draw(b, 101, 100);
        var inputA = files.Bytes("旧.png", a.ImEncode(".png")); var inputB = files.Bytes("新.png", b.ImEncode(".png"));
        const string baseline = "diff: {max_shift_mm: 0, edge_tolerance: 0}\nreport: {raw_overlay: true}\n";
        var config = files.Text("領域.yaml", baseline + "regions: [{name: '明細 <script> & 領域', page: all, x: 0, y: 0, w: 100, h: 100, profile: loose}]\nexclude: [{page: all, x: 1, y: 1, w: 1, h: 1}]\n");
        var plain = files.Text("基準.yaml", baseline);
        var outputs = new List<string>();
        foreach (var mode in new[] { "region", "audit", "plain" })
        {
            var output = Path.Combine(files.Root, mode); outputs.Add(output);
            var run = await CliProcess.Run(["compare", inputA, inputB, "--out", output, "--config", mode == "plain" ? plain : config,
                .. (mode == "audit" ? new[] { "--no-regions" } : Array.Empty<string>())]);
            Assert.Equal(mode == "region" ? 0 : 1, run.Code); Assert.Empty(run.Error);
            var report = Read(output); var page = Assert.Single(report.Pages);
            Assert.NotNull(page.Images.Overlay);
            using var sourceA = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(output, page.RawEvidence!.A.Image)), ImreadModes.Color);
            using var sourceB = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(output, page.RawEvidence.B.Image)), ImreadModes.Color);
            Assert.Equal(0, Cv2.Norm(a, sourceA, NormTypes.INF)); Assert.Equal(0, Cv2.Norm(b, sourceB, NormTypes.INF));
            var html = File.ReadAllText(Path.Combine(output, "report.html"));
            if (mode == "region")
            {
                Assert.Equal(0, report.Summary.Clusters); Assert.True(page.Regions!.SuppressedPixels > 0);
                Assert.True(page.Regions.SuppressedComponents > 0); Assert.Equal(2, page.Regions.Runs.Count);
                Assert.Contains("8 近傍", html); Assert.Contains("薄い橙色", html); Assert.Contains("明細 &lt;script&gt; &amp; 領域", html);
                Assert.DoesNotContain("<script> & 領域", html);
                using var overlay = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(output, page.Images.Overlay!)), ImreadModes.Color);
                Assert.True(Cv2.Norm(b, overlay, NormTypes.INF) > 0);
            }
            if (mode == "audit")
            {
                Assert.Empty(report.Config.Regions!); Assert.Empty(report.Config.Exclude);
                Assert.Single(report.Config.RegionAudit!.Regions); Assert.Single(report.Config.RegionAudit.Exclude);
                Assert.Contains("領域を無効にした監査比較", html); Assert.Null(page.Regions);
            }
        }
        var raw = outputs.Select(o => File.ReadAllBytes(Path.Combine(o, Read(o).Pages[0].RawEvidence!.Overlay))).ToArray();
        Assert.Equal(raw[0], raw[1]); Assert.Equal(raw[0], raw[2]);
        Assert.Equal(JsonSerializer.Serialize(Read(outputs[1]).Pages, ReportJson.Options), JsonSerializer.Serialize(Read(outputs[2]).Pages, ReportJson.Options));
    }

    [Fact]
    public async Task DirectoryRulesReplaceClearInheritAndNoHtmlWorks()
    {
        using var files = new DirectoryTestFiles();
        foreach (var id in new[] { "inherit", "clear", "replace" }) { files.Image("A/" + id + ".png"); files.Image("B/" + id + ".png", true); }
        var common = files.Text("common.yaml", "regions: [{name: 全面除外, page: all, x: 0, y: 0, w: 100, h: 100, mode: exclude}]");
        files.Text("clear.yaml", "regions: []"); files.Text("replace.yaml", RegionConfigurationTests.One);
        var rules = files.Text("rules.yaml", "schema_version: 1\nrules: [{pattern: clear, config: clear.yaml}, {pattern: replace, config: replace.yaml}]");
        var result = await files.Run("--config", common, "--rules", rules, "--no-html", "--raw-overlay");
        Assert.Equal(1, result.Code); Assert.Empty(result.Error);
        var index = files.Read();
        var reports = index.Files.Where(f => f.Json is not null).Select(files.Child).ToArray();
        Assert.Equal(3, reports.Length);
        Assert.Single(reports, r => r.Summary.Status == "same");
        Assert.Single(reports, r => r.Config.Regions is null);
        Assert.All(reports, r => Assert.NotNull(r.Pages[0].RawEvidence));
        Assert.Empty(Directory.GetFiles(files.Output, "*.html", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RegionExclusionAndAuditAffectGlobalAlignmentWithoutChangingRawEvidence()
    {
        using var files = new DirectoryTestFiles();
        var a = files.Bytes("旧.pdf", AlignmentCliTests.CreatePage(false, false));
        var b = files.Bytes("新.pdf", AlignmentCliTests.CreatePage(false, true));
        var config = files.Text("設定.yaml", "dpi: 144\nalign: {enabled: true}\nreport: {raw_overlay: true}\nregions: [{name: 全面, page: all, x: 0, y: 0, w: 1000, h: 1000, mode: exclude}]");
        byte[]? raw = null;
        foreach (var audit in new[] { false, true })
        {
            var output = Path.Combine(files.Root, audit ? "audit" : "region");
            var run = await CliProcess.Run(["compare", a, b, "--out", output, "--config", config, .. (audit ? new[] { "--no-regions" } : Array.Empty<string>())]);
            Assert.Equal(0, run.Code); Assert.Empty(run.Error);
            var report = Read(output); var page = report.Pages[0];
            Assert.True(report.Config.Align.Enabled);
            if (audit) { Assert.Equal("applied", page.Alignment.Status); Assert.Equal(new PixelShift(-6, 4), page.GlobalShiftPx); }
            else { Assert.NotEqual("applied", page.Alignment.Status); Assert.Null(page.GlobalShiftPx); }
            var png = File.ReadAllBytes(Path.Combine(output, page.RawEvidence!.Overlay));
            if (raw is not null) Assert.Equal(raw, png); raw = png;
        }
    }

    [Fact]
    public async Task PdfTextUsesRegionalExclusionAndAuditRestoresWholeWords()
    {
        using var files = new DirectoryTestFiles();
        var a = files.Bytes("旧.pdf", PdfFixture.CreateTextPage([new("123", 50, 220)]));
        var b = files.Bytes("新.pdf", PdfFixture.CreateTextPage([new("828", 50, 220)]));
        var config = files.Text("設定.yaml", "regions: [{name: 文字の一部, page: all, x: 20.5, y: 25.5, w: 0.2, h: 1, mode: exclude}]");
        foreach (var audit in new[] { false, true })
        {
            var output = Path.Combine(files.Root, audit ? "audit" : "region");
            var run = await CliProcess.Run(["compare", a, b, "--out", output, "--config", config, .. (audit ? new[] { "--no-regions" } : Array.Empty<string>())]);
            Assert.Equal(1, run.Code); Assert.Empty(run.Error);
            var clusters = Read(output).Pages[0].Clusters; Assert.NotEmpty(clusters);
            Assert.All(clusters, c => { Assert.Equal(audit ? "123" : "", c.TextA); Assert.Equal(audit ? "828" : "", c.TextB); });
        }
    }

    [Fact]
    public async Task UnpairedAndNonApplicablePagesAreReportedWithoutInventingComparisons()
    {
        using var files = new DirectoryTestFiles();
        var a = files.Bytes("旧.pdf", PdfFixture.CreatePages((200, 200), (200, 200)));
        var b = files.Bytes("新.pdf", PdfFixture.CreatePages((200, 200)));
        var config = files.Text("設定.yaml", "regions: [{name: 片側, page: 2, x: 0, y: 0, w: 10, h: 10, profile: strict}]");
        var run = await CliProcess.Run("compare", a, b, "--out", files.Output, "--config", config, "--raw-overlay");
        Assert.Equal(1, run.Code); Assert.Empty(run.Error);
        var report = Read(files.Output);
        Assert.Equal("not_applicable", report.Pages[0].Regions!.Items[0].Status);
        Assert.Equal("not_compared", report.Pages[1].Regions!.Items[0].Status);
        Assert.All(report.Pages, p => { Assert.Empty(p.Regions!.Runs); Assert.Equal(0, p.Regions.SuppressedPixels); });
    }

    [Fact]
    public async Task InvalidRegionsFailEvenInAuditAndPreserveExistingOutput()
    {
        using var files = new DirectoryTestFiles();
        var a = files.Image("旧.png"); var b = files.Image("新.png", true);
        var config = files.Text("設定.yaml", RegionConfigurationTests.One.Replace("w: 10", "w: -1"));
        var sentinel = files.Text("比較 結果/old.txt", "旧結果");
        var run = await CliProcess.Run("compare", a, b, "--out", files.Output, "--config", config, "--no-regions", "--force");
        Assert.Equal(2, run.Code); Assert.Contains("regions[0].w", run.Error);
        Assert.Equal("旧結果", File.ReadAllText(sentinel)); Assert.Single(Directory.GetFiles(files.Output));
    }

    [Fact]
    public void SamePageRegionOverlayWriteFailurePreservesOldOutput()
    {
        using var files = new DirectoryTestFiles();
        var sentinel = files.Text("比較 結果/old.txt", "旧結果");
        var settings = ConfigurationLoader.Load(RegionConfigurationTests.One);
        using (var workspace = new OutputWorkspace(files.Output, true, []))
        {
            var inputs = new ReportInputs(new("A.png", "png", 1, ""), new("B.png", "png", 1, ""));
            var writer = new ReportWriter(workspace.StagingPath, inputs, settings.ToReportConfiguration());
            Directory.CreateDirectory(Path.Combine(workspace.StagingPath, "pages", "p001_overlay.png"));
            using var image = RegionCoreTests.White();
            using var pair = PageNormalizer.Normalize(image, image);
            using var comparison = PageComparer.Compare(pair.A, pair.B, settings.ForPage(1, 300));
            Assert.Equal("same", comparison.Status);
            Assert.Throws<ReportWriteException>(() => writer.AddComparedPage(1, pair, comparison, 300));
            Assert.Throws<ReportWriteException>(() => writer.Complete());
        }
        Assert.Equal("旧結果", File.ReadAllText(sentinel)); Assert.Single(Directory.GetFiles(files.Output));
        Assert.Empty(Directory.EnumerateDirectories(files.Root, ".reportdiff-*"));
    }

    private static ReportDocument Read(string path) => JsonSerializer.Deserialize<ReportDocument>(File.ReadAllText(Path.Combine(path, "result.json")), ReportJson.Options)!;
}
