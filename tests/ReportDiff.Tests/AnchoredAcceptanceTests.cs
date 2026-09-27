using System.Runtime.Versioning;
using System.Security.Cryptography;
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
public sealed class AnchoredAcceptanceTests
{
    [Fact]
    public void Separate_pdf_fixtures_are_fixed_and_original_inputs_are_unchanged()
    {
        foreach (var name in new[] { "page-flow-same-page-support", "page-flow-anchored-acceptance" })
        {
            var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
            var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "sha256.json")))!;
            foreach (var (path, hash) in hashes)
                Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, path)))));
        }
    }

    [Theory]
    [InlineData(false, "original", 0)] [InlineData(true, "original", 0)]
    [InlineData(false, "tone", 3807)] [InlineData(true, "tone", 3807)]
    [InlineData(false, "context", 956)] [InlineData(true, "context", 956)]
    public void Actual_pdf_rasters_keep_the_required_content_and_cross_page_reference(bool reverse, string variant, int raw)
    {
        using var input = new Input(variant, reverse); using var plan = input.Plan(); var before = plan.Budget.UsedBytes;
        if (variant == "tone")
        {
            using var expected = new AnchoredComparisonTests.Input(reverse, "tone");
            foreach (var side in new[] { PageSpace.A, PageSpace.B })
            for (var page = 1; page <= 2; page++)
            {
                using var actual = input.Read(new(side, page)); using var pixels = expected.Read(new(side, page));
                Assert.Equal(0, Cv2.Norm(actual, pixels, NormTypes.INF));
            }
        }
        // PDFから直接読む。A2のメモリ改変ヘルパーをこの検査へ渡さない。
        using (var c = plan.RenderContent(1, input.Read))
        using (var comparison = PageComparer.Compare(c.A, c.B, new()))
        { Assert.Equal(raw, comparison.RawPixels); Assert.Equal(raw == 0 ? 0 : 1, comparison.Clusters.Count); }
        using (var result = plan.Compare(input.Read, input.Text, out var reason))
        {
            Assert.Null(reason); Assert.NotNull(result); Assert.Equal(raw, result.Contents.Sum(c => c.RawPixels));
            Assert.Equal(raw == 0 ? 6 : 7, result.Aggregation.DifferenceCount); Assert.Equal(raw == 0 ? 2 : 3, result.Aggregation.AggregatedDifferenceCount);
            Assert.Equal(6, result.Structures.Count);
            using var d1 = result.ProjectDisplay(1); using var d2 = result.ProjectDisplay(2);
            Assert.Equal(raw, d1.DisplayRawPixels); Assert.Equal(variant == "context" ? 238 : 0, d2.DisplayRawPixels);
            Assert.All(d2.Parts, p => Assert.Equal(new ContentClusterKey(new(1), 1), p.Content));
            if (Environment.GetEnvironmentVariable("REPORTDIFF_ANCHORED_PDF_EVIDENCE") is { Length: > 0 } evidence)
                AnchoredComparisonTests.SaveEvidence(Path.Combine(evidence, variant + (reverse ? "-ba" : "-ab")), input.Read, plan, result);
        }
        Assert.Equal(before, plan.Budget.UsedBytes);
        using var output = new Output();
        var report = output.Compare(input, "on", "rows: {enabled: true, carry_enabled: true}");
        Assert.Equal("applied", report.PageFlow!.AnchoredContent!.Status);
        Assert.Equal(raw == 0 ? 6 : 7, report.Summary.DifferenceCount); Assert.Equal(raw == 0 ? 2 : 3, report.Summary.AggregatedDifferenceCount);
        Assert.Equal(raw == 0 ? 0 : 1, report.Summary.Clusters); Assert.Equal(raw, report.Pages.Sum(p => p.RawPixels));
        Assert.Equal(variant == "context" ? 1 : 0, report.Pages.Sum(p => p.ContentReferences!.Count));
        var baseline = output.Compare(input, "off", "rows: {enabled: true, carry_enabled: false}");
        foreach (var page in report.Pages)
            Assert.Equal(File.ReadAllBytes(output.PathOf("off", baseline.Pages[page.Page - 1].RawEvidence!.Overlay)),
                File.ReadAllBytes(output.PathOf("on", page.RawEvidence!.Overlay)));
    }

    [Theory]
    [InlineData(false, "too-different")] [InlineData(true, "too-different")]
    [InlineData(false, "cluster-limit")] [InlineData(true, "cluster-limit")]
    [InlineData(false, "second-too-different")] [InlineData(true, "second-too-different")]
    [InlineData(false, "second-cluster-limit")] [InlineData(true, "second-cluster-limit")]
    public void Incomplete_content_falls_back_as_a_whole_before_projecting_any_page(bool reverse, string variant)
    {
        using var input = new Input(variant, reverse); using var plan = input.Plan(); var before = plan.Budget.UsedBytes;
        using (var images = plan.RenderContent(variant.StartsWith("second-", StringComparison.Ordinal) ? 2 : 1, input.Read))
        using (var c = PageComparer.Compare(images.A, images.B, new()))
        {
            if (variant.EndsWith("too-different", StringComparison.Ordinal)) Assert.Equal("too_different", c.Status);
            else { Assert.Contains("CLUSTER_LIMIT", c.Warnings); Assert.Equal(500, c.Clusters.Count); }
        }
        Assert.Null(plan.Compare(input.Read, input.Text, out var reason)); Assert.Equal("anchored_content_incomplete", reason);
        Assert.Equal(before, plan.Budget.UsedBytes);
        using var output = new Output();
        var report = output.Compare(input, "on", "rows: {enabled: true, carry_enabled: true}");
        Assert.Equal("skipped", report.PageFlow!.AnchoredContent!.Status);
        Assert.Equal("anchored_content_incomplete", report.PageFlow.AnchoredContent.Reason);
        Assert.All(report.Pages, p => { Assert.Null(p.ContentDisplay); Assert.Null(p.ContentReferences); });
        var baseline = output.Compare(input, "off", "rows: {enabled: true, carry_enabled: false}");
        Assert.Equal(JsonSerializer.Serialize(baseline.Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
        Assert.DoesNotContain(Directory.GetFiles(output.PathOf("on"), "*", SearchOption.AllDirectories), p => p.Contains("_content_") || p.Contains("_ref_"));
    }

    [Theory]
    [InlineData("normal", true)] [InlineData("explicit-normal", true)]
    [InlineData("strict", false)] [InlineData("loose", false)]
    [InlineData("dpi150", false)] [InlineData("dpi600", false)]
    [InlineData("disabled", false)] [InlineData("strong-support", false)] [InlineData("strong-improvement", false)]
    public void Cli_settings_apply_by_effective_values_and_do_not_relax_strengthened_rows(string variant, bool expected)
    {
        using var input = new Input("tone", false); using var output = new Output();
        var yaml = variant switch
        {
            "disabled" => "rows: {enabled: true, carry_enabled: false}",
            "strong-support" => "rows: {enabled: true, carry_enabled: true, min_support_bands: 3}",
            "strong-improvement" => "rows: {enabled: true, carry_enabled: true, min_improvement: 1}",
            "explicit-normal" => "rows: {enabled: true, carry_enabled: true}\ndiff: {max_shift_mm: 0.15, color_threshold: 3, edge_tolerance: 0.3}\nink: {background_radius_mm: 1.5, contrast_threshold: 25}\ncluster: {merge_x_mm: 3, merge_y_mm: 1, min_pixels: 4, max_clusters_per_page: 500, max_diff_ratio: 0.3, reading_band_mm: 5}\nmove: {search_mm: 5, min_score: 0.98, min_score_gap: 0.02, template_margin_mm: 1}",
            _ => "rows: {enabled: true, carry_enabled: true}"
        };
        string[] args = variant switch
        { "strict" or "loose" or "normal" => ["--profile", variant], "dpi150" => ["--dpi", "150"], "dpi600" => ["--dpi", "600"], _ => [] };
        var report = output.Compare(input, variant, yaml, args);
        Assert.Equal(expected, report.PageFlow?.AnchoredContent?.Status == "applied");
        if (expected) { Assert.Equal(3807, report.Pages.Sum(p => p.RawPixels)); Assert.Equal(3, report.Summary.AggregatedDifferenceCount); }
        else Assert.All(report.Pages, p => { Assert.Null(p.ContentDisplay); Assert.Null(p.ContentReferences); });
        if (variant == "strong-improvement") Assert.Equal("page_1_low_improvement", report.PageFlow!.AnchoredContent!.Reason);
    }

    private sealed class Input : IDisposable
    {
        internal string A { get; } internal string B { get; }
        private readonly PdfReader a, b; private readonly PdfTextReader ta, tb; private readonly Size size;
        internal Input(string variant, bool reverse)
        {
            var root = variant == "original" ? Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-same-page-support/same-page-two")
                : Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-anchored-acceptance", variant);
            A = Path.Combine(root, reverse ? "b.pdf" : "a.pdf"); B = Path.Combine(root, reverse ? "a.pdf" : "b.pdf");
            a = PdfReader.Open(A); b = PdfReader.Open(B); ta = new(A); tb = new(B);
            using var first = a.ReadPage(1); size = first.Pixels.Size();
        }
        internal Mat Read(PageFlowPageKey key)
        { using var page = (key.Side == PageSpace.A ? a : b).ReadPage(key.Page); return page.TakePixels(); }
        internal RowTextResult Text(PageFlowPageKey key) => (key.Side == PageSpace.A ? ta : tb).ReadRowWords(key.Page, size, 300);
        internal AnchoredContentPlan Plan()
        {
            PageFlowPageKey[] keys = [new(PageSpace.A, 1), new(PageSpace.B, 1), new(PageSpace.A, 2), new(PageSpace.B, 2)];
            var collector = new PageFlowCollector(keys);
            foreach (var key in keys) { using var image = Read(key); Assert.True(collector.Add(key, image, Text(key), .5)); }
            var rows = new RowOptions { Enabled = true, CarryEnabled = true };
            var previous = PageFlowPlan.Prepare(collector.Complete()!, Read, new(), rows);
            var plan = AnchoredContentPlan.Prepare(previous, new(new(), new(), rows, new('a', 64), new('b', 64), new('c', 64)), out var reason);
            Assert.Null(reason); return Assert.IsType<AnchoredContentPlan>(plan);
        }
        public void Dispose() { ta.Dispose(); tb.Dispose(); a.Dispose(); b.Dispose(); }
    }
    private sealed class Output : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "reportdiff-acceptance-" + Guid.NewGuid().ToString("N"));
        internal Output() => Directory.CreateDirectory(root);
        internal string PathOf(string path, string? child = null) => child is null ? Path.Combine(root, path) : Path.Combine(root, path, child);
        internal ReportDocument Compare(Input input, string name, string yaml, params string[] options)
        {
            var config = PathOf(name + ".yaml"); File.WriteAllText(config, yaml + "\nreport: {raw_overlay: true}\n");
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var exit = CliApplication.Run(["compare", input.A, input.B, "--out", PathOf(name), "--config", config, "--quiet", .. options], stdout, stderr);
            Assert.Equal("", stderr.ToString()); Assert.Equal(1, exit);
            return JsonSerializer.Deserialize<ReportDocument>(File.ReadAllBytes(PathOf(name, "result.json")), ReportJson.Options)!;
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
