using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
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
public sealed class AnchoredOutputTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Actual_pdf_cli_uses_anchored_route_and_keeps_raw_evidence(bool reverse)
    {
        using var files = new Files(reverse);
        var report = files.Compare("on");
        Assert.Equal(6, report.Summary.DifferenceCount); Assert.Equal(2, report.Summary.AggregatedDifferenceCount);
        Assert.Equal("applied", report.PageFlow!.AnchoredContent!.Status);
        Assert.Equal(2, report.PageFlow.AnchoredContent.OmittedOriginalBands.Count);
        Assert.All(report.Pages, p => { Assert.Null(p.RowAlignment.ContentCanvas); Assert.Equal(p.Page, p.RowAlignment.ContentSurfaceId); });
        Assert.Equal(199800, report.Pages.Sum(p => p.RowAlignment.OmissionAudit!.OmittedBandPixels));
        Assert.All(report.Pages.SelectMany(p => p.RowAlignment.StructuralChanges), s => Assert.Equal(s.Role != "cause", s.ComparedInContent));
        var baseline = files.Compare("off", files.Settings with { Rows = files.Settings.Rows with { CarryEnabled = false } });
        foreach (var p in report.Pages)
            Assert.Equal(File.ReadAllBytes(files.PathOf("off", baseline.Pages[p.Page - 1].RawEvidence!.Overlay)),
                File.ReadAllBytes(files.PathOf("on", p.RawEvidence!.Overlay)));
        CheckLinks(files.PathOf("on"));
        Assert.Empty(Directory.GetDirectories(files.Root, ".reportdiff-stage-*"));
        var repeat = files.Compare("repeat");
        Assert.Equal(JsonSerializer.Serialize(report with { GeneratedAt = repeat.GeneratedAt }, ReportJson.Options), JsonSerializer.Serialize(repeat, ReportJson.Options));
    }

    [Theory]
    [InlineData("content_saved_1")] [InlineData("display_saved_1")]
    public void Reservation_failure_after_images_discards_entire_attempt_and_runs_baseline(string failure)
    {
        using var files = new Files(); AnchoredContentBudget.Reservation? held = null; var injected = false; AnchoredContentPlan? plan = null;
        try
        {
            var report = files.Compare("fallback", hook: (stage, p, trial) =>
            {
                plan = p; if (stage != failure) return;
                Assert.NotEmpty(Directory.GetFiles(trial, "*.png", SearchOption.AllDirectories));
                Assert.True(p.Budget.TryReserve(AnchoredAllocation.PixelId, (p.Budget.RemainingBytes - 64) / 4, out held)); injected = true;
            });
            Assert.True(injected); Assert.Equal("skipped", report.PageFlow!.AnchoredContent!.Status);
            Assert.Equal("flow_descriptor_limit", report.PageFlow.AnchoredContent.Reason);
            Assert.All(report.Pages, p => { Assert.Null(p.ContentReferences); Assert.Null(p.ContentDisplay); });
            var off = files.Compare("off", files.Settings with { Rows = files.Settings.Rows with { CarryEnabled = false } });
            Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
            Assert.Empty(Directory.GetDirectories(files.PathOf("fallback"), ".anchored*", SearchOption.AllDirectories));
            Assert.DoesNotContain(Directory.GetFiles(files.PathOf("fallback"), "*", SearchOption.AllDirectories), p => p.Contains("_ref_"));
            Assert.True(plan!.Budget.IsDisposed); Assert.Equal(plan.Budget.ExistingBytes, plan.Budget.UsedBytes);
        }
        finally { held?.Dispose(); }
    }

    [Theory]
    [InlineData("before_recompare")] [InlineData("content_saved_1")] [InlineData("display_saved_1")]
    [InlineData("json_saved")] [InlineData("before_publication")]
    public void Changed_input_is_error_and_old_output_survives(string failure)
    {
        using var files = new Files(); files.Old("keep");
        Assert.Throws<CommandLineException>(() => files.Compare("keep", hook: (stage, _, _) =>
        { if (stage == failure) File.AppendAllText(files.B, "\n% changed\n"); }));
        files.AssertOld("keep");
    }

    [Theory]
    [InlineData("content_saved_1", "io")] [InlineData("display_saved_1", "cancel")]
    [InlineData("json_saved", "memory")] [InlineData("before_publication", "io")]
    public void Actual_errors_never_turn_into_successful_baseline(string failure, string kind)
    {
        using var files = new Files(); files.Old("keep");
        var error = Record.Exception(() => files.Compare("keep", hook: (stage, _, _) =>
        {
            if (stage == failure) throw kind switch { "io" => new IOException("保存失敗"), "cancel" => new OperationCanceledException(), _ => new OutOfMemoryException() };
        }));
        Assert.NotNull(error); files.AssertOld("keep");
    }

    [Fact]
    public void Missing_image_link_prevents_publication()
    {
        using var files = new Files(); files.Old("keep");
        Assert.Throws<ReportWriteException>(() => files.Compare("keep", hook: (stage, _, trial) =>
        { if (stage == "before_publication") File.Delete(Path.Combine(trial, "pages/p001_content_a.png")); }));
        files.AssertOld("keep");
    }

    [Fact]
    public void Settings_file_edits_do_not_replace_resolved_snapshot()
    {
        using var files = new Files(); var path = files.PathOf("settings.yaml"); File.WriteAllText(path, "rows: {enabled: true, carry_enabled: true}");
        var report = files.Compare("out", hook: (stage, _, _) => { if (stage == "before_recompare") File.WriteAllText(path, "rows: {enabled: false}"); });
        Assert.Equal("applied", report.PageFlow!.AnchoredContent!.Status);
        Assert.True(report.Config.Rows.Enabled);
    }

    [Theory]
    [InlineData("global")] [InlineData("selected")] [InlineData("strict")] [InlineData("region")] [InlineData("disabled")]
    public void Out_of_scope_does_not_add_anchored_fields(string variant)
    {
        using var files = new Files(); var settings = files.Settings;
        settings = variant switch
        {
            "global" => settings with { Align = new() { Enabled = true } },
            "strict" => settings with { Diff = new() { MaxShiftMm = 0 } },
            "disabled" => settings with { Rows = settings.Rows with { CarryEnabled = false } },
            "region" => settings with { Exclude = [new(1, 1, 1, 1, 1)] }, _ => settings
        };
        var report = files.Compare("out", settings, pages: variant == "selected" ? "1-2" : null);
        Assert.Null(report.PageFlow?.AnchoredContent);
        Assert.DoesNotContain("anchored_content", File.ReadAllText(files.PathOf("out", "result.json")));
    }

    [Theory]
    [InlineData(false, "original", 0)] [InlineData(true, "original", 0)]
    [InlineData(false, "tone", 3807)] [InlineData(true, "tone", 3807)]
    [InlineData(false, "context", 956)] [InlineData(true, "context", 956)]
    public void Memory_variant_report_preserves_C_counts_D_references_and_original_A_yaml(bool reverse, string variant, int raw)
    {
        using var files = new Files(reverse); using var input = new AnchoredComparisonTests.Input(reverse, variant);
        using var plan = input.Plan(); using var result = plan.Compare(input.Read, input.Text, out _); Assert.NotNull(result);
        var output = files.PathOf("memory"); var writer = new ReportWriter(output,
            new(new(files.A, "pdf", 2, new('a', 64)), new(files.B, "pdf", 2, new('b', 64))), files.Settings.ToReportConfiguration());
        var paths = new PageImages[2];
        for (var page = 1; page <= 2; page++) { using var images = plan.RenderContent(page, input.Read); paths[page - 1] = writer.AddAnchoredContent(page, images); }
        string? source = null, target = null;
        for (var page = 1; page <= 2; page++)
        {
            RawEvidence? original;
            using (var a = input.Read(new(PageSpace.A, page))) using (var b = input.Read(new(PageSpace.B, page)))
            {
                original = writer.AddAnchoredRaw(page, a, b, 300);
                foreach (var (span, isSource) in new[] { (plan.Evidence.Source, true), (plan.Evidence.Target, false) })
                {
                    if (span.Page.Page != page) continue;
                    var path = writer.AddFlowBand(plan.Evidence.CandidateId, isSource, span.Page.Side == PageSpace.A ? a : b, new(span.Page, span.Top, span.Height));
                    if (isSource) source = path; else target = path;
                }
            }
            using var d = result.ProjectDisplay(page); using var images = plan.RenderDisplay(page, input.Read);
            var content = result.Contents[page - 1]; var pieces = plan.Surfaces[page - 1].Pieces;
            var textA = AnchoredTextAnnotations.Create(pieces, PageSpace.A, input.Text, 300, content.Clusters, new(), plan.Budget);
            var textB = AnchoredTextAnnotations.Create(pieces, PageSpace.B, input.Text, 300, content.Clusters, new(), plan.Budget);
            writer.AddAnchoredPage(plan, result, d, images, paths[page - 1], original, textA, textB, new(new Dictionary<int, string>(), []), new(new Dictionary<int, string>(), []));
        }
        var report = writer.Complete(AnchoredFlowReport.Create(plan, result, source!, target!)); HtmlReportWriter.Write(output, report);
        AnchoredFlowReport.Validate(report, output); CheckLinks(output);
        Assert.Equal(raw, report.Pages.Sum(p => p.RawPixels)); Assert.Equal(raw == 0 ? 0 : 1, report.Summary.Clusters);
        Assert.Equal(raw == 0 ? 6 : 7, report.Summary.DifferenceCount); Assert.Equal(raw == 0 ? 2 : 3, report.Summary.AggregatedDifferenceCount);
        if (variant != "context") { Assert.All(report.Pages, p => Assert.Empty(p.ContentReferences!)); return; }
        var owned = Assert.Single(report.Pages[0].Clusters); var reference = Assert.Single(report.Pages[1].ContentReferences!);
        Assert.Equal(owned.ContentRef, reference.ContentRef); Assert.Equal(238, reference.DisplayPixels);
        Assert.Equal(new[] { 956, 238 }, report.Pages.Select(p => p.ContentDisplay!.RawPixels));
        Assert.Empty(report.Pages[1].Clusters); Assert.Equal(2, report.Pages[1].DifferenceCount);
        Assert.Null(reverse ? owned.SourceA : owned.SourceB);
        var foreign = Assert.Single(reference.Parts); Assert.Equal(reverse ? 2 : 1, foreign.SourceA!.Page);
        Assert.All(owned.RowParts!, p => { if (reverse && p.ContentBoundsPx.Y == 700) Assert.Null(p.SourceA); if (!reverse && p.ContentBoundsPx.Y == 700) Assert.Null(p.SourceB); });
        var html = File.ReadAllText(Path.Combine(output, "report.html"));
        Assert.Contains("P1-C1の参照表示", html); Assert.Contains("表示上の画素数 238", html);
        Assert.Contains("crops/p002_ref_p001_c001_diff.png", html);
        Assert.Contains("href=\"#page-2-ref-1-1\"", html); Assert.Contains("href=\"#page-1-cluster-1\"", html);
        if (reverse) Assert.Contains("この断片の除外 YAML · 元A 2ページ", html);
        else Assert.Contains("この参照位置には設定用Aを表示していません", html);
        Assert.DoesNotContain("https://", html); Assert.DoesNotContain("http://", html);
    }

    [Fact]
    public void Original_mapping_applies_margin_after_mapping()
    {
        var snippet = ExclusionSnippet.CreateOriginal(new("a", 2, "original_top_left", new(100, 300, 238, 1)), new(999, 1250), 300, 1);
        Assert.Contains("page: 2", snippet); Assert.Equal("- {page: 2, x: 7, y: 24, w: 23, h: 2.5, note: \"\"}", snippet); Assert.DoesNotContain("y: 58", snippet);
    }

    [Fact]
    public void Transferred_input_pixels_remain_alive_until_new_owner_disposes()
    {
        using var files = new Files(); using var pdf = PdfReader.Open(files.A);
        Mat pixels;
        using (var loaded = pdf.ReadPage(1, 300)) { pixels = loaded.TakePixels(); Assert.Throws<ObjectDisposedException>(() => loaded.Pixels); }
        using (pixels) Assert.False(pixels.Empty()); Assert.True(pixels.IsDisposed);
    }

    [Fact]
    public void Original_words_are_taken_from_foreign_physical_page_and_limits_are_audited()
    {
        using var input = new AnchoredComparisonTests.Input(); using var plan = input.Plan();
        var pieces = plan.Surfaces[0].Pieces;
        RowTextResult Text(PageFlowPageKey key) => new("available", null,
            [new(key.Page == 2 ? "FOREIGN_PAGE_TWO" : "WRONG_PAGE_ONE", new(581, 300, 819, 301), [301])]);
        var cluster = new DifferenceCluster(1, new(581, 700, 238, 1), 238);
        var text = AnchoredTextAnnotations.Create(pieces, PageSpace.B, Text, 300, [cluster], new(), plan.Budget);
        Assert.Equal("FOREIGN_PAGE_TWO", text.TextByCluster[1]);
        var shortText = AnchoredTextAnnotations.Create(pieces, PageSpace.B, Text, 300, [cluster], new() { MaxRunesPerCluster = 5 }, plan.Budget);
        Assert.Equal("FORE…", shortText.TextByCluster[1]); Assert.Equal("TEXT_ANNOTATION_TRUNCATED", Assert.Single(shortText.Warnings).Code);
        var skipped = AnchoredTextAnnotations.Create(pieces, PageSpace.B, _ => new("text_unavailable", "rotated_page", []), 300, [cluster], new(), plan.Budget);
        Assert.Empty(skipped.TextByCluster); Assert.Contains("rotated_page", Assert.Single(skipped.Warnings).Message);
    }

    [Fact]
    public void Compare_directory_uses_owned_summary_counts()
    {
        using var files = new Files();
        Directory.CreateDirectory(files.PathOf("A")); Directory.CreateDirectory(files.PathOf("B"));
        File.Copy(files.A, files.PathOf("A", "帳票.pdf")); File.Copy(files.B, files.PathOf("B", "帳票.pdf"));
        var config = files.PathOf("settings.yaml"); File.WriteAllText(config, "rows: {enabled: true, carry_enabled: true}\nreport: {raw_overlay: true}");
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(1, CliApplication.Run(["compare-dir", files.PathOf("A"), files.PathOf("B"), "--out", files.PathOf("directory"), "--config", config], output, error));
        Assert.Empty(error.ToString());
        var index = JsonSerializer.Deserialize<DirectoryReportDocument>(File.ReadAllText(files.PathOf("directory", "index.json")), ReportJson.Options)!;
        var pair = Assert.Single(index.Files); Assert.Equal(6, pair.Comparison!.DifferenceCount); Assert.Equal(2, pair.Comparison.AggregatedDifferenceCount);
        Assert.Equal(0, pair.Comparison.Clusters); Assert.Equal("different", pair.Status);
        Assert.Contains("集約後 2 箇所（ページ別内訳 6 箇所）", File.ReadAllText(files.PathOf("directory", "index.html")));
    }

    [Fact]
    public void Directory_final_input_check_records_error_without_damaging_other_pair()
    {
        using var files = new Files(); Directory.CreateDirectory(files.PathOf("A")); Directory.CreateDirectory(files.PathOf("B"));
        foreach (var name in new[] { "one.pdf", "two.pdf" })
        { File.Copy(files.A, files.PathOf("A", name)); File.Copy(files.B, files.PathOf("B", name)); }
        var config = files.PathOf("settings.yaml"); File.WriteAllText(config, "rows: {enabled: true, carry_enabled: true}");
        using var output = new HookWriter(line => { if (line == "一覧レポート出力中") File.AppendAllText(files.PathOf("B", "one.pdf"), "\n% changed\n"); });
        using var error = new StringWriter();
        Assert.Equal(2, CliApplication.Run(["compare-dir", files.PathOf("A"), files.PathOf("B"), "--out", files.PathOf("directory"), "--config", config], output, error));
        var index = JsonSerializer.Deserialize<DirectoryReportDocument>(File.ReadAllText(files.PathOf("directory", "index.json")), ReportJson.Options)!;
        Assert.Equal(1, index.Summary.Error); Assert.Equal(1, index.Summary.Compared);
        var failed = Assert.Single(index.Files, f => f.Status == "error"); Assert.Null(failed.Json); Assert.Null(failed.Comparison);
        Assert.False(Directory.Exists(files.PathOf("directory", "files/" + failed.Id)));
        Assert.Equal(2, Assert.Single(index.Files, f => f.Status == "different").Comparison!.AggregatedDifferenceCount);
    }
    private sealed class HookWriter(Action<string> hook) : StringWriter
    { public override void WriteLine(string? value) { hook(value ?? ""); base.WriteLine(value); } }

    private static void CheckLinks(string output)
    {
        var html = File.ReadAllText(Path.Combine(output, "report.html"));
        foreach (Match match in Regex.Matches(html, "(?:href|src)=\"([^\"]+)\""))
        {
            var path = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
            if (path.StartsWith('#')) Assert.Contains($"id=\"{path[1..]}\"", html);
            else Assert.True(File.Exists(Path.Combine(output, path)), path);
        }
    }
    private sealed class Files : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "reportdiff-anchored-" + Guid.NewGuid().ToString("N"));
        internal string A => PathOf("入力A.pdf"); internal string B => PathOf("入力B.pdf");
        internal AppSettings Settings { get; } = new() { Rows = new() { Enabled = true, CarryEnabled = true }, Report = new() { RawOverlay = true } };
        internal Files(bool reverse = false)
        {
            Directory.CreateDirectory(Root); var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-same-page-support/same-page-two");
            File.Copy(Path.Combine(dir, reverse ? "b.pdf" : "a.pdf"), A); File.Copy(Path.Combine(dir, reverse ? "a.pdf" : "b.pdf"), B);
        }
        internal string PathOf(string path, string? name = null) => name is null ? Path.Combine(Root, path) : Path.Combine(Root, path, name);
        internal ReportDocument Compare(string output, AppSettings? settings = null, Action<string, AnchoredContentPlan, string>? hook = null, string? pages = null)
        {
            using var workspace = new OutputWorkspace(PathOf(output), true, A, B);
            var command = new CompareCommand(A, B, PathOf(output), null, null, null, pages, false, false, true, true);
            var result = ComparisonRunner.Compare(command, settings ?? Settings, workspace.StagingPath, anchoredStage: hook); workspace.Commit(); return result;
        }
        internal void Old(string name) { Directory.CreateDirectory(PathOf(name)); File.WriteAllText(PathOf(name, "old.txt"), "old result"); }
        internal void AssertOld(string name)
        { Assert.Equal(new[] { PathOf(name, "old.txt") }, Directory.GetFiles(PathOf(name))); Assert.Equal("old result", File.ReadAllText(PathOf(name, "old.txt"))); Assert.Empty(Directory.GetDirectories(Root, ".reportdiff-stage-*")); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
