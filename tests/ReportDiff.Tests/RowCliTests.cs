using System.Runtime.Versioning;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class RowCliTests
{
    [Theory]
    [InlineData("R01", false, 2)] [InlineData("R01", true, 2)]
    [InlineData("R02", false, 3)] [InlineData("R02", true, 3)]
    [InlineData("R04", false, 4)] [InlineData("R04", true, 4)]
    [InlineData("R05", false, 2)] [InlineData("R05", true, 2)]
    [InlineData("R06", false, 2)] [InlineData("R06", true, 2)]
    [InlineData("R07", false, 6)] [InlineData("R07", true, 6)]
    public async Task Rows_flow_through_cli_json_html_and_raw_evidence(string id, bool reverse, int expected)
    {
        using var files = new Files(id, reverse);
        var run = await files.Run("rows"); Assert.Equal(1, run.Code); Assert.Empty(run.Error);
        Assert.Contains($"{expected} 箇所", run.Output); Assert.Contains(reverse ? "行の削除" : "行の挿入", run.Output);
        var report = files.Read("rows"); var page = Assert.Single(report.Pages);
        Assert.True(report.Config.Rows.Enabled); Assert.Equal(expected, report.Summary.DifferenceCount);
        Assert.Equal("applied", page.RowAlignment.Status); Assert.Equal("pdf_text", page.RowAlignment.Source);
        Assert.Equal(expected, page.DifferenceCount); Assert.Equal(page.Clusters.Count, report.Summary.Clusters);
        Assert.Equal(expected - page.Clusters.Count, page.StructuralChangeCount); Assert.True(page.DifferenceCountComplete);
        Assert.Equal(page.StructuralChangeCounts, report.Summary.StructuralChangeCounts);
        Assert.Contains(report.Warnings, w => w.Code == "ROW_ALIGNMENT_APPLIED");
        Assert.Null(page.RowAlignment.OmissionAudit!.OmittedCandidateRawPixels);
        Assert.All(page.RowAlignment.StructuralChanges.Where(c => c.Kind != "block_moved"), c =>
        {
            Assert.NotEmpty(reverse ? c.TextA! : c.TextB!);
            Assert.Equal("", reverse ? c.TextB : c.TextA);
        });
        if (id == "R02")
        {
            var c = Assert.Single(page.Clusters);
            Assert.Equal(reverse ? "111" : "555", c.TextA); Assert.Equal(reverse ? "555" : "111", c.TextB);
            Assert.NotNull(c.ContentBboxPx); Assert.NotEmpty(c.DisplayPartsPx!); Assert.NotNull(c.SourceA); Assert.NotNull(c.SourceB);
        }
        foreach (var path in new[] { page.Images.A, page.Images.B, page.Images.Overlay, page.Images.ContentA, page.Images.ContentB })
            Assert.True(File.Exists(files.Output("rows", path!)));
        using var ca = files.Image("rows", page.Images.ContentA!); using var da = files.Image("rows", page.Images.A!);
        Assert.Equal(page.RowAlignment.ContentCanvas!.SizePx.H, ca.Height); Assert.Equal(page.SizePx.H, da.Height);
        Assert.True(da.Height > ca.Height);
        var html = File.ReadAllText(files.Output("rows", "report.html"));
        Assert.Contains("行の構造変化", html); Assert.Contains("内容比較に使った画像", html);
        Assert.Contains("白い詰め物", html); Assert.Contains("S1", html);
        var baselineRun = await files.Run("baseline", "rows: {enabled: false}\nreport: {raw_overlay: true}", noHtml: true);
        Assert.Equal(1, baselineRun.Code);
        var before = Assert.Single(files.Read("baseline").Pages);
        foreach (var (oldPath, newPath) in new[] { (before.RawEvidence!.Overlay, page.RawEvidence!.Overlay),
                     (before.RawEvidence.A.Image, page.RawEvidence.A.Image), (before.RawEvidence.B.Image, page.RawEvidence.B.Image) })
            Assert.Equal(File.ReadAllBytes(files.Output("baseline", oldPath)), File.ReadAllBytes(files.Output("rows", newPath)));
        Assert.NotEqual(page.Images.A, page.RawEvidence.A.Image); Assert.NotEqual(page.Images.B, page.RawEvidence.B.Image);
        if (id == "R02" && !reverse && Environment.GetEnvironmentVariable("REPORTDIFF_ROW_REPORT_DIR") is { } folder)
            CopyDirectory(files.Output("rows"), folder);
    }

    [Theory]
    [InlineData("R08", "no_text")]
    [InlineData("R09", "column_conflict")]
    public async Task Skipped_rows_keep_masks_images_and_raw_evidence(string id, string reason)
    {
        using var files = new Files(id);
        var first = await files.Run("on", noHtml: true); var second = await files.Run("off", "rows: {enabled: false}\nreport: {raw_overlay: true}", noHtml: true);
        Assert.Equal(second.Code, first.Code); Assert.Equal(1, first.Code);
        var on = files.Read("on").Pages[0]; var off = files.Read("off").Pages[0];
        Assert.Equal(reason, on.RowAlignment.Reason); Assert.Equal(off.RawPixels, on.RawPixels); Assert.Equal(off.Clusters.Count, on.Clusters.Count);
        Assert.Null(on.Images.ContentA); Assert.Equal(off.Status, on.Status);
        foreach (var path in Directory.EnumerateFiles(files.Output("off"), "*.png", SearchOption.AllDirectories))
            Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(files.Output("on", Path.GetRelativePath(files.Output("off"), path))));
        Assert.DoesNotContain(files.Read("on").Warnings, w => w.Code == "ROW_ALIGNMENT_APPLIED");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Exclusion_yaml_returns_to_source_A_and_reexecution_keeps_structural_counts(bool reverse)
    {
        using var files = new Files("R02", reverse);
        Assert.Equal(1, (await files.Run("before")).Code);
        var page = files.Read("before").Pages[0]; var c = Assert.Single(page.Clusters);
        var yaml = ExclusionSnippet.Create(page, c, 300, 1);
        var run = await files.Run("after", "rows: {enabled: true}\nexclude:\n" + string.Join('\n', yaml.Split('\n').Select(s => "  " + s)), noHtml: true);
        Assert.Equal(1, run.Code); Assert.Empty(run.Error); Assert.False(File.Exists(files.Output("after", "report.html")));
        var after = files.Read("after").Pages[0]; Assert.Equal("applied", after.RowAlignment.Status);
        Assert.Empty(after.Clusters); Assert.Equal(2, after.DifferenceCount);
        Assert.Equal(2, files.Read("after").Summary.DifferenceCount);
        var audit = await files.Run("audit", "rows: {enabled: true}\nexclude:\n" + string.Join('\n', yaml.Split('\n').Select(s => "  " + s)), extra: ["--no-regions"]);
        Assert.Equal(1, audit.Code); Assert.True(files.Read("audit").Config.Rows.Enabled);
        Assert.Equal(3, files.Read("audit").Summary.DifferenceCount);
    }

    [Fact]
    public async Task Directory_summary_uses_total_differences_and_preserves_file_counts()
    {
        using var files = new Files("R01");
        var a = Path.Combine(files.Root, "A"); var b = Path.Combine(files.Root, "B"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.Copy(files.A, Path.Combine(a, "帳票.pdf")); File.Copy(files.B, Path.Combine(b, "帳票.pdf"));
        var config = Path.Combine(files.Root, "rows.yaml"); File.WriteAllText(config, "rows: {enabled: true}");
        var run = await CliProcess.Run("compare-dir", a, b, "--config", config, "--out", files.Output("directory"));
        Assert.Equal(1, run.Code); Assert.Empty(run.Error);
        var result = JsonSerializer.Deserialize<DirectoryReportDocument>(File.ReadAllText(files.Output("directory", "index.json")), ReportJson.Options)!;
        Assert.Equal(1, result.Summary.Total); Assert.Equal(1, result.Summary.Different);
        var comparison = Assert.Single(result.Files).Comparison!; Assert.Equal(0, comparison.Clusters); Assert.Equal(2, comparison.DifferenceCount);
        Assert.Contains("2 箇所（行の挿入 1、ブロック移動 1 を含む）", File.ReadAllText(files.Output("directory", "index.html")));
    }

    [Fact]
    public async Task Global_and_row_alignment_compose_without_changing_raw_evidence()
    {
        using var files = new Files("R02");
        // 300dpiで右6px・上4px。行の追加を伴うため、全体補正の支持条件は明示した設定で確認する。
        File.WriteAllBytes(files.B, PdfFixture.RowScenario("R02", true, 1.44, 0.96));
        const string yaml = "rows: {enabled: true}\nreport: {raw_overlay: true}\nalign: {enabled: true, min_score: 0.5, min_score_gap: 0.001, min_improvement: 0.001, min_support_cells: 1, min_support_rows: 1, min_support_columns: 1}";
        var run = await files.Run("composed", yaml); Assert.Equal(1, run.Code); Assert.Empty(run.Error);
        var page = files.Read("composed").Pages[0];
        Assert.True(page.Alignment.Status == "applied", JsonSerializer.Serialize(page.Alignment));
        Assert.Equal(new PixelShift(-6, 4), page.GlobalShiftPx);
        Assert.Equal("applied", page.RowAlignment.Status); Assert.Equal(3, page.DifferenceCount);
        var cluster = Assert.Single(page.Clusters); Assert.Equal("555", cluster.TextA); Assert.Equal("111", cluster.TextB);
        Assert.NotNull(page.Images.BOriginal); Assert.Equal(page.Images.BOriginal, page.RawEvidence!.B.Image);
        var baseline = await files.Run("original", "report: {raw_overlay: true}", noHtml: true); Assert.Equal(1, baseline.Code);
        var evidence = files.Read("original").Pages[0].RawEvidence!;
        Assert.Equal(File.ReadAllBytes(files.Output("original", evidence.Overlay)), File.ReadAllBytes(files.Output("composed", page.RawEvidence.Overlay)));
        var snippet = ExclusionSnippet.Create(page, cluster, 300, 1);
        Assert.Equal(1, (await files.Run("excluded", yaml + "\nexclude:\n" + string.Join('\n', snippet.Split('\n').Select(s => "  " + s)), noHtml: true)).Code);
        var excluded = files.Read("excluded").Pages[0]; Assert.Equal("applied", excluded.RowAlignment.Status); Assert.Empty(excluded.Clusters);
    }

    [Theory]
    [InlineData("identical")] [InlineData("size_mismatch")] [InlineData("text_unavailable")]
    public async Task Eligibility_and_text_limits_preserve_explicit_reasons(string reason)
    {
        using var files = new Files("R01"); var yaml = "rows: {enabled: true}";
        if (reason == "identical") File.Copy(files.A, files.B, overwrite: true);
        if (reason == "size_mismatch") File.WriteAllBytes(files.B, PdfFixture.CreateTextPage([], media: "0 0 250 300"));
        if (reason == "text_unavailable") yaml += "\ntext: {max_words_per_page: 1}";
        var run = await files.Run("result", yaml, noHtml: true); Assert.Equal(reason == "identical" ? 0 : 1, run.Code);
        var report = files.Read("result"); Assert.Equal(reason, report.Pages[0].RowAlignment.Reason);
        Assert.DoesNotContain(report.Warnings, w => w.Code == "ROW_SHIFT_SUSPECTED");
        Assert.Null(report.Pages[0].Images.ContentA);
    }

    [Theory]
    [InlineData("too_different")] [InlineData("limit")]
    public async Task Incomplete_counts_stay_incomplete_in_report_and_html(string mode)
    {
        using var files = new Files("R07");
        var yaml = "rows: {enabled: true}\ncluster: {" + (mode == "limit" ? "max_clusters_per_page: 1" : "max_diff_ratio: 0.000001") + "}";
        var run = await files.Run("result", yaml); Assert.Equal(1, run.Code);
        var report = files.Read("result"); Assert.Equal("applied", report.Pages[0].RowAlignment.Status);
        Assert.False(report.Summary.DifferenceCountComplete); Assert.False(report.Pages[0].DifferenceCountComplete);
        Assert.Contains("網羅できません", File.ReadAllText(files.Output("result", "report.html")));
        Assert.Equal(mode == "limit" ? 3 : 2, report.Summary.DifferenceCount);
    }

    [Fact]
    public async Task Unpaired_pdf_is_not_compared_and_counts_are_not_exhaustive()
    {
        using var files = new Files("R01");
        File.WriteAllBytes(files.A, PdfFixture.CreatePages((100, 100))); File.WriteAllBytes(files.B, PdfFixture.CreatePages((100, 100), (100, 100)));
        var run = await files.Run("result", noHtml: true); Assert.Equal(1, run.Code);
        var report = files.Read("result"); Assert.Equal("not_compared", report.Pages[1].RowAlignment.Reason);
        Assert.Equal("only_in_b", report.Pages[1].Status); Assert.False(report.Summary.DifferenceCountComplete);
    }

    internal sealed class Files : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "reportdiff-row-cli-" + Guid.NewGuid().ToString("N"));
        internal string A => Path.Combine(Root, "旧版.pdf"); internal string B => Path.Combine(Root, "新版.pdf");
        internal Files(string id, bool reverse = false)
        { Directory.CreateDirectory(Root); File.WriteAllBytes(A, PdfFixture.RowScenario(id, reverse)); File.WriteAllBytes(B, PdfFixture.RowScenario(id, !reverse)); }
        internal string Output(string name, string path = "") => Path.Combine(Root, name, path);
        internal async Task<CliResult> Run(string name, string yaml = "rows: {enabled: true}\nreport: {raw_overlay: true}", bool noHtml = false, string[]? extra = null)
        {
            var config = Path.Combine(Root, name + ".yaml"); File.WriteAllText(config, yaml);
            return await CliProcess.Run(["compare", A, B, "--config", config, "--out", Output(name), .. (noHtml ? new[] { "--no-html" } : []), .. (extra ?? [])]);
        }
        internal ReportDocument Read(string name) => JsonSerializer.Deserialize<ReportDocument>(File.ReadAllText(Output(name, "result.json")), ReportJson.Options)!;
        internal Mat Image(string name, string path) => Cv2.ImDecode(File.ReadAllBytes(Output(name, path)), ImreadModes.Color);
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static void CopyDirectory(string source, string target)
    {
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, path)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination, overwrite: true);
        }
    }
}
