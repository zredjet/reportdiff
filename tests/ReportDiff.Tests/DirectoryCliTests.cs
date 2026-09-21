using System.Text.Json;
using System.Text.Json.Nodes;
using ReportDiff.Cli;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

public sealed class DirectoryCliTests
{
    [Fact]
    public async Task MixedBatchContinuesReportsUnpairedPresenceAndKeepsOnlySuccessfulChildren()
    {
        using var files = new DirectoryTestFiles();
        files.Image("A/.hidden/同じ.PNG"); files.Image("B/.hidden/同じ.png");
        files.Image("A/変更.png"); files.Image("B/変更.png", true);
        files.Text("A/Aだけ.pdf", "開くと失敗するが、片側のみなら存在差分");
        files.Text("B/Bだけ.tiff", "同上");
        files.Text("A/壊れた.png", "broken"); files.Image("B/壊れた.png");
        files.Text("A/readme.txt", "ignored"); files.Text("B/readme.txt", "ignored");
        var process = await files.Run("--profile", "strict");
        Assert.Equal(2, process.Code); Assert.Single(process.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("壊れた.png", process.Error);
        var result = files.Read();
        Assert.Equal(new DirectorySummary("error", 5, 2, 1, 1, 1, 1, 1, 2), result.Summary);
        Assert.Equal("directory_comparison", result.ReportType); Assert.Equal(1, result.SchemaVersion);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(files.Output, "files")).Length);
        Assert.All(result.Files.Where(x => x.Comparison is null), x => { Assert.Null(x.WarningCount); Assert.Null(x.Json); Assert.Null(x.Html); });
        Assert.All(result.Files.Where(x => x.Json is not null), x => { Assert.True(File.Exists(Path.Combine(files.Output, x.Json!))); Assert.True(File.Exists(Path.Combine(files.Output, x.Html!))); });
        Assert.DoesNotContain(Directory.EnumerateDirectories(files.Root), x => Path.GetFileName(x).StartsWith(".reportdiff-"));
        Assert.Contains("内容は検査していません", File.ReadAllText(Path.Combine(files.Output, "index.html")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrIgnoredOnlyIsErrorWithSavedIndex(bool ignored)
    {
        using var files = new DirectoryTestFiles();
        if (ignored) files.Text("A/notes.txt", "ignored");
        var process = await files.Run("--quiet", "--no-html");
        Assert.Equal(2, process.Code); Assert.Empty(process.Output); Assert.Contains("比較対象", process.Error);
        var report = files.Read(); Assert.Equal("error", report.Summary.Status); Assert.Equal(0, report.Summary.Total);
        Assert.Equal("NO_TARGET_FILES", Assert.Single(report.Warnings).Code);
        Assert.Equal(ignored ? 1 : 0, report.Summary.Ignored);
        Assert.Equal(new[] { "index.json" }, Directory.GetFiles(files.Output).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("same", 0)]
    [InlineData("different", 1)]
    [InlineData("only_in_a", 1)]
    [InlineData("only_in_b", 1)]
    public async Task StatusExitCodesAndNoHtmlSaveAllPages(string status, int expected)
    {
        using var files = new DirectoryTestFiles();
        if (status != "only_in_b") files.Image("A/入力.png");
        if (status != "only_in_a") files.Image("B/入力.png", status == "different");
        var process = await files.Run("--quiet", "--no-html", "--save-all-pages", "--profile", "strict");
        Assert.Equal(expected, process.Code); Assert.Empty(process.Output); Assert.Empty(process.Error);
        var row = Assert.Single(files.Read().Files); Assert.Equal(status, row.Status); Assert.Null(row.Html);
        Assert.Empty(Directory.GetFiles(files.Output, "*.html", SearchOption.AllDirectories));
        if (row.Json is not null)
        {
            var page = Assert.Single(files.Child(row).Pages); Assert.NotNull(page.Images.A);
            Assert.True(File.Exists(Path.Combine(files.Output, Path.GetDirectoryName(row.Json)!, page.Images.A!)));
        }
    }

    [Fact]
    public async Task SelectedLayerMatchesStandaloneCompareIncludingEffectiveSettingsAndImages()
    {
        using var files = new DirectoryTestFiles();
        var a = files.Image("A/請求/帳票.png"); var b = files.Image("B/請求/帳票.png", true);
        var common = files.Text("common.yaml", "dpi: 144\ndiff: {color_threshold: 8}\nexclude: [{page: all, x: 0, y: 0, w: 20, h: 20}]");
        files.Text("selected.yaml", "dpi: 200\nexclude: []\nreport: {crop_margin_mm: 1}");
        var rules = files.Text("rules.yaml", "schema_version: 1\nrules: [{pattern: '請求/', config: selected.yaml}]");
        Assert.Equal(1, (await files.Run("--config", common, "--rules", rules, "--profile", "strict", "--dpi", "300")).Code);
        var report = files.Read(); var row = Assert.Single(report.Files); var child = files.Child(row);
        Assert.Equal(1, row.SelectedRule!.Index); Assert.Equal(300, child.Config.ImageDpi); Assert.Empty(child.Config.Exclude);
        Assert.Equal(8, child.Config.Diff.ColorThreshold); Assert.Equal(1, child.Config.Report.CropMarginMm);
        Assert.Equal(3, new[] { report.Configuration.Common, report.Configuration.Rules, report.Configuration.Referenced.Single() }.Count(x => x!.Sha256.Length == 64));
        var effective = files.Text("effective.yaml", "dpi: 300\ndiff: {color_threshold: 8}\nreport: {crop_margin_mm: 1}");
        var single = Path.Combine(files.Root, "単一結果");
        Assert.Equal(1, (await CliProcess.Run("compare", a, b, "--out", single, "--config", effective, "--profile", "strict")).Code);
        var actual = JsonNode.Parse(File.ReadAllText(Path.Combine(files.Output, row.Json!)))!.AsObject();
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(single, "result.json")))!.AsObject();
        actual.Remove("generated_at"); expected.Remove("generated_at");
        Assert.True(JsonNode.DeepEquals(expected, actual));
        foreach (var png in Directory.GetFiles(single, "*.png", SearchOption.AllDirectories))
            Assert.Equal(File.ReadAllBytes(png), File.ReadAllBytes(Path.Combine(files.Output, Path.GetDirectoryName(row.Json!)!, Path.GetRelativePath(single, png))));
    }

    [Fact]
    public async Task AmbiguousRulesArePerPairErrorsEvenWhenTheyReferenceSameConfig()
    {
        using var files = new DirectoryTestFiles();
        files.Image("A/ambiguous.png"); files.Image("B/AMBIGUOUS.png");
        files.Image("A/ok.png"); files.Image("B/ok.png"); files.Text("A/ambiguous-only.pdf", "unpaired");
        files.Text("settings.yaml", "{}");
        var rules = files.Text("rules.yaml", "schema_version: 1\nrules:\n- {pattern: ambiguous, config: settings.yaml}\n- {pattern: '(?-i:AMBIGUOUS|ambiguous)', config: settings.yaml}");
        Assert.Equal(2, (await files.Run("--rules", rules)).Code);
        var result = files.Read(); Assert.Equal(1, result.Summary.Compared); Assert.Equal(1, result.Summary.Error);
        var failure = Assert.Single(result.Files, x => x.Error is not null);
        Assert.Equal("CONFIG_RULE_AMBIGUOUS", failure.Error!.Code); Assert.Equal(2, failure.Error.MatchedRules.Count);
        Assert.Null(Assert.Single(result.Files, x => x.Status == "only_in_a").SelectedRule);
    }

    [Fact]
    public async Task LaterPdfPageFailureRemovesPartialChildAndForceCommitsErrorBatch()
    {
        using var files = new DirectoryTestFiles();
        var bytes = PdfFixture.CreatePages((72, 72), (17000, 72));
        files.Bytes("A/first.pdf", bytes); files.Bytes("B/first.pdf", bytes);
        files.Image("A/next.png"); files.Image("B/next.png");
        files.Text("比較 結果/old.txt", "旧結果");
        var process = await files.Run("--dpi", "72", "--save-all-pages", "--force");
        Assert.Equal(2, process.Code); Assert.False(File.Exists(Path.Combine(files.Output, "old.txt")));
        var report = files.Read(); Assert.Equal(1, report.Summary.Error); Assert.Equal(1, report.Summary.Same);
        var failed = Assert.Single(report.Files, x => x.Status == "error");
        Assert.False(Directory.Exists(Path.Combine(files.Output, "files", failed.Id))); Assert.Null(failed.Json);
        Assert.Contains("dpi を下げて", process.Error);
    }

    [Fact]
    public async Task PdfSniffingPagesAndFontWarningsRemainInIndividualReport()
    {
        using var files = new DirectoryTestFiles();
        var bytes = PdfFixture.CreateFontReport(contents: [PdfFixture.FontText, PdfFixture.FontText]);
        files.Bytes("A/実体PDF.png", bytes); files.Bytes("B/実体PDF.png", bytes);
        Assert.Equal(0, (await files.Run("--dpi", "72", "--pages", "2")).Code);
        var row = Assert.Single(files.Read().Files); Assert.Equal(2, row.WarningCount);
        var child = files.Child(row); Assert.Equal("pdf", child.Inputs.A.Type); Assert.Equal(2, Assert.Single(child.Pages).Page);
        Assert.All(child.Warnings, x => Assert.Equal("NON_EMBEDDED_FONT", x.Code));
    }

    [Fact]
    public async Task MixedDpiAndOutOfRangePagesAreItemErrors()
    {
        using var files = new DirectoryTestFiles();
        files.Bytes("A/input.png", PdfFixture.CreatePages((72, 72))); files.Image("B/input.png");
        var config = files.Text("config.yaml", "dpi: 72\nimage_dpi: 144");
        var mismatch = await files.Run("--config", config);
        Assert.Equal(2, mismatch.Code); Assert.Contains("同じ値", mismatch.Error);
        var pages = await files.Run("--pages", "3", "--force");
        Assert.Equal(2, pages.Code); Assert.Single(files.Read().Files, x => x.Status == "error");
    }

    [Theory]
    [InlineData("invalid-config")]
    [InlineData("unused-config")]
    [InlineData("protected-common")]
    [InlineData("protected-rules")]
    [InlineData("protected-reference")]
    [InlineData("nonempty")]
    public async Task PreflightFailurePreservesOldOutput(string reason)
    {
        using var files = new DirectoryTestFiles();
        files.Image("A/a.png"); files.Image("B/a.png");
        var sentinel = files.Text("比較 結果/old.txt", "残す");
        var args = new List<string>(); if (reason != "nonempty") args.Add("--force");
        if (reason is "invalid-config" or "protected-common") args.AddRange(["--config", files.Text(reason == "protected-common" ? "比較 結果/config.yaml" : "config.yaml", reason == "invalid-config" ? "dpi: 0" : "{}")]);
        if (reason is "unused-config" or "protected-reference" or "protected-rules")
        {
            var referenced = files.Text(reason == "protected-reference" ? "比較 結果/config.yaml" : "config.yaml", reason == "unused-config" ? "dpi: 0" : "{}");
            var rules = files.Text(reason == "protected-rules" ? "比較 結果/rules.yaml" : "rules.yaml", $"schema_version: 1\nrules: [{{pattern: never, config: '{referenced}'}}]");
            args.AddRange(["--rules", rules]);
        }
        Assert.Equal(2, (await files.Run(args.ToArray())).Code); Assert.Equal("残す", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(files.Output, "index.json")));
    }

    [Theory]
    [InlineData("enumeration")]
    [InlineData("child-output")]
    [InlineData("index-output")]
    public void InjectedWholeBatchFailuresPreservePreviousOutput(string failure)
    {
        using var files = new DirectoryTestFiles();
        files.Image("A/a.png"); files.Image("B/a.png"); var sentinel = files.Text("比較 結果/old.txt", "残す");
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        IEnumerable<DirectoryEntry> Enumerate(string _)
        {
            yield return new("a.png", false);
            throw new IOException("列挙途中の失敗を注入");
        }
        ReportDocument Compare(CompareCommand command, AppSettings settings, string child)
        {
            Directory.CreateDirectory(child);
            if (failure == "child-output")
            {
                File.WriteAllText(Path.Combine(child, "pages"), "ディレクトリ作成を妨げるファイル");
                if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                    return ComparisonRunner.Compare(command, settings, child);
            }
            var stage = Directory.GetParent(child)!.Parent!.FullName;
            File.WriteAllText(Path.Combine(stage, "index.json"), "一覧出力を妨げるファイル");
            return new(1, ReportTool.Current, DateTimeOffset.UtcNow, null!, settings.ToReportConfiguration(), new("same", 1, 0, 0, 0), [], []);
        }
        Assert.ThrowsAny<Exception>(() => DirectoryComparison.Run(files.Command with { Force = true }, stdout, stderr,
            compare: Compare, enumerate: failure == "enumeration" ? Enumerate : null));
        Assert.Equal("残す", File.ReadAllText(sentinel)); Assert.False(File.Exists(Path.Combine(files.Output, "index.json")));
        Assert.Empty(Directory.EnumerateDirectories(files.Root, ".reportdiff-*"));
    }

    [Fact]
    public void HtmlEscapesNamesAndNeverLinksFailedRows()
    {
        var file = new DirectoryFileResult("f000001", "<script>&\".pdf", null, "<x>.pdf", "only_in_b", null, null, null, null, null, null);
        var doc = new DirectoryReportDocument(1, "directory_comparison", ReportTool.Current, DateTimeOffset.UtcNow,
            new("<A>", "<B>"), new(null, null, [], new(null, null, null, false, false, false, false)),
            new("different", 1, 0, 0, 0, 0, 1, 0, 0), [file], [], []);
        var html = DirectoryReportWriter.Render(doc);
        Assert.DoesNotContain("<script>", html); Assert.Contains("&lt;script&gt;&amp;&quot;.pdf", html);
        Assert.DoesNotContain("href=\"files/", html); Assert.Contains("未比較", html);
        Assert.Throws<ArgumentException>(() => DirectoryReportWriter.Render(doc with { Files = [file with { Html = "https://example.com" }] }));
    }
}
