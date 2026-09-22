using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

public sealed class RawOverlayColorTests
{
    [Theory]
    [InlineData(0, 0, "#CCCCCC", 204, 204, 204)]
    [InlineData(128, 128, "#CCCCCC", 230, 230, 230)]
    [InlineData(255, 255, "#CCCCCC", 255, 255, 255)]
    [InlineData(0, 255, "#CCCCCC", 255, 0, 0)]
    [InlineData(255, 0, "#CCCCCC", 0, 0, 255)]
    [InlineData(0, 128, "#CCCCCC", 230, 102, 102)]
    [InlineData(128, 0, "#CCCCCC", 102, 102, 230)]
    [InlineData(0, 0, "#123456", 18, 52, 86)]
    [InlineData(128, 64, "#123456", 73, 90, 171)]
    [InlineData(0, 128, "#FFFFFF", 255, 127, 127)]
    public void CommonToneChangesWhileWhiteAndExclusiveContentKeepTheirColors(int ga, int gb, string color, int r, int g, int b)
    {
        using var a = new Mat(1, 1, MatType.CV_8UC3, Scalar.All(ga));
        using var inputB = new Mat(1, 1, MatType.CV_8UC3, Scalar.All(gb));
        using var actual = RawOverlay.Create(a, inputB, color);
        Assert.Equal(new Vec3b((byte)b, (byte)g, (byte)r), actual.At<Vec3b>(0, 0));
    }

    [Theory]
    [InlineData("#CCCCCC")] [InlineData("#123456")] [InlineData("#FFFFFF")]
    public void AllGrayPairsMatchInkDecompositionOnNoncontinuousInput(string color)
    {
        using var parent = new Mat(258, 258, MatType.CV_8UC3, Scalar.All(255));
        using var a = new Mat(parent, new Rect(1, 1, 256, 256));
        using var b = new Mat(256, 256, MatType.CV_8UC3);
        for (var y = 0; y < 256; y++) for (var x = 0; x < 256; x++)
        { a.Set(y, x, new Vec3b((byte)y, (byte)y, (byte)y)); b.Set(y, x, new Vec3b((byte)x, (byte)x, (byte)x)); }
        using var actual = RawOverlay.Create(a, b, color);
        using var reverse = RawOverlay.Create(b, a, color);
        var rgb = Convert.FromHexString(color[1..]);
        for (var y = 0; y < 256; y++) for (var x = 0; x < 256; x++)
        {
            var inkA = 255 - y; var inkB = 255 - x; var common = Math.Min(inkA, inkB);
            var onlyA = inkA - common; var onlyB = inkB - common;
            byte Channel(int sideInk, int pigment) => (byte)((65025 - sideInk * 255 - common * (255 - pigment) + 127) / 255);
            var pixel = actual.At<Vec3b>(y, x); var swapped = reverse.At<Vec3b>(y, x);
            Assert.Equal(new Vec3b(Channel(onlyA, rgb[2]), Channel(onlyA + onlyB, rgb[1]), Channel(onlyB, rgb[0])), pixel);
            Assert.Equal(x - y, pixel.Item2 - swapped.Item2);
            Assert.Equal(y - x, pixel.Item0 - swapped.Item0);
            Assert.Equal(pixel.Item1, swapped.Item1);
        }
    }

    [Theory]
    [InlineData("null")] [InlineData("[]")] [InlineData("{}")] [InlineData("true")] [InlineData("123456")]
    [InlineData("''")] [InlineData("'#fff'")] [InlineData("'#12345678'")] [InlineData("'#GG0000'")]
    [InlineData("' #123456'")] [InlineData("'#123456 '")] [InlineData("'#123456; color:red'")]
    public void InvalidColorIsRejectedEvenWhenOverlayIsDisabled(string value)
    {
        var exception = Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load("report: {raw_overlay: false, raw_overlay_common_color: " + value + "}"));
        Assert.Contains("report.raw_overlay_common_color", exception.Message);
    }

    [Fact]
    public void ColorDefaultsLayersProfilesAndCliAreIndependentOfEnablement()
    {
        const string common = "report: {raw_overlay_common_color: '#aBcDeF'}";
        var defaults = ConfigurationLoader.Load(); Assert.Equal("#CCCCCC", defaults.Report.RawOverlayCommonColor);
        Assert.Equal("#CCCCCC", defaults.ToReportConfiguration().Report.RawOverlayCommonColor);
        foreach (var profile in new[] { "normal", "strict", "loose" })
        {
            var inherited = ConfigurationLoader.LoadLayered(common, "report: {raw_overlay: false}", profile, 400);
            Assert.Equal("#ABCDEF", inherited.Report.RawOverlayCommonColor);
            var command = CommandLine.Parse(["compare", "a", "b", "--out", "out", "--raw-overlay"]);
            Assert.Equal("#ABCDEF", command.ApplyReportOptions(inherited).ToReportConfiguration().Report.RawOverlayCommonColor);
        }
        Assert.Equal("#000000", ConfigurationLoader.LoadLayered(common, "report: {raw_overlay_common_color: '#000000'}").Report.RawOverlayCommonColor);
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.LoadLayered("report: {raw_overlay_common_color: bad}", common));
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load("report:\n  raw_overlay_common_color: #CCCCCC\n"));
    }

    [Fact]
    public async Task PdfColorFlowsIntoPngJsonAndHtmlWhileExistingResultsStayIdentical()
    {
        using var files = new DirectoryTestFiles();
        var input = files.Bytes("帳票.pdf", PdfFixture.CreatePages((72, 80), (72, 80)));
        var reports = new List<JsonNode>();
        var images = new List<Dictionary<string, byte[]>>();
        foreach (var color in new[] { "#000000", "#CCCCCC", "#123456" })
        {
            var config = files.Text("色.yaml", $"dpi: 72\nreport: {{raw_overlay: true, raw_overlay_common_color: '{color}'}}");
            var output = Path.Combine(files.Root, color[1..]);
            var result = await CliProcess.Run("compare", input, input, "--config", config, "--out", output, "--save-all-pages");
            Assert.Equal(0, result.Code); Assert.Empty(result.Error);
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "result.json")))!;
            Assert.Equal(color, node["config"]!["report"]!["raw_overlay_common_color"]!.GetValue<string>());
            var html = File.ReadAllText(Path.Combine(output, "report.html"));
            Assert.Contains("共通：" + color, html); Assert.Contains("background:" + color, html);
            foreach (var page in node["pages"]!.AsArray())
            {
                var evidence = page!["raw_evidence"]!;
                Assert.Equal(color, evidence["common_color"]!.GetValue<string>());
                using var a = Read(evidence["a"]!["image"]!.GetValue<string>());
                using var b = Read(evidence["b"]!["image"]!.GetValue<string>());
                using var overlay = Read(evidence["overlay"]!.GetValue<string>());
                using var expected = RawOverlay.Create(a, b, color);
                Assert.Equal(0, Cv2.Norm(expected, overlay, NormTypes.INF));
                Mat Read(string path) => Cv2.ImDecode(File.ReadAllBytes(Path.Combine(output, path)), ImreadModes.Color);
                evidence.AsObject().Remove("common_color");
            }
            node.AsObject().Remove("generated_at"); node["config"]!["report"]!.AsObject().Remove("raw_overlay_common_color");
            reports.Add(node);
            images.Add(Directory.GetFiles(output, "*.png", SearchOption.AllDirectories).Where(p => !p.EndsWith("_raw_overlay.png", StringComparison.Ordinal))
                .ToDictionary(p => Path.GetRelativePath(output, p), File.ReadAllBytes));
        }
        foreach (var report in reports) Assert.True(JsonNode.DeepEquals(reports[0], report));
        foreach (var set in images) foreach (var (path, bytes) in images[0]) Assert.Equal(bytes, set[path]);
    }

    [Fact]
    public async Task DirectoryRulesSelectColorAndNoHtmlStillWritesPng()
    {
        using var files = new DirectoryTestFiles();
        files.Image("A/a.png", true); files.Image("B/a.png", true);
        files.Image("A/b.png", true); files.Image("B/b.png", true);
        var config = files.Text("共通.yaml", "report: {raw_overlay: true, raw_overlay_common_color: '#CCCCCC'}");
        files.Text("色.yaml", "report: {raw_overlay_common_color: '#123456'}");
        var rules = files.Text("選択.yaml", "schema_version: 1\nrules: [{pattern: '^a', config: 色.yaml}]");
        var result = await files.Run("--config", config, "--rules", rules, "--no-html");
        Assert.Equal(0, result.Code); Assert.Empty(result.Error);
        foreach (var item in files.Read().Files)
        {
            var report = files.Child(item); var page = Assert.Single(report.Pages); var evidence = page.RawEvidence!;
            var color = item.RelativePath == "a.png" ? "#123456" : "#CCCCCC";
            Assert.Equal(color, report.Config.Report.RawOverlayCommonColor); Assert.Equal(color, evidence.CommonColor);
            using var image = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(files.Output, Path.GetDirectoryName(item.Json!)!, evidence.Overlay)), ImreadModes.Color);
            var rgb = Convert.FromHexString(color[1..]); Assert.Equal(new Vec3b(rgb[2], rgb[1], rgb[0]), image.At<Vec3b>(22, 22));
        }
        Assert.Empty(Directory.GetFiles(files.Output, "*.html", SearchOption.AllDirectories));
    }

    [Fact]
    public void OldEvidenceDefaultsToBlackAndColorCannotInjectCss()
    {
        const string old = """{"method":"grayscale_red_blue_v1","coordinate_system":"original_top_left","dpi":300,"canvas_size_px":{"w":1,"h":1},"a":{"missing":true,"image":"a.png"},"b":{"missing":true,"image":"b.png"},"overlay":"o.png"}""";
        var evidence = JsonSerializer.Deserialize<RawEvidence>(old, ReportJson.Options)!;
        Assert.Equal("#000000", evidence.CommonColor);
        Assert.Throws<ArgumentException>(() => evidence with { CommonColor = "red;display:none" });
        Assert.Throws<ArgumentException>(() => new ReportOutputOptions(2) { RawOverlayCommonColor = "red" });
    }
}
