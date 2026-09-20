using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Pdf;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

public sealed class CliTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessReturnsSameOrDifferentAndWritesUtf8SummaryAndReports(bool different)
    {
        using var files = new CliFiles();
        var a = files.Image("旧 帳票.dat");
        var b = different ? files.Image("新 帳票.dat", new Rect(20, 20, 10, 8)) : a;
        var result = await ProcessRun("compare", a, b, "--out", files.Output, "--profile", "strict");
        Assert.Equal(different ? 1 : 0, result.Code);
        Assert.Equal("", result.Error);
        Assert.StartsWith(different ? "相違あり:" : "相違なし:", result.Output);
        Assert.Single(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(Path.Combine(files.Output, "report.html"), result.Output);
        var report = files.ReadReport();
        Assert.Equal(different ? "different" : "same", report.Summary.Status);
        Assert.Equal("png", report.Inputs.A.Type); // 拡張子 .dat を使わない。
        Assert.Equal(a, report.Inputs.A.Path);
        Assert.Equal(different ? 1 : 0, report.Summary.Clusters);
        Assert.True(File.Exists(Path.Combine(files.Output, "report.html")));
        if (different)
        {
            var cluster = Assert.Single(report.Pages[0].Clusters);
            Assert.Equal(new PixelBox(20, 20, 10, 8), cluster.BboxPx);
            foreach (var path in new[] { report.Pages[0].Images.Overlay!, cluster.Crops.A, cluster.Crops.B, cluster.Crops.Diff })
                Assert.True(File.Exists(Path.Combine(files.Output, path)));
        }
        files.AssertNoTemporaryOutput();
    }

    [Fact]
    public async Task ProcessVersionMatchesReportVersionAndDoesNotLoadInputs()
    {
        var result = await ProcessRun("--version");
        Assert.Equal(0, result.Code); Assert.Empty(result.Error);
        Assert.Equal($"{ReportTool.Current.Name} {ReportTool.Current.Version}{Environment.NewLine}", result.Output);
    }

    [Fact]
    public async Task ProcessQuietSuppressesSummaryButKeepsJapaneseErrors()
    {
        using var files = new CliFiles();
        var input = files.Image("画像.png");
        var success = await ProcessRun("compare", input, input, "--out", files.Output, "--quiet", "--no-html");
        Assert.Equal(0, success.Code); Assert.Empty(success.Output); Assert.Empty(success.Error);
        Assert.False(File.Exists(Path.Combine(files.Output, "report.html")));
        var failure = await ProcessRun("compare", input, input, "--out", files.Output, "--quiet");
        AssertError(failure, "空ではありません");
    }

    [Fact]
    public async Task ProcessForceReplacesPreviousReportAndRemovesStaleImagesAndHtml()
    {
        using var files = new CliFiles();
        var a = files.Image("旧.png");
        var b = files.Image("新.png", new Rect(20, 20, 10, 8));
        Assert.Equal(1, Run("compare", a, b, "--out", files.Output, "--profile", "strict").Code);
        File.WriteAllText(Path.Combine(files.Output, "既存のファイル.txt"), "古い出力");
        var result = await ProcessRun("compare", a, a, "--out", files.Output, "--force", "--no-html");
        Assert.Equal(0, result.Code); Assert.Empty(result.Error);
        Assert.Contains("result.json", result.Output);
        Assert.Equal(new[] { "result.json" }, Directory.GetFileSystemEntries(files.Output).Select(Path.GetFileName));
        Assert.Equal("same", files.ReadReport().Summary.Status);
        files.AssertNoTemporaryOutput();
    }

    public static IEnumerable<object[]> InvalidArguments()
    {
        string[][] cases = [[], ["unknown"], ["--version", "--quiet"], ["compare"], ["compare", "a"],
            ["compare", "a", "b"], ["compare", "a", "b", "c", "--out", "out"],
            ["compare", "a", "b", "--out"], ["compare", "a", "b", "--out", ""],
            ["compare", "a", "b", "--out", "--quiet"], ["compare", "a", "b", "--out", "out", "--unknown"],
            ["compare", "a", "b", "--out", "out", "--out", "other"],
            ["compare", "a", "b", "--out", "out", "--force", "--force"],
            ["compare", "a", "b", "--out", "out", "--dpi", "3.5"],
            ["compare", "a", "b", "--out", "out", "--dpi", "999999999999"],
            ["compare", "a", "b", "--out", "out", "--dpi", "71"],
            ["compare", "a", "b", "--out", "out", "--dpi", "1201"],
            ["compare", "a", "b", "--out", "out", "--profile", "other"],
            ["compare", "a", "b", "--out", "out", "--config"],
            ["compare", "a", "b", "--out", "out", "--pages", "--quiet"]];
        return cases.Select(args => new object[] { args });
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void InvalidArgumentsReturnTwoWithJapaneseStderr(string[] args) => AssertError(Run(args));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveAllPagesControlsIdenticalImagesAndEmptyOutputIsAccepted(bool saveAll)
    {
        using var files = new CliFiles();
        var input = files.Image("同一.png");
        Directory.CreateDirectory(files.Output);
        var args = new List<string> { "compare", input, input, "--out", files.Output };
        if (saveAll) args.Add("--save-all-pages");
        var result = Run(args.ToArray());
        Assert.Equal(0, result.Code); Assert.Empty(result.Error);
        Assert.Equal(saveAll, files.ReadReport().Pages[0].Images.A is not null);
        Assert.Equal(saveAll, Directory.Exists(Path.Combine(files.Output, "pages")));
        files.AssertNoTemporaryOutput();
    }

    [Fact]
    public void ConfigProfileAndDpiPrecedenceAndImageMmConversionReachReports()
    {
        using var files = new CliFiles();
        var a = files.Image("旧.png");
        var b = files.Image("新.png", new Rect(20, 20, 10, 8));
        var config = files.Text("設定.yaml", """
            dpi: 144
            image_dpi: 200
            diff: { max_shift_mm: 0.8, edge_tolerance: 0.5, color_threshold: 2 }
            cluster: { merge_x_mm: 0, merge_y_mm: 0 }
            report: { crop_margin_mm: 1 }
            exclude:
              - { page: all, x: 0, y: 0, w: 1, h: 1, note: 日本語の設定 }
            """);
        var result = Run("compare", "--profile", "strict", a, "--dpi", "300", "--config", config, b, "--out", files.Output);
        Assert.Equal(1, result.Code); Assert.Empty(result.Error);
        var report = files.ReadReport();
        Assert.Equal(300, report.Config.Dpi); Assert.Equal(200, report.Config.ImageDpi);
        Assert.Equal(0, report.Config.Diff.MaxShiftMm); Assert.Equal(0, report.Config.Diff.EdgeTolerance);
        Assert.Equal(2, report.Config.Diff.ColorThreshold); Assert.Equal(1, report.Config.Report.CropMarginMm);
        Assert.Equal("日本語の設定", Assert.Single(report.Config.Exclude).Note);
        Assert.Equal(2.54, Assert.Single(report.Pages[0].Clusters).BboxMm.X, 8);
    }

    [Fact]
    public void OmittedProfileKeepsYamlAndExclusionActuallyRemovesDifference()
    {
        using var files = new CliFiles();
        var a = files.Image("旧.png");
        var b = files.Image("新.png", new Rect(20, 20, 10, 8));
        var config = files.Text("除外.yaml", """
            diff: { max_shift_mm: 0, edge_tolerance: 0 }
            exclude: [{page: all, x: 0, y: 0, w: 10, h: 10}]
            """);
        var result = Run("compare", a, b, "--config", config, "--dpi", "200", "--out", files.Output);
        Assert.Equal(0, result.Code); Assert.Empty(result.Error);
        var report = files.ReadReport();
        Assert.Equal(200, report.Config.Dpi); Assert.Equal(200, report.Config.ImageDpi);
        Assert.Equal(0, report.Config.Diff.EdgeTolerance);
        Assert.Equal(0, report.Pages[0].RawPixels);
    }

    [Fact]
    public void TooDifferentStillReturnsOneWithoutClusters()
    {
        using var files = new CliFiles();
        var a = files.Image("白.png");
        var b = files.Image("黒.png", new Rect(0, 0, 64, 64));
        Assert.Equal(1, Run("compare", a, b, "--out", files.Output).Code);
        var report = files.ReadReport();
        Assert.Equal("too_different", report.Pages[0].Status); Assert.Empty(report.Pages[0].Clusters);
        Assert.Contains(report.Warnings, w => w.Code == "TOO_DIFFERENT");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    public void PdfPageCountMismatchAndSelectedCommonPageReturnOne(bool reverse, bool commonOnly)
    {
        using var files = new CliFiles();
        var longer = files.Bytes("長い.dat", PdfFixture.CreatePages((72, 72), (72, 72)));
        var shorter = files.Bytes("短い.dat", PdfFixture.CreatePages((72, 72)));
        var result = Run("compare", reverse ? shorter : longer, reverse ? longer : shorter,
            "--out", files.Output, "--dpi", "72", "--pages", commonOnly ? "1" : "2,1-2");
        Assert.Equal(1, result.Code); Assert.Empty(result.Error);
        var report = files.ReadReport();
        Assert.Equal(commonOnly ? new[] { 1 } : [1, 2], report.Pages.Select(p => p.Page));
        Assert.Equal("same", report.Pages[0].Status);
        Assert.Equal(1, report.Summary.PagesCompared);
        Assert.Equal(commonOnly ? 0 : 1, report.Summary.PagesDifferent);
        Assert.Contains(report.Warnings, w => w.Code == "PAGE_COUNT_MISMATCH");
        if (!commonOnly) Assert.Equal(reverse ? "only_in_b" : "only_in_a", report.Pages[1].Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    public void MixedInputsRequireMatchingDpiInEitherOrder(bool reverse, bool mismatch)
    {
        using var files = new CliFiles();
        var pdf = files.Bytes("PDF.png", PdfFixture.CreatePages((72, 72)));
        using var reader = PdfReader.Open(pdf);
        using var page = reader.ReadPage(1, 72);
        Assert.True(Cv2.ImEncode(".png", page.Pixels, out var bytes));
        var png = files.Bytes("画像.pdf", bytes);
        var config = files.Text("設定.yaml", $"dpi: 72\nimage_dpi: {(mismatch ? 144 : 72)}\n");
        var result = Run("compare", reverse ? png : pdf, reverse ? pdf : png, "--out", files.Output, "--config", config);
        if (mismatch)
        {
            AssertError(result, "同じ値"); Assert.False(Directory.Exists(files.Output));
        }
        else
        {
            Assert.Equal(0, result.Code); Assert.Empty(result.Error);
            Assert.Equal("same", files.ReadReport().Summary.Status);
            Assert.Contains(files.ReadReport().Warnings, w => w.Code == "MIXED_INPUT_TYPES");
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("format")]
    [InlineData("config")]
    [InlineData("pages")]
    [InlineData("utf8")]
    [InlineData("decode")]
    public void ErrorsDoNotReplaceExistingOutputEvenWithForce(string kind)
    {
        using var files = new CliFiles();
        var input = files.Image("入力.png");
        var args = new List<string> { "compare", input, input, "--out", files.Output, "--force", "--quiet" };
        switch (kind)
        {
            case "missing": args[1] = Path.Combine(files.Root, "不存在.png"); break;
            case "format": args[1] = files.Text("不明.txt", "画像ではない"); break;
            case "config": args.AddRange(["--config", files.Text("不正.yaml", "wrong_key: 2")]); break;
            case "pages": args.AddRange(["--pages", "2"]); break;
            case "utf8": args.AddRange(["--config", files.Bytes("文字化け.yaml", [0xff, 0xff])]); break;
            case "decode": args[1] = files.Bytes("壊れた.png", [137, 80, 78, 71, 13, 10, 26, 10]); break;
        }
        Directory.CreateDirectory(files.Output);
        var sentinel = Path.Combine(files.Output, "旧結果.txt");
        File.WriteAllText(sentinel, "残す");
        AssertError(Run(args.ToArray()));
        Assert.Equal("残す", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(files.Output));
        files.AssertNoTemporaryOutput();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    public void LaterPdfPageFailureLeavesNoPartialReportAndPreservesOldOutput(bool previous)
    {
        using var files = new CliFiles();
        var input = files.Bytes("巨大ページ.pdf", PdfFixture.CreatePages((72, 72), (17000, 72)));
        if (previous) { Directory.CreateDirectory(files.Output); File.WriteAllText(Path.Combine(files.Output, "旧.txt"), "残す"); }
        AssertError(Run("compare", input, input, "--out", files.Output, "--dpi", "72", "--force", "--save-all-pages"), "dpi を下げて");
        Assert.False(File.Exists(Path.Combine(files.Output, "result.json")));
        Assert.Equal(previous, Directory.Exists(files.Output));
        if (previous) Assert.Equal("残す", File.ReadAllText(Path.Combine(files.Output, "旧.txt")));
        files.AssertNoTemporaryOutput();
    }

    [Fact]
    public void FileAsOutputAndFileAsParentAreErrors()
    {
        using var files = new CliFiles();
        var input = files.Image("入力.png");
        var blocked = files.Text("ファイル.txt", "そのまま");
        foreach (var output in new[] { blocked, Path.Combine(blocked, "結果") })
            AssertError(Run("compare", input, input, "--out", output, "--force"));
        Assert.Equal("そのまま", File.ReadAllText(blocked));
    }

    [Fact]
    public void InputAndConfigInsideOutputCannotBeDeleted()
    {
        using var files = new CliFiles();
        var input = files.Image("入力.png");
        var config = files.Text("設定.yaml", "dpi: 72");
        AssertError(Run("compare", input, input, "--out", files.Root, "--force"), "含めることはできません");
        using var others = new CliFiles();
        var external = others.Image("外.png");
        AssertError(Run("compare", external, external, "--out", files.Root, "--force", "--config", config), "含めることはできません");
        Assert.True(File.Exists(input)); Assert.True(File.Exists(config));
    }

    [Fact]
    public void OutputPrefixIsNotConfusedWithAncestor()
    {
        using var files = new CliFiles();
        var input = files.Image("結果-old.png");
        Assert.Equal(0, Run("compare", input, input, "--out", Path.Combine(files.Root, "結果")).Code);
        Assert.True(File.Exists(input));
    }

    private static CliResult Run(params string[] args)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var code = CliApplication.Run(args, output, error);
        return new(code, output.ToString(), error.ToString());
    }

    private static Task<CliResult> ProcessRun(params string[] args) => CliProcess.Run(args);

    private static void AssertError(CliResult result, string? text = null)
    {
        Assert.Equal(2, result.Code); Assert.Empty(result.Output); Assert.StartsWith("エラー: ", result.Error);
        Assert.Matches("[ぁ-んァ-ヶ一-龠]", result.Error);
        Assert.DoesNotContain(" at ", result.Error);
        if (text is not null) Assert.Contains(text, result.Error);
    }

    private sealed class CliFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "reportdiff-CLI 試験-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Root, "比較 結果");
        public CliFiles() => Directory.CreateDirectory(Root);
        public string Image(string name, Rect? change = null)
        {
            using var image = new Mat(64, 64, MatType.CV_8UC3, Scalar.All(255));
            if (change is not null) Cv2.Rectangle(image, change.Value, Scalar.All(0), -1);
            Assert.True(Cv2.ImEncode(".png", image, out var bytes));
            return Bytes(name, bytes);
        }
        public string Text(string name, string text) => Bytes(name, Encoding.UTF8.GetBytes(text));
        public string Bytes(string name, byte[] bytes)
        {
            var path = Path.Combine(Root, name); File.WriteAllBytes(path, bytes); return path;
        }
        public ReportDocument ReadReport() => JsonSerializer.Deserialize<ReportDocument>(File.ReadAllBytes(Path.Combine(Output, "result.json")), ReportJson.Options)!;
        public void AssertNoTemporaryOutput() => Assert.Empty(Directory.EnumerateFileSystemEntries(Root, ".reportdiff-*"));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
