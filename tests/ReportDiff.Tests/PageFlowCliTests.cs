using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed partial class PageFlowCliTests
{
    [Theory]
    [InlineData("R10", false, 5, 1, true)] [InlineData("R10", true, 5, 1, true)]
    [InlineData("R11", false, 6, 1, false)] [InlineData("R11", true, 6, 1, false)]
    [InlineData("chain3", false, 8, 1, true)] [InlineData("chain3", true, 8, 1, true)]
    [InlineData("paired-content-tone", false, 6, 2, true)] [InlineData("paired-content-tone", true, 6, 2, true)]
    [InlineData("two-inserts", false, 7, 2, true)] [InlineData("two-inserts", true, 7, 2, true)]
    [InlineData("flow-number-change", false, 6, 2, true)] [InlineData("flow-number-change", true, 6, 2, true)]
    public void Flow_is_reported_without_changing_raw_evidence_or_exit_status(string scenario, bool reverse, int count, int aggregate, bool complete)
    {
        using var files = new Files(scenario, reverse);
        var on = files.Run("on", true); Assert.Equal(1, on.Code); Assert.Empty(on.Error);
        var report = files.Report("on"); var flow = Assert.IsType<ReportPageFlow>(report.PageFlow);
        Assert.Equal(count, report.Summary.DifferenceCount); Assert.Equal(aggregate, report.Summary.AggregatedDifferenceCount);
        Assert.Equal(complete, report.Summary.AggregatedDifferenceCountComplete);
        Assert.Contains($"集約後 {aggregate} 箇所（ページ別内訳 {count} 箇所）", on.Output);
        Assert.Equal("applied", flow.Status);
        Assert.Equal("grouped", flow.Aggregation.Status);
        foreach (var group in flow.Aggregation.Groups)
        foreach (var reference in group.Structures)
            Assert.Contains(report.Pages.Single(p => p.Page == reference.Page).RowAlignment.StructuralChanges, s => s.Id == reference.StructuralChangeId);
        foreach (var link in flow.Links)
        foreach (var endpoint in new[] { link.Source, link.Target }.OfType<ReportFlowEndpoint>())
        {
            Assert.Equal("original_top_left", endpoint.CoordinateSystem); Assert.NotNull(endpoint.Image);
            using var band = Cv2.ImDecode(File.ReadAllBytes(files.PathOf("on", endpoint.Image)), ImreadModes.Color);
            Assert.Equal(endpoint.BoundsPx.W, band.Width); Assert.Equal(endpoint.BoundsPx.H, band.Height);
        }
        if (scenario == "R11")
        {
            var unpaired = Assert.Single(report.Pages, p => p.Status.StartsWith("only_in_", StringComparison.Ordinal));
            Assert.False(unpaired.DifferenceCountComplete); Assert.Empty(unpaired.RowAlignment.StructuralChanges);
            Assert.True(flow.Pages.Single(p => p.Page == unpaired.Page).OnlyVerifiedBandsAndFixedParts);
            Assert.NotEmpty(Assert.Single(flow.Aggregation.Groups).AuxiliaryBands);
        }
        var html = File.ReadAllText(files.PathOf("on", "report.html"));
        Assert.Contains("ページ送りと文書集約", html); Assert.Contains("ページ別内訳", html);
        Assert.Contains("元画像の送り帯", html); Assert.DoesNotContain("src=\"https:", html);
        foreach (var reference in flow.Aggregation.Groups.SelectMany(g => g.Structures))
        {
            Assert.Contains($"href=\"#page-{reference.Page}-structure-{reference.StructuralChangeId}\"", html);
            Assert.Contains($"id=\"page-{reference.Page}-structure-{reference.StructuralChangeId}\"", html);
        }
        var off = files.Run("off", false); Assert.Equal(1, off.Code);
        var baseline = files.Report("off"); Assert.Null(baseline.PageFlow); Assert.Null(baseline.Summary.AggregatedDifferenceCount);
        Assert.DoesNotContain("事前検証", off.Output);
        foreach (var page in report.Pages)
        {
            var before = baseline.Pages.Single(p => p.Page == page.Page);
            foreach (var (oldPath, newPath) in new[] { (before.RawEvidence!.A.Image, page.RawEvidence!.A.Image),
                         (before.RawEvidence.B.Image, page.RawEvidence.B.Image), (before.RawEvidence.Overlay, page.RawEvidence.Overlay) })
                Assert.Equal(File.ReadAllBytes(files.PathOf("off", oldPath)), File.ReadAllBytes(files.PathOf("on", newPath)));
        }
    }

    [Theory]
    [InlineData("max_segments: 1", "too_many_segments")]
    [InlineData("min_support_ink_mm2: 1000", "insufficient_support")]
    [InlineData("min_score_gap: 1", "ambiguous")]
    [InlineData("min_support_bands: 3", "insufficient_support")]
    public void Row_adoption_settings_can_reject_the_entire_flow(string setting, string reason)
    {
        using var files = new Files("chain3");
        var result = files.Run("restricted", true, setting); Assert.Equal(1, result.Code); Assert.Empty(result.Error);
        var report = files.Report("restricted");
        Assert.Equal("skipped", report.PageFlow!.Status);
        Assert.Equal(report.Summary.DifferenceCount, report.Summary.AggregatedDifferenceCount);
        Assert.All(report.PageFlow.Pages.Where(p => p.Choice != "unpaired"), p => Assert.Equal("baseline", p.Choice));
        Assert.Contains(report.PageFlow.Pages, p => p.Adoption?.Reason == reason);
    }

    [Fact]
    public void Improvement_threshold_rejects_a_candidate_with_remaining_content_difference()
    {
        using var files = new Files("paired-content-tone");
        var result = files.Run("strict", true, "min_improvement: 1"); Assert.Equal(1, result.Code);
        var flow = files.Report("strict").PageFlow!;
        Assert.Equal("skipped", flow.Status); Assert.Contains(flow.Pages, p => p.Adoption?.Reason == "low_improvement");
    }

    [Theory]
    [InlineData("1,3")]
    [InlineData("1")]
    public void Selection_does_not_bridge_or_claim_document_completeness(string selection)
    {
        using var files = new Files("chain3");
        var result = files.Run("selected", true, extraArgs: ["--pages", selection]); Assert.Equal(1, result.Code);
        var report = files.Report("selected");
        Assert.Equal(selection.Split(',').Select(int.Parse), report.Pages.Select(p => p.Page));
        Assert.Equal("skipped", report.PageFlow!.Status); Assert.Empty(report.PageFlow.Links);
        Assert.False(report.Summary.AggregatedDifferenceCountComplete);
    }

    [Fact]
    public void Implicit_and_explicit_carry_disabled_keep_existing_results()
    {
        using var files = new Files("R10");
        Assert.Equal(1, files.Run("implicit", null).Code); Assert.Equal(1, files.Run("explicit", false).Code);
        var first = files.Report("implicit"); var second = files.Report("explicit");
        Assert.Equal(JsonSerializer.Serialize(first with { GeneratedAt = second.GeneratedAt }, ReportJson.Options), JsonSerializer.Serialize(second, ReportJson.Options));
        Assert.Empty(Directory.GetFiles(files.PathOf("implicit", "pages"), "flow_*.png"));
    }

    [Theory]
    [InlineData("R10")] [InlineData("global-down")] [InlineData("multiple-independent-inserts")] [InlineData("numeric-terminal-number")] [InlineData("shared-shared-two")]
    [InlineData("terminal-shared-before8")]
    [InlineData("unpaired-independent-terminal")]
    [InlineData("shared-shared-chain")] [InlineData("shared-shared-and-independent")]
    public void Output_failure_preserves_previous_output_and_removes_staging(string scenario)
    {
        using var files = new Files(scenario);
        Directory.CreateDirectory(files.PathOf("failed")); File.WriteAllText(files.PathOf("failed", "old.txt"), "old result");
        var inserted = false;
        using var writer = new HookWriter(line =>
        {
            if (!line.StartsWith("処理中 2 /", StringComparison.Ordinal)) return;
            var stage = Assert.Single(Directory.GetDirectories(files.Root, ".reportdiff-stage-*"));
            Directory.CreateDirectory(Path.Combine(stage, "pages", "flow_l001_target.png")); inserted = true;
        });
        var result = files.Run("failed", true, extraArgs: ["--force"], writer: writer, extraYaml: "align: {enabled: " + (scenario == "global-down" ? "true" : "false") + "}");
        Assert.True(inserted); Assert.Equal(2, result.Code); Assert.Contains("書き込めません", result.Error);
        Assert.Equal("old result", File.ReadAllText(files.PathOf("failed", "old.txt")));
        Assert.False(File.Exists(files.PathOf("failed", "result.json")));
        Assert.Empty(Directory.GetDirectories(files.Root, ".reportdiff-stage-*"));
    }

    [Fact]
    public void Image_inputs_are_skipped_and_raw_overlay_is_still_saved()
    {
        using var files = new Files("R10");
        using var image = new Mat(40, 60, MatType.CV_8UC3, Scalar.White);
        File.WriteAllBytes(files.A, image.ImEncode(".png")); image.Set(20, 20, new Vec3b(0, 0, 0)); File.WriteAllBytes(files.B, image.ImEncode(".png"));
        var result = files.Run("images", true); Assert.Equal(0, result.Code); // 1pxは既存ノイズ閾値で省略される。
        var report = files.Report("images"); Assert.Contains("not_pdf", report.PageFlow!.Reasons);
        Assert.NotNull(Assert.Single(report.Pages).RawEvidence); Assert.Empty(report.PageFlow.Links);
    }

    [Fact]
    public void Global_alignment_is_preserved_while_flow_uses_explicit_fallback()
    {
        using var files = new Files("R10");
        File.WriteAllBytes(files.A, PdfFixture.RowScenario("R02", false));
        File.WriteAllBytes(files.B, PdfFixture.RowScenario("R02", true, 1.44, .96));
        const string align = "align: {enabled: true, min_score: 0.5, min_score_gap: 0.001, min_improvement: 0.001, min_support_cells: 1, min_support_rows: 1, min_support_columns: 1}";
        Assert.Equal(1, files.Run("on", true, extraYaml: align).Code);
        Assert.Equal(1, files.Run("off", false, extraYaml: align).Code);
        var on = files.Report("on"); var off = files.Report("off");
        Assert.Contains("global_alignment_applied", on.PageFlow!.Reasons);
        var page = Assert.Single(on.Pages); Assert.Equal("applied", page.Alignment.Status);
        Assert.Equal(new PixelShift(-6, 4), page.GlobalShiftPx); Assert.Equal(3, page.DifferenceCount);
        Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(on.Pages, ReportJson.Options));
        Assert.Equal(File.ReadAllBytes(files.PathOf("on", page.RawEvidence!.Overlay)), File.ReadAllBytes(files.PathOf("off", off.Pages[0].RawEvidence!.Overlay)));
    }

    [Fact]
    public void Per_page_diff_settings_are_saved_at_both_endpoints_and_no_regions_keeps_carry_enabled()
    {
        using var files = new Files("R10");
        const string region = "regions: [{name: page2, page: 2, x: 0, y: 0, w: 100, h: 120, mode: compare, diff: {color_threshold: 9, max_shift_mm: 0}}]";
        Assert.Equal(1, files.Run("regions", true, extraYaml: region).Code);
        var report = files.Report("regions"); var link = Assert.Single(report.PageFlow!.Links);
        Assert.Equal("applied", report.PageFlow.Status); Assert.Equal(16, link.OriginalComparisons);
        Assert.Empty(link.Verification!.Source.Regions); Assert.Equal(9, Assert.Single(link.Verification.Target.Regions).Diff.ColorThreshold);
        Assert.Empty(link.Verification.Source.Exclude); Assert.False(link.Verification.ExclusionsUsed);
        Assert.True(link.Verification.ExactBandPixelsRequired);
        Assert.Equal(1, files.Run("without", true, extraArgs: ["--no-regions"], extraYaml: region).Code);
        var without = files.Report("without"); Assert.True(without.Config.Rows.CarryEnabled);
        Assert.Empty(without.Config.Regions!); Assert.Equal("applied", without.PageFlow!.Status);
    }

    [Theory]
    [InlineData("text: {max_words_per_page: 1}", "text_unavailable")]
    [InlineData("", "size_mismatch")]
    public void Ineligible_document_keeps_legacy_comparison(string yaml, string reason)
    {
        using var files = new Files("R10");
        if (reason == "size_mismatch") File.WriteAllBytes(files.B, PdfFixture.CreateTextPage([], media: "0 0 250 300"));
        Assert.Equal(1, files.Run("on", true, extraYaml: yaml).Code);
        Assert.Equal(1, files.Run("off", false, extraYaml: yaml).Code);
        var on = files.Report("on"); var off = files.Report("off");
        Assert.Contains(reason, on.PageFlow!.Reasons);
        Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(on.Pages, ReportJson.Options));
    }

    [Theory]
    [InlineData("R10", 5, 1)] [InlineData("global-down", 5, 1)] [InlineData("shared-shared-two", 7, 2)] [InlineData("multiple-independent-inserts", 10, 2)]
    [InlineData("shared-shared-chain", 10, 2)] [InlineData("shared-shared-and-independent", 12, 3)]
    [InlineData("terminal-shared-before8", 8, 2)] [InlineData("terminal-shared-tone", 9, 3)]
    [InlineData("unpaired-independent-terminal", 11, 2)] [InlineData("unpaired-independent-tone", 12, 3)]
    public void Compare_dir_preserves_layered_carry_setting_and_both_counts(string scenario, int count, int aggregate)
    {
        using var files = new Files(scenario);
        var a = Path.Combine(files.Root, "A"); var b = Path.Combine(files.Root, "B"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.Copy(files.A, Path.Combine(a, "帳票.pdf")); File.Copy(files.B, Path.Combine(b, "帳票.pdf"));
        var common = Path.Combine(files.Root, "common.yaml"); File.WriteAllText(common, "rows: {enabled: true}\nalign: {enabled: " + (scenario == "global-down" ? "true" : "false") + "}");
        File.WriteAllText(Path.Combine(files.Root, "selected.yaml"), "rows: {carry_enabled: true}");
        var rules = Path.Combine(files.Root, "rules.yaml"); File.WriteAllText(rules, "schema_version: 1\nrules: [{pattern: '帳票', config: selected.yaml}]");
        using var output = new StringWriter(); using var error = new StringWriter();
        var code = CliApplication.Run(["compare-dir", a, b, "--out", files.PathOf("directory"), "--config", common, "--rules", rules], output, error);
        Assert.Equal(1, code); Assert.Equal("", error.ToString());
        var index = JsonSerializer.Deserialize<DirectoryReportDocument>(File.ReadAllText(files.PathOf("directory", "index.json")), ReportJson.Options)!;
        var result = Assert.Single(index.Files); Assert.Equal(count, result.Comparison!.DifferenceCount); Assert.Equal(aggregate, result.Comparison.AggregatedDifferenceCount);
        Assert.Contains($"集約後 {aggregate} 箇所（ページ別内訳 {count} 箇所）", File.ReadAllText(files.PathOf("directory", "index.html")));
        var child = JsonSerializer.Deserialize<ReportDocument>(File.ReadAllText(files.PathOf("directory", result.Json)), ReportJson.Options)!;
        Assert.True(child.Config.Rows.CarryEnabled); Assert.Equal("applied", child.PageFlow!.Status);
        if (scenario.StartsWith("unpaired-", StringComparison.Ordinal) || scenario.StartsWith("terminal-shared-", StringComparison.Ordinal))
        { Assert.False(child.Summary.DifferenceCountComplete); Assert.False(result.Comparison.AggregatedDifferenceCountComplete); }
    }

    [Theory]
    [InlineData("down", false, -4, -4, 5)] [InlineData("up", false, 6, 6, 5)]
    [InlineData("down", true, 4, 4, 5)] [InlineData("variable", false, -4, 6, 5)]
    [InlineData("mixed", false, 0, -4, 5)] [InlineData("chain", false, -4, 6, 8)]
    public void Vertical_global_flow_uses_original_evidence_and_relative_structure_displacement(string scenario, bool reverse, int dy1, int dy2, int count)
    {
        using var files = new Files("global-" + scenario, reverse);
        const string align = "align: {enabled: true}";
        var result = files.Run("on", true, extraYaml: align); Assert.Equal(1, result.Code); Assert.Empty(result.Error);
        var report = files.Report("on"); var flow = report.PageFlow!;
        Assert.Equal("applied", flow.Status); Assert.Equal(count, report.Summary.DifferenceCount);
        Assert.Equal(1, report.Summary.AggregatedDifferenceCount); Assert.Empty(report.Pages.SelectMany(p => p.Clusters));
        Assert.Equal(dy1, report.Pages[0].Alignment.EstimatedShiftPx!.Dy); Assert.Equal(dy2, report.Pages[1].Alignment.EstimatedShiftPx!.Dy);
        if (scenario == "chain") Assert.Equal(-3, report.Pages[2].Alignment.EstimatedShiftPx!.Dy);
        foreach (var page in report.Pages)
        foreach (var structure in page.RowAlignment.StructuralChanges)
        {
            if (structure.Kind == "block_moved")
            {
                Assert.Equal(reverse ? -100 : 100, structure.DisplacementPx!.Dy);
                Assert.Equal(structure.DisplacementPx.Dy, structure.SourceB!.BoundsPx.Y + page.Alignment.EstimatedShiftPx!.Dy - structure.SourceA!.BoundsPx.Y);
            }
            if (structure.Kind == (reverse ? "deleted" : "inserted"))
                Assert.False(string.IsNullOrWhiteSpace(reverse ? structure.TextA : structure.TextB));
        }
        foreach (var link in flow.Links)
        {
            Assert.Equal(12, link.OriginalComparisons); Assert.Equal("verified", link.ImageStatus);
            foreach (var endpoint in new[] { link.Source!, link.Target! })
            {
                Assert.Equal("original_top_left", endpoint.CoordinateSystem); Assert.NotEmpty(endpoint.Structures);
                var page = report.Pages.Single(p => p.Page == endpoint.Page);
                foreach (var reference in endpoint.Structures)
                {
                    var structure = page.RowAlignment.StructuralChanges.Single(s => s.Id == reference.StructuralChangeId);
                    var source = endpoint.Side == "a" ? structure.SourceA! : structure.SourceB!;
                    Assert.Equal(endpoint.BoundsPx.Y, source.BoundsPx.Y); Assert.Equal(endpoint.BoundsPx.H, source.BoundsPx.H);
                }
                using var original = Cv2.ImDecode(File.ReadAllBytes(files.PathOf("on", endpoint.Side == "a" ? page.RawEvidence!.A.Image : page.RawEvidence!.B.Image)), ImreadModes.Color);
                using var crop = new Mat(original, new Rect(endpoint.BoundsPx.X, endpoint.BoundsPx.Y, endpoint.BoundsPx.W, endpoint.BoundsPx.H));
                using var saved = Cv2.ImDecode(File.ReadAllBytes(files.PathOf("on", endpoint.Image!)), ImreadModes.Color);
                Assert.Equal(0, Cv2.Norm(crop, saved, NormTypes.INF));
            }
        }
        Assert.Equal(1, files.Run("off", false, extraYaml: align).Code);
        var off = files.Report("off");
        foreach (var page in report.Pages)
            Assert.Equal(File.ReadAllBytes(files.PathOf("off", off.Pages[page.Page - 1].RawEvidence!.Overlay)), File.ReadAllBytes(files.PathOf("on", page.RawEvidence!.Overlay)));
    }

    [Theory]
    [InlineData("body-tone", "", 2)]
    [InlineData("body-tone", "exclude: [{page: 2, x: 54.86, y: 59.43599999999999, w: 1.6933333333333334, h: 1.6933333333333334}]", 1)]
    [InlineData("body-tone", "regions: [{page: 2, name: strict, mode: compare, x: 54.86, y: 59.43599999999999, w: 1.6933333333333334, h: 1.6933333333333334, diff: {max_shift_mm: 0, edge_tolerance: 0}}]", 2)]
    public void Global_flow_content_settings_are_mapped_from_A_once(string scenario, string yaml, int aggregate)
    {
        using var files = new Files("global-" + scenario);
        Assert.Equal(1, files.Run("on", true, extraYaml: "align: {enabled: true}\n" + yaml).Code);
        var report = files.Report("on"); Assert.Equal("applied", report.PageFlow!.Status);
        Assert.Equal(aggregate, report.Summary.AggregatedDifferenceCount);
        Assert.Equal(aggregate - 1, report.Summary.Clusters);
        foreach (var cluster in report.Pages[1].Clusters)
        {
            Assert.Single(cluster.SourceB!.PartsPx);
            Assert.InRange(cluster.SourceB.BoundsPx.Y, 809, 811); // original B: 550 + 256 + 4
        }
    }

    [Theory]
    [InlineData("horizontal")] [InlineData("both")] [InlineData("unpaired")]
    [InlineData("band-tone")] [InlineData("band-tone", true)] [InlineData("edge")] [InlineData("subpixel")]
    public void Global_flow_fallback_preserves_the_entire_legacy_page_result(string scenario, bool exclude = false)
    {
        using var files = new Files("global-" + scenario);
        var align = "align: {enabled: true}" + (exclude ? "\nexclude: [{page: 2, x: 54, y: 51, w: 3, h: 3}]" : "");
        Assert.Equal(1, files.Run("on", true, extraYaml: align).Code); Assert.Equal(1, files.Run("off", false, extraYaml: align).Code);
        var on = files.Report("on"); var off = files.Report("off");
        Assert.Equal("skipped", on.PageFlow!.Status);
        if (scenario == "band-tone") Assert.Equal("nonidentical_band_not_proven", Assert.Single(on.PageFlow.Links).Reason);
        Assert.All(on.PageFlow.Pages.Where(p => p.Choice != "unpaired"), p => Assert.Equal("baseline", p.Choice));
        Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(on.Pages, ReportJson.Options));
        Assert.Equal(on.Summary.DifferenceCount, on.Summary.AggregatedDifferenceCount);
    }

    [Fact]
    public void Global_flow_changed_input_preserves_old_output()
    {
        using var files = new Files("global-down"); Directory.CreateDirectory(files.PathOf("changed"));
        File.WriteAllText(files.PathOf("changed", "old.txt"), "old result"); var changed = false;
        using var writer = new HookWriter(line =>
        {
            if (changed || !line.StartsWith("処理中 1 /", StringComparison.Ordinal)) return;
            File.AppendAllText(files.B, "\n% changed during verification\n"); changed = true;
        });
        var result = files.Run("changed", true, extraArgs: ["--force"], writer: writer, extraYaml: "align: {enabled: true}");
        Assert.True(changed); Assert.Equal(2, result.Code); Assert.Contains("入力ファイルが変わりました", result.Error);
        Assert.Equal("old result", File.ReadAllText(files.PathOf("changed", "old.txt")));
        Assert.False(File.Exists(files.PathOf("changed", "result.json")));
        Assert.Empty(Directory.GetDirectories(files.Root, ".reportdiff-stage-*"));
    }

    private sealed class HookWriter(Action<string> hook) : StringWriter
    {
        public override void WriteLine(string? value) { hook(value ?? ""); base.WriteLine(value); }
    }
    private sealed class Files : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "reportdiff-flow-" + Guid.NewGuid().ToString("N"));
        internal string A => Path.Combine(Root, "入力A.pdf");
        internal string B => Path.Combine(Root, "入力B.pdf");
        internal Files(string scenario, bool reverse = false)
        {
            Directory.CreateDirectory(Root);
            var shared = scenario.StartsWith("shared-", StringComparison.Ordinal);
            var ambiguity = scenario.StartsWith("ambiguity-", StringComparison.Ordinal);
            var support = scenario.StartsWith("support-", StringComparison.Ordinal);
            var numeric = scenario.StartsWith("numeric-", StringComparison.Ordinal);
            var global = scenario.StartsWith("global-", StringComparison.Ordinal);
            var multiple = scenario.StartsWith("multiple-", StringComparison.Ordinal);
            var terminalShared = scenario.StartsWith("terminal-shared-", StringComparison.Ordinal);
            var unpaired = scenario.StartsWith("unpaired-", StringComparison.Ordinal);
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", terminalShared ? "page-flow-terminal-shared" : unpaired ? "page-flow-unpaired" : support ? "page-flow-same-page-support" : ambiguity ? "page-flow-ambiguity" : shared ? "page-flow-shared" : numeric ? "page-flow-numeric" : multiple ? "page-flow-multiple" : global ? "page-flow-global" : "page-flow");
            if (terminalShared) scenario = scenario[16..];
            if (unpaired) scenario = scenario[9..];
            if (support) scenario = scenario[8..];
            if (ambiguity) scenario = scenario[10..];
            if (shared) scenario = scenario[7..];
            if (numeric) scenario = scenario[8..];
            if (global) scenario = scenario[7..];
            if (multiple) scenario = scenario[9..];
            var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(fixture, "sha256.json")))!;
            foreach (var (name, output) in new[] { (reverse ? "b" : "a", A), (reverse ? "a" : "b", B) })
            {
                var bytes = File.ReadAllBytes(Path.Combine(fixture, scenario, name + ".pdf"));
                Assert.Equal(hashes[scenario + "/" + name + ".pdf"], Convert.ToHexStringLower(SHA256.HashData(bytes))); File.WriteAllBytes(output, bytes);
            }
        }
        internal string PathOf(string name, string? file = null) => file is null ? Path.Combine(Root, name) : Path.Combine(Root, name, file);
        internal ReportDocument Report(string name) => JsonSerializer.Deserialize<ReportDocument>(File.ReadAllBytes(PathOf(name, "result.json")), ReportJson.Options)!;
        internal (int Code, string Output, string Error) Run(string name, bool? carry, string rowSettings = "", string[]? extraArgs = null, StringWriter? writer = null, string extraYaml = "")
        {
            var config = Path.Combine(Root, name + ".yaml");
            File.WriteAllText(config, "rows:\n  enabled: true\n" + (carry is null ? "" : $"  carry_enabled: {carry.ToString()!.ToLowerInvariant()}\n")
                + (rowSettings.Length == 0 ? "" : "  " + rowSettings + "\n") + "report: {raw_overlay: true}\n" + extraYaml);
            using var own = new StringWriter(); using var error = new StringWriter(); var output = writer ?? own;
            var args = new[] { "compare", A, B, "--config", config, "--out", PathOf(name) }.Concat(extraArgs ?? []).ToArray();
            var code = CliApplication.Run(args, output, error); return (code, output.ToString(), error.ToString());
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
