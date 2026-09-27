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
public sealed partial class PageFlowCliTests
{
    public static IEnumerable<object[]> NumericCases()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-numeric/expected.json")));
        return json.RootElement.EnumerateArray().Select(r => new object[] { r.GetProperty("id").GetString()!, r.GetProperty("reverse").GetBoolean(),
            r.GetProperty("pairs").GetInt32(), r.GetProperty("ready").GetBoolean(), r.GetProperty("applied").GetBoolean(),
            r.GetProperty("count").GetInt32(), r.GetProperty("aggregate").GetInt32() }).ToArray();
    }

    [Theory]
    [MemberData(nameof(NumericCases))]
    public void Numeric_correspondence_preserves_fixed_content_and_baseline_fallback(string scenario, bool reverse, int pairs, bool ready, bool applied, int count, int aggregate)
    {
        using var files = new Files("numeric-" + scenario, reverse);
        Assert.Equal(1, files.Run("on", true).Code); Assert.Equal(1, files.Run("off", false).Code);
        var report = files.Report("on"); var off = files.Report("off"); var flow = report.PageFlow!;
        Assert.Equal(ready, flow.RangeReady); Assert.Equal(applied ? "applied" : "skipped", flow.Status);
        Assert.Equal(count, report.Summary.DifferenceCount); Assert.Equal(aggregate, report.Summary.AggregatedDifferenceCount);
        Assert.Equal(pairs, flow.NumericMatches?.Count ?? 0); Assert.True(report.Summary.AggregatedDifferenceCountComplete);
        foreach (var pair in flow.NumericMatches ?? [])
        {
            Assert.Equal(applied ? "applied" : "not_applied", pair.Status); Assert.False(pair.UsedAsExactSupport); Assert.False(pair.PixelEqualityProven);
            Assert.NotEqual(pair.A.Text, pair.B.Text); Assert.Equal(pair.A.Original.Page, pair.B.Original.Page);
            Assert.NotEmpty(pair.ChangedTokenIndices); Assert.Equal(2, pair.Anchors.Count);
            foreach (var anchor in pair.Anchors)
            {
                Assert.Equal(anchor.A.Text, anchor.B.Text); Assert.Equal(pair.A.Original.Page, anchor.A.Original.Page);
                Assert.Equal(pair.B.Original.BoundsPx.Y - pair.A.Original.BoundsPx.Y, anchor.B.Original.BoundsPx.Y - anchor.A.Original.BoundsPx.Y);
            }
        }
        if (!applied)
        {
            Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
            Assert.Empty(flow.Aggregation.Groups);
        }
        else
        {
            using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-numeric/expected.json")));
            var row = expected.RootElement.EnumerateArray().Single(r => r.GetProperty("id").GetString() == scenario && r.GetProperty("reverse").GetBoolean() == reverse);
            foreach (var range in row.GetProperty("numeric_ranges").EnumerateArray())
            {
                var page = report.Pages.Single(p => p.Page == range.GetProperty("page").GetInt32());
                Assert.Equal(range.GetProperty("raw_pixels").GetInt32(), page.RawPixels);
                Assert.Equal(range.GetProperty("clusters").GetInt32(), page.Clusters.Count);
            }
        }
        if (scenario == "weak-actual-support")
        {
            Assert.True(ready); Assert.False(applied);
            Assert.Equal("support_ink", flow.Pages[1].Adoption!.Detail);
        }
        foreach (var (p, q) in report.Pages.Zip(off.Pages))
        foreach (var (x, y) in new[] { (p.RawEvidence!.A.Image, q.RawEvidence!.A.Image), (p.RawEvidence.B.Image, q.RawEvidence.B.Image), (p.RawEvidence.Overlay, q.RawEvidence.Overlay) })
            Assert.Equal(File.ReadAllBytes(files.PathOf("on", x)), File.ReadAllBytes(files.PathOf("off", y)));
        var html = File.ReadAllText(files.PathOf("on", "report.html"));
        if (pairs > 0) { Assert.Contains("数値変更を含む行対応", html); Assert.Contains("変更行を完全一致の支持には数えていません", html); }
    }

    [Theory]
    [InlineData("1-2", "", "")]
    [InlineData("1", "", "")]
    [InlineData("", "", "exclude: [{page: 2, x: 13, y: 35, w: 30, h: 14}]")]
    [InlineData("", "", "regions: [{page: 2, name: excluded, mode: exclude, x: 13, y: 35, w: 30, h: 14}]")]
    [InlineData("", "min_support_ink_mm2: 1000", "")]
    [InlineData("", "min_improvement: 1", "")]
    [InlineData("", "", "text: {max_words_per_page: 1}")]
    public void Numeric_selection_exclusion_and_failed_adoption_keep_baseline(string selection, string rows, string yaml)
    {
        using var files = new Files("numeric-terminal-number"); var args = selection.Length == 0 ? Array.Empty<string>() : ["--pages", selection];
        Assert.Equal(1, files.Run("on", true, rows, args, extraYaml: yaml).Code);
        Assert.Equal(1, files.Run("off", false, rows, args, extraYaml: yaml).Code);
        var report = files.Report("on"); var off = files.Report("off");
        Assert.Equal("skipped", report.PageFlow!.Status); Assert.Empty(report.PageFlow.Aggregation.Groups);
        Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
        if (selection.Length > 0 || yaml.Length > 0) Assert.Null(report.PageFlow.NumericMatches);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Numeric_compare_region_and_no_regions_keep_original_content(bool disable)
    {
        using var files = new Files("numeric-terminal-number");
        var yaml = disable ? "exclude: [{page: 2, x: 13, y: 35, w: 30, h: 14}]"
            : "regions: [{page: 2, name: numeric, mode: compare, x: 35, y: 54, w: 8, h: 3, diff: {max_shift_mm: 0, edge_tolerance: 0}}]";
        Assert.Equal(1, files.Run("on", true, extraArgs: disable ? ["--no-regions"] : [], extraYaml: yaml).Code);
        var report = files.Report("on"); Assert.Equal("applied", report.PageFlow!.Status);
        Assert.True(report.Pages[1].RawPixels >= 40); Assert.NotEmpty(report.Pages[1].Clusters);
        Assert.Single(report.PageFlow.NumericMatches!);
    }

    [Fact]
    public void Numeric_input_update_failure_keeps_previous_output()
    {
        using var files = new Files("numeric-terminal-number"); Directory.CreateDirectory(files.PathOf("changed"));
        File.WriteAllText(files.PathOf("changed", "old.txt"), "old"); var mutation = new InputMutationAttempt(files.B);
        using var writer = new HookWriter(line =>
        { if (!mutation.Attempted && line.StartsWith("処理中 1 /", StringComparison.Ordinal)) mutation.AppendPdfComment(); });
        var result = files.Run("changed", true, extraArgs: ["--force"], writer: writer);
        mutation.AssertCliError(result.Code, result.Error);
        Assert.Equal("old", File.ReadAllText(files.PathOf("changed", "old.txt"))); Assert.Empty(Directory.GetDirectories(files.Root, ".reportdiff-stage-*"));
    }

    [Fact]
    public void Numeric_compare_dir_inherits_settings_and_reports_two_counts()
    {
        using var files = new Files("numeric-terminal-number");
        var a = Path.Combine(files.Root, "a"); var b = Path.Combine(files.Root, "b"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.Copy(files.A, Path.Combine(a, "帳票.pdf")); File.Copy(files.B, Path.Combine(b, "帳票.pdf"));
        var config = Path.Combine(files.Root, "common.yaml"); File.WriteAllText(config, "rows: {enabled: true}\n");
        File.WriteAllText(Path.Combine(files.Root, "selected.yaml"), "rows: {carry_enabled: true}\n");
        var rules = Path.Combine(files.Root, "rules.yaml"); File.WriteAllText(rules, "schema_version: 1\nrules: [{pattern: '帳票', config: selected.yaml}]\n");
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        Assert.Equal(1, CliApplication.Run(["compare-dir", a, b, "--out", files.PathOf("batch"), "--config", config, "--rules", rules], stdout, stderr));
        Assert.Empty(stderr.ToString());
        var index = JsonSerializer.Deserialize<DirectoryReportDocument>(File.ReadAllText(files.PathOf("batch", "index.json")), ReportJson.Options)!;
        var item = Assert.Single(index.Files); Assert.Equal(6, item.Comparison!.DifferenceCount); Assert.Equal(2, item.Comparison.AggregatedDifferenceCount);
        var result = Directory.GetFiles(files.PathOf("batch"), "result.json", SearchOption.AllDirectories).Single();
        var report = JsonSerializer.Deserialize<ReportDocument>(File.ReadAllBytes(result), ReportJson.Options)!;
        Assert.Equal(6, report.Summary.DifferenceCount); Assert.Equal(2, report.Summary.AggregatedDifferenceCount);
        Assert.Single(report.PageFlow!.NumericMatches!);
    }

    [Theory]
    [InlineData("numeric-down", false, true, 2)] [InlineData("numeric-down", true, true, 2)]
    [InlineData("numeric-up", false, true, 3)] [InlineData("numeric-up", true, true, 3)]
    [InlineData("numeric-variable", false, true, 2)] [InlineData("numeric-variable", true, false, 0)]
    [InlineData("numeric-multiple", false, true, 3)] [InlineData("numeric-multiple", true, false, 0)]
    [InlineData("numeric-weak", false, false, 0)] [InlineData("numeric-weak", true, false, 0)]
    [InlineData("numeric-horizontal", false, false, 0)] [InlineData("numeric-horizontal", true, false, 0)]
    public void Numeric_global_composition_keeps_original_evidence_and_content(string scenario, bool reverse, bool applied, int aggregate)
    {
        using var files = new Files("numeric-" + scenario, reverse);
        Assert.Equal(1, files.Run("on", true, extraYaml: "align: {enabled: true}").Code);
        Assert.Equal(1, files.Run("off", false, extraYaml: "align: {enabled: true}").Code);
        var report = files.Report("on"); var off = files.Report("off");
        Assert.Equal(applied ? "applied" : "skipped", report.PageFlow!.Status);
        if (applied)
        {
            Assert.Equal(aggregate, report.Summary.AggregatedDifferenceCount);
            Assert.Equal(scenario == "numeric-up" ? 2 : 1, report.Summary.Clusters);
            var proof = Assert.Single(report.PageFlow.NumericMatches!);
            Assert.False(proof.UsedAsExactSupport);
            var page = report.Pages.Single(p => p.Page == proof.A.Original.Page);
            Assert.Equal(reverse ? -100 : 100, proof.B.Original.BoundsPx.Y + page.GlobalShiftPx!.Dy - proof.A.Original.BoundsPx.Y);
            Assert.All(proof.Anchors, a => Assert.Equal(a.A.Text, a.B.Text));
        }
        else
        {
            Assert.Empty(report.PageFlow.Aggregation.Groups);
            Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
            if (scenario == "numeric-weak")
            {
                // この追加PDFは第2ページの全体補正が低スコアになり、支持評価より前に見送る。
                // support_inkの反例は補正なしの固定weak-actual-supportで別に要求する。
                Assert.Contains("fixed_parts_support", report.PageFlow.Reasons);
                Assert.Equal("low_score", report.Pages[1].Alignment.Reason);
                Assert.All(report.PageFlow.Pages, p => Assert.Null(p.Adoption));
            }
        }
        foreach (var (a, b) in report.Pages.Zip(off.Pages))
            Assert.Equal(File.ReadAllBytes(files.PathOf("on", a.RawEvidence!.Overlay)), File.ReadAllBytes(files.PathOf("off", b.RawEvidence!.Overlay)));
    }

    [Fact]
    public void Numeric_original_text_is_html_escaped()
    {
        using var files = new Files("numeric-terminal-number"); Assert.Equal(1, files.Run("on", true).Code);
        var report = files.Report("on"); var proof = Assert.Single(report.PageFlow!.NumericMatches!);
        var changed = proof with { A = proof.A with { Text = "<script>unsafe</script> & 555" } };
        Directory.CreateDirectory(files.PathOf("escaped"));
        HtmlReportWriter.Write(files.PathOf("escaped"), report with { PageFlow = report.PageFlow with { NumericMatches = [changed] } });
        var html = File.ReadAllText(files.PathOf("escaped", "report.html"));
        Assert.Contains("&lt;script&gt;unsafe&lt;&#47;script&gt; &amp; 555", html); Assert.DoesNotContain("<script>unsafe", html);
    }
}

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class PageFlowNumericCoreTests
{
    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(-6)]
    public void Numeric_proofs_keep_original_text_coordinates_and_budget(int dy)
    {
        using var fixture = new NumericInput("terminal-number", dy);
        var document = fixture.Document(); var plan = PageFlowPlan.Prepare(document, fixture.Read, new(), new() { Enabled = true });
        Assert.True(plan.Decision.Ready); var proof = Assert.Single(plan.NumericRows.Proofs);
        Assert.Equal("SUM TOTAL 555", proof.A.Text); Assert.Equal("SUM TOTAL 556", proof.B.Text);
        Assert.Equal(proof.B.Band.Top + dy, proof.B.OriginalBand.Top);
        Assert.Equal("SUM TOTAL 556", plan.Inference.Layouts.B[1].Body[^1].Text);
        Assert.All(proof.Anchors, a => { Assert.Equal(a.A.Text, a.B.Text); Assert.Equal(a.B.Band.Top + dy, a.B.OriginalBand.Top); });
        var exact = new PageFlowDocumentDescriptor(document.Pages.ToArray(), document.Usage with { DescriptorBytes = PageFlowLimits.MaximumDescriptorBytes - plan.NumericRows.DescriptorBytes });
        var accepted = PageFlowPlan.Prepare(exact, fixture.Read, new(), new() { Enabled = true });
        Assert.True(accepted.Decision.Ready); Assert.Equal(PageFlowLimits.MaximumDescriptorBytes, accepted.Usage.DescriptorBytes);
        var exceeded = new PageFlowDocumentDescriptor(document.Pages.ToArray(), exact.Usage with { DescriptorBytes = exact.Usage.DescriptorBytes + 1 });
        var rejected = PageFlowPlan.Prepare(exceeded, fixture.Read, new(), new() { Enabled = true });
        Assert.False(rejected.Decision.Ready); Assert.Empty(rejected.NumericRows.Proofs); Assert.Contains("flow_descriptor_limit", rejected.Decision.Reasons);
        Assert.Throws<ArgumentException>(() => PageFlowNonflow.Find(exact, plan.Inference, false, plan.NumericRows));
        using var a = fixture.Read(new(PageSpace.A, 1)); using var b = fixture.Read(new(PageSpace.B, 1));
        Assert.Throws<ArgumentException>(() => PageFlowSurface.Create(plan.Inference.Layouts.A[0] with { Body = [] }, plan.Inference.Layouts.B[0], a, b, [], new(), plan.NumericRows));
    }

    [Fact]
    public void Missing_duplicate_modified_or_reordered_evidence_never_creates_numeric_proof()
    {
        using var fixture = new NumericInput("terminal-number"); var document = fixture.Document();
        var inference = PageFlowInference.Find(document, new(), 300);
        var valid = PageFlowNumericRows.Find(document, inference, false, _ => new(), new());
        foreach (var fault in new[] { "missing", "duplicate", "text", "position", "order" })
        foreach (var index in new[] { 2, 3, 4 })
        {
            var layout = inference.Layouts.B[1]; var body = layout.Body.ToList();
            if (fault == "missing") body.RemoveAt(index);
            if (fault == "duplicate") body.Add(body[index]);
            if (fault == "text") body[index] = body[index] with { Text = "CHANGED TEXT" };
            if (fault == "position") body[index] = body[index] with { Baseline = body[index].Baseline + 1 };
            if (fault == "order") (body[index], body[0]) = (body[0], body[index]);
            var altered = inference with { Layouts = inference.Layouts with { B = [inference.Layouts.B[0], layout with { Body = body }] } };
            Assert.Empty(PageFlowNumericRows.Find(document, altered, false, _ => new(), new()).Proofs);
            Assert.Throws<ArgumentException>(() => PageFlowNonflow.Find(document, altered, false, valid));
        }
        Assert.Empty(PageFlowNumericRows.Find(document, inference, true, _ => new(), new()).Proofs);
        var shuffled = inference with { Layouts = inference.Layouts with { A = inference.Layouts.A.Reverse().ToArray(), B = inference.Layouts.B.Reverse().ToArray() } };
        Assert.Equal(JsonSerializer.Serialize(PageFlowNumericRows.Find(document, inference, false, _ => new(), new()).Proofs, ReportJson.Options),
            JsonSerializer.Serialize(PageFlowNumericRows.Find(document, shuffled, false, _ => new(), new()).Proofs, ReportJson.Options));
    }

    [Fact]
    public void Numeric_and_nonflow_proofs_share_one_metadata_budget()
    {
        using var fixture = new NumericInput("multiple-terminal"); var document = fixture.Document();
        var plan = PageFlowPlan.Prepare(document, fixture.Read, new(), new() { Enabled = true });
        Assert.True(plan.Decision.Ready); Assert.NotEmpty(plan.Nonflow.Proofs); Assert.Single(plan.NumericRows.Proofs);
        var added = plan.Nonflow.DescriptorBytes + plan.NumericRows.DescriptorBytes;
        var limited = new PageFlowDocumentDescriptor(document.Pages.ToArray(), document.Usage with { DescriptorBytes = PageFlowLimits.MaximumDescriptorBytes - added });
        Assert.True(PageFlowPlan.Prepare(limited, fixture.Read, new(), new() { Enabled = true }).Decision.Ready);
        var over = new PageFlowDocumentDescriptor(document.Pages.ToArray(), limited.Usage with { DescriptorBytes = limited.Usage.DescriptorBytes + 1 });
        var rejected = PageFlowPlan.Prepare(over, fixture.Read, new(), new() { Enabled = true });
        Assert.False(rejected.Decision.Ready); Assert.Empty(rejected.NumericRows.Proofs); Assert.Empty(rejected.Nonflow.Proofs);
    }

    internal sealed class NumericInput : IDisposable
    {
        private readonly PdfReader a, b; private readonly PdfTextReader ta, tb; private readonly int dy;
        internal NumericInput(string scenario, int dy = 0, string group = "page-flow-numeric")
        {
            this.dy = dy; var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", group, scenario);
            a = PdfReader.Open(Path.Combine(path, "a.pdf")); b = PdfReader.Open(Path.Combine(path, "b.pdf"));
            ta = new(Path.Combine(path, "a.pdf")); tb = new(Path.Combine(path, "b.pdf"));
        }
        internal PageFlowDocumentDescriptor Document()
        {
            var keys = Enumerable.Range(1, a.PageCount).SelectMany(n => new[] { new PageFlowPageKey(PageSpace.A, n), new PageFlowPageKey(PageSpace.B, n) }).ToArray();
            var collector = new PageFlowCollector(keys);
            foreach (var key in keys)
            {
                using var image = Read(key); var text = (key.Side == PageSpace.A ? ta : tb).ReadRowWords(key.Page, image.Size(), 300);
                Assert.True(key.Side == PageSpace.B && dy != 0 ? collector.AddAligned(key, image, new(0, -dy), text, .5) : collector.Add(key, image, text, .5));
            }
            return collector.Complete()!;
        }
        internal Mat Read(PageFlowPageKey key)
        {
            using var image = (key.Side == PageSpace.A ? a : b).ReadPage(key.Page, 300);
            return key.Side == PageSpace.B && dy != 0 ? PageMap.Global(image.Pixels.Size(), image.Pixels.Size(), image.Pixels.Size(), new(0, dy)).Render(image.Pixels, PageSpace.B) : image.Pixels.Clone();
        }
        public void Dispose() { ta.Dispose(); tb.Dispose(); a.Dispose(); b.Dispose(); }
    }
}
