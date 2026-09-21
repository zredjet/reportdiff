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
public sealed class ParameterBehaviorTests
{
    [Fact]
    public void InkRadiusAndThresholdChangeClassificationWithoutChangingStrictDetection()
    {
        using var a = new Mat(100, 100, MatType.CV_8UC3, Scalar.All(255)); using var b = a.Clone();
        Cv2.Rectangle(a, new Rect(40, 40, 20, 20), Scalar.All(0), -1);
        var p = ConfigurationLoader.Load("diff: {max_shift_mm: 0, edge_tolerance: 0}\nmove: {search_mm: 0}").ForPage(1, 300);
        using var standard = PageComparer.Compare(a, b, p);
        using var narrow = PageComparer.Compare(a, b, p with { Ink = p.Ink with { BackgroundRadiusMm = .1 } });
        using var high = PageComparer.Compare(a, b, p with { Ink = p.Ink with { ContrastThreshold = 100 } });
        Assert.Equal("removed", Assert.Single(standard.Clusters).Kind);
        Assert.Equal("changed", Assert.Single(narrow.Clusters).Kind); Assert.Equal("changed", Assert.Single(high.Clusters).Kind);
        Assert.Equal(0, Cv2.Norm(standard.RawMask, narrow.RawMask, NormTypes.INF));
        Assert.Equal(0, Cv2.Norm(standard.RawMask, high.RawMask, NormTypes.INF));
    }

    [Fact]
    public void ReadingBandChangesNumbersAndEqualSizeLimitSelectionButNotRawPixels()
    {
        using var a = new Mat(200, 200, MatType.CV_8UC3, Scalar.All(255)); using var b = a.Clone();
        Cv2.Rectangle(b, new Rect(140, 20, 5, 5), Scalar.All(0), -1);
        Cv2.Rectangle(b, new Rect(20, 30, 5, 5), Scalar.All(0), -1);
        var p = ConfigurationLoader.Load("diff: {max_shift_mm: 0, edge_tolerance: 0}\ncluster: {merge_x_mm: 0, merge_y_mm: 0}\nmove: {search_mm: 0}").ForPage(1, 300);
        using var wide = PageComparer.Compare(a, b, p);
        using var narrow = PageComparer.Compare(a, b, p with { Cluster = p.Cluster with { ReadingBandMm = 1 } });
        Assert.Equal(20, wide.Clusters[0].Bounds.X); Assert.Equal(140, narrow.Clusters[0].Bounds.X);
        Assert.Equal(0, Cv2.Norm(wide.RawMask, narrow.RawMask, NormTypes.INF));
        using var wideLimited = PageComparer.Compare(a, b, p with { Cluster = p.Cluster with { MaxClustersPerPage = 1 } });
        using var narrowLimited = PageComparer.Compare(a, b, p with { Cluster = p.Cluster with { ReadingBandMm = 1, MaxClustersPerPage = 1 } });
        Assert.Equal(20, Assert.Single(wideLimited.Clusters).Bounds.X);
        Assert.Equal(140, Assert.Single(narrowLimited.Clusters).Bounds.X);
    }

    [Fact]
    public void TemplateMarginAndSharedInkControlMovementWithoutChangingDetection()
    {
        using var a = new Mat(400, 600, MatType.CV_8UC3, Scalar.All(255)); using var b = a.Clone();
        MovementTests.Draw(a, new(200, 160)); MovementTests.Draw(b, new(224, 160));
        using var standard = PageComparer.Compare(a, b, new());
        using var largeMargin = PageComparer.Compare(a, b, new() { Move = new() { TemplateMarginMm = 20 } });
        using var noInk = PageComparer.Compare(a, b, new() { Ink = new() { ContrastThreshold = 100 } });
        Assert.NotEmpty(standard.Clusters); Assert.All(standard.Clusters, c => Assert.Equal("moved", c.Kind));
        Assert.All(largeMargin.Clusters, c => Assert.NotEqual("moved", c.Kind));
        Assert.All(noInk.Clusters, c => Assert.NotEqual("moved", c.Kind));
        Assert.Equal(0, Cv2.Norm(standard.RawMask, largeMargin.RawMask, NormTypes.INF));
        MovementTests.AssertDetectionUnchanged(a, b, new() { Move = new() { TemplateMarginMm = 20 } }, largeMargin);
    }

    [Theory]
    [InlineData(512, 1)] [InlineData(512, 8)] [InlineData(1024, 4)]
    public void NondefaultCoarseResolutionAndRefinementCorrectTheKnownShift(int samples, int radius)
    {
        using var a = AlignmentFixture.Render(100); using var b = AlignmentFixture.Shift(a, 9, -6);
        var options = new AlignOptions { Enabled = true, CoarseMaxSideSamples = samples, RefineRadiusSamples = radius };
        var result = GlobalAligner.Estimate(a, b, new() { Dpi = 100 }, options);
        Assert.Equal("applied", result.Status); Assert.Equal(new(-9, 6), result.EstimatedShiftPx);
        using var corrected = GlobalAligner.TranslateB(b, result.EstimatedShiftPx!);
        Assert.Equal(0, Cv2.Norm(a, corrected, NormTypes.INF));
    }

    [Fact]
    public void SupportCountsAxesAndAreaAreIndependentlyApplied()
    {
        using var a = AlignmentFixture.Render(100);
        Cv2.Rectangle(a, new Rect(0, a.Height * 2 / 3, a.Width, a.Height - a.Height * 2 / 3), Scalar.All(255), -1);
        Cv2.Rectangle(a, new Rect(a.Width * 2 / 3, 0, a.Width - a.Width * 2 / 3, a.Height), Scalar.All(255), -1);
        using var b = AlignmentFixture.Shift(a, 9, 6);
        var p = new ComparisonParameters { Dpi = 100 }; var options = new AlignOptions { Enabled = true };
        var baseline = GlobalAligner.Estimate(a, b, p, options);
        Assert.Equal("applied", baseline.Status); Assert.Equal(4, baseline.SupportCells);
        foreach (var stricter in new[] { options with { MinSupportCells = 5 }, options with { MinSupportRows = 3 },
            options with { MinSupportColumns = 3 }, options with { MinInkAreaMm2 = 10000 } })
            Assert.Equal("insufficient_support", GlobalAligner.Estimate(a, b, p, stricter).Reason);
    }

    [Fact]
    public void RelaxedSupportCanAcceptASingleLocalObjectAsDocumented()
    {
        using var a = new Mat(600, 600, MatType.CV_8UC3, Scalar.All(255));
        Cv2.PutText(a, "12345", new(250, 290), HersheyFonts.HersheySimplex, 0.7, Scalar.All(0), 2);
        using var b = AlignmentFixture.Shift(a, 9, 6);
        var options = ConfigurationLoader.Load("align: {enabled: true, min_support_cells: 1, min_support_rows: 1, min_support_columns: 1}").Align;
        Assert.Equal("applied", GlobalAligner.Estimate(a, b, new() { Dpi = 100 }, options).Status);
    }

    [Theory]
    [InlineData(1, "…")] [InlineData(3, "A😀…")] [InlineData(4, "A😀BC")]
    public void TextLimitCountsUnicodeScalarsAndIncludesEllipsis(int maximum, string expected)
    {
        var result = TextAnnotations.Create([new("A😀BC", new(0, 0, 10, 10))], [new(1, new(0, 0, 20, 20), 1)], [], 300,
            new() { MaxRunesPerCluster = maximum });
        Assert.Equal(expected, result.TextByCluster[1]); Assert.Equal(maximum < 4 ? 1 : 0, result.Warnings.Count);
    }

    [Fact]
    public void TextLineOverlapUsesTheConfiguredRatio()
    {
        TextWord[] words = [new("左", new(0, 0, 10, 10)), new("右", new(20, 6, 10, 10))];
        DifferenceCluster[] clusters = [new(1, new(0, 0, 40, 30), 1)];
        Assert.Equal("左\n右", TextAnnotations.Create(words, clusters, [], 300).TextByCluster[1]);
        Assert.Equal("左 右", TextAnnotations.Create(words, clusters, [], 300, new() { MinLineOverlap = .4 }).TextByCluster[1]);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void PdfExtractionLimitsUseConfiguredCounts(bool letters)
    {
        using var files = new TextFile(PdfFixture.CreateTextPage([new("AAA BBB", 50, 220)]));
        var options = letters ? new TextOptions { MaxLettersPerPage = 3 } : new TextOptions { MaxWordsPerPage = 1 };
        using var reader = new PdfTextReader(files.Path, options);
        var result = reader.Annotate(1, new(240, 300), 72, [new(1, new(0, 0, 240, 300), 1)], []);
        Assert.Empty(result.TextByCluster);
        Assert.Contains(letters ? "文字要素が 3 件" : "単語が 1 件", Assert.Single(result.Warnings).Message);
        using var enough = new PdfTextReader(files.Path, new() { MaxLettersPerPage = 7, MaxWordsPerPage = 2 });
        Assert.Equal("AAA BBB", enough.Annotate(1, new(240, 300), 72, [new(1, new(0, 0, 240, 300), 1)], []).TextByCluster[1]);
    }

    [Fact]
    public async Task RealCliUsesCustomTextSettingsAndPreservesInputCommentsAndDetection()
    {
        using var files = new TextFile(PdfFixture.CreateTextPage([new("11123", 50, 220)]));
        var b = Path.Combine(files.DirectoryPath, "新.pdf");
        File.WriteAllBytes(b, PdfFixture.CreateTextPage([new("11128", 50, 220)]));
        var config = Path.Combine(files.DirectoryPath, "コメント付き.yaml");
        var yaml = "dpi: 144\n" + ExternalConfigurationTests.CustomYaml;
        File.WriteAllText(config, yaml); var bytes = File.ReadAllBytes(config);
        var output = Path.Combine(files.DirectoryPath, "結果");
        var run = await CliProcess.Run("compare", files.Path, b, "--config", config, "--out", output);
        Assert.Equal(1, run.Code); Assert.Empty(run.Error); Assert.Equal(bytes, File.ReadAllBytes(config));
        var report = JsonSerializer.Deserialize<ReportDocument>(File.ReadAllText(Path.Combine(output, "result.json")), ReportJson.Options)!;
        Assert.Equal(JsonSerializer.Serialize(ConfigurationLoader.Load(yaml).ToReportConfiguration(), ReportJson.Options),
            JsonSerializer.Serialize(report.Config, ReportJson.Options));
        var cluster = Assert.Single(Assert.Single(report.Pages).Clusters);
        Assert.Equal("111…", cluster.TextA); Assert.Equal("111…", cluster.TextB);
        Assert.Equal(2, report.Warnings.Count(w => w.Code == "TEXT_ANNOTATION_TRUNCATED"));
        using var pdfA = PdfReader.Open(files.Path); using var pdfB = PdfReader.Open(b);
        using var aImage = pdfA.ReadPage(1, 144); using var bImage = pdfB.ReadPage(1, 144);
        using var baseline = PageComparer.Compare(aImage.Pixels, bImage.Pixels, ConfigurationLoader.Load(yaml).ForPage(1, 144));
        Assert.Equal(baseline.RawPixels, report.Pages[0].RawPixels);
        Assert.Equal(baseline.Clusters.Single().Pixels, cluster.Pixels);
        var html = File.ReadAllText(Path.Combine(output, "report.html"));
        foreach (var label in new[] { "インクの局所背景半径", "インクの明度差のしきい値", "クラスタの読み順の帯幅", "移動テンプレートの余白",
            "全体補正の粗い格子の長辺上限", "全体補正の復元段階の探索半径", "全体補正の支持区画数の下限", "全体補正の支持行数の下限",
            "全体補正の支持列数の下限", "全体補正の区画内の黒換算面積", "PDF 注釈の文字要素数上限", "PDF 注釈の単語数上限",
            "PDF 注釈の本文上限", "PDF 注釈の行の重なり率の下限" }) Assert.Contains(label, html);
        var displayed = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("512 要素", displayed); Assert.Contains("4 要素", displayed);
        Assert.Contains("2.5 mm²", displayed); Assert.Contains("4 Unicode 文字", displayed);
    }
}
