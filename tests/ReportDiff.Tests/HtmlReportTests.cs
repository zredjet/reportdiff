using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

public sealed class HtmlReportTests
{
    [Fact]
    public void OfflineHtmlContainsSummaryMetricsWarningsSettingsAndAllCrops()
    {
        var report = Example();
        var html = HtmlReportWriter.Render(report);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotMatch("<script[^>]+src=", html);
        Assert.Contains("<html lang=\"ja\">", html);
        Assert.Contains("<style>", html);
        Assert.Contains("<dt>相違箇所</dt><dd>1</dd>", html);
        Assert.Contains("<dt>位置ずれを吸収した数</dt><dd>7</dd>", html);
        Assert.Contains("<dt>除外したノイズの数</dt><dd>3</dd>", html);
        Assert.Contains("<dt>吸収に使った最大ずれ</dt><dd>2 px</dd>", html);
        Assert.Contains("右と下を白で埋め", html);
        Assert.Contains("<code>SIZE_MISMATCH</code>", html);
        Assert.Contains("<details class=\"card settings\"><summary>", html);
        Assert.Contains("<dt>PDF の描画解像度</dt><dd>144 dpi</dd>", html);
        Assert.Contains("<dt>画像の換算解像度</dt><dd>200 dpi</dd>", html);
        Assert.Contains("<dt>コントラスト比例の許容係数</dt><dd>0.2</dd>", html);
        Assert.Contains("<td>すべて</td>", html);
        Assert.Contains("出力日時", html);
        Assert.Contains("ページ限定", html);
        Assert.Contains("X 1.25 · Y 2.50<br>幅 3.75 × 高さ 4.00", html);
        Assert.Contains("<td>81</td>", html);
        foreach (var path in new[] { "crops/p001_c001_a.png", "crops/p001_c001_b.png", "crops/p001_c001_diff.png" })
            Assert.Contains($"class=\"crop\" src=\"{path}\"", html);
        Assert.Contains("class=\"page-image\" src=\"pages/p001_overlay.png\"", html);
        AssertEmbeddedReport(report, html);
    }

    [Fact]
    public void UntrustedTextIsEscapedWithoutLosingEmbeddedJsonContent()
    {
        const string hostile = "日本語 </script><script>alert('x')</script> \" & https://example.invalid/ http://example.invalid/";
        var report = Example();
        report = report with
        {
            Inputs = report.Inputs with { A = report.Inputs.A with { Path = hostile } },
            Warnings = [new(hostile, hostile)],
            Config = report.Config with { Exclude = [new(null, 1, 2, 3, 4, hostile)] }
        };
        var html = HtmlReportWriter.Render(report);
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.Contains(hostile, WebUtility.HtmlDecode(html));
        Assert.Equal(2, Regex.Matches(html, "</script>").Count);
        AssertEmbeddedReport(report, html);
    }

    [Theory]
    [InlineData("https://example.invalid/p.png")]
    [InlineData("http://example.invalid/p.png")]
    [InlineData("//example.invalid/p.png")]
    [InlineData("/pages/p.png")]
    [InlineData("pages/../p.png")]
    [InlineData("pages/%2e%2e/p.png")]
    [InlineData("pages\\p.png")]
    [InlineData("pages/p.png?url=x")]
    [InlineData("pages/p.png\" onerror=\"alert(1)")]
    [InlineData("data:image/png;base64,xxx")]
    public void ImageReferencesMustBeLocalOutputPaths(string path)
    {
        var report = Example();
        var page = report.Pages[0];
        Assert.Throws<ReportWriteException>(() => HtmlReportWriter.Render(report with
        {
            Pages = [page with { Images = page.Images with { A = path } }]
        }));
        var cluster = page.Clusters[0];
        Assert.Throws<ReportWriteException>(() => HtmlReportWriter.Render(report with
        {
            Pages = [page with { Clusters = [cluster with { Crops = cluster.Crops with { Diff = path } }] }]
        }));
    }

    [Fact]
    public void SamePageWithoutSavedImagesKeepsMetricsAndCanBeExpanded()
    {
        var report = Example();
        report = report with
        {
            Summary = new("same", 1, 0, 0, 7), Warnings = [],
            Pages = [report.Pages[0] with { Status = "same", Images = new(null, null, null), Clusters = [] }]
        };
        var html = HtmlReportWriter.Render(report);
        Assert.Contains("相違なし", html);
        Assert.Contains("<details class=\"card page\" id=\"page-1\"><summary>", html);
        Assert.Contains("相違のないページの画像は保存されていません。", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("<dt>位置ずれを吸収した数</dt><dd>7</dd>", html);
        Assert.Contains("警告はありません。", html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnpairedPageShowsExistingSideAndDisablesMissingSides(bool onlyA)
    {
        var report = Example();
        var images = onlyA ? new PageImages("pages/p001_a.png", null, null) : new PageImages(null, "pages/p001_b.png", null);
        report = report with { Pages = [report.Pages[0] with { Status = onlyA ? "only_in_a" : "only_in_b", Images = images, Clusters = [] }] };
        var html = HtmlReportWriter.Render(report);
        Assert.Contains(onlyA ? "A のみに存在" : "B のみに存在", html);
        Assert.Contains("対応するページがないため", html);
        Assert.Equal(2, Regex.Matches(html, " disabled>").Count);
        Assert.Contains($"class=\"page-image\" src=\"{(onlyA ? images.A : images.B)}\"", html);
        Assert.DoesNotContain("class=\"crop\"", html);
    }

    [Fact]
    public void TooDifferentPageExplainsOmittedClustering()
    {
        var report = Example();
        var html = HtmlReportWriter.Render(report with { Pages = [report.Pages[0] with { Status = "too_different", Clusters = [] }] });
        Assert.Contains("差分の割合が上限を超えたため", html);
        Assert.Contains("重ね描き", html);
        Assert.DoesNotContain("class=\"clusters\"", html);
    }

    [Fact]
    public void MillimetersAndSettingsUseStableDecimalSeparators()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var html = HtmlReportWriter.Render(Example());
            Assert.Contains("X 1.25 · Y 2.50", html);
            Assert.Contains("<dd>0.2</dd>", html);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void CompletedReportWritesUtf8HtmlWithoutChangingJsonOrOverwritingFiles()
    {
        using var directory = new ReportTestDirectory();
        using var image = new Mat(16, 24, MatType.CV_8UC3, Scalar.All(255));
        using var pair = PageNormalizer.Normalize(image, image);
        using var comparison = PageComparer.Compare(image, image, new());
        var fixture = Example();
        var writer = new ReportWriter(directory.Output, fixture.Inputs, fixture.Config, saveAllPages: true);
        writer.AddComparedPage(1, pair, comparison, 144);
        var report = writer.Complete();
        var jsonPath = Path.Combine(directory.Output, "result.json");
        var json = File.ReadAllBytes(jsonPath);
        HtmlReportWriter.Write(directory.Output, report);
        var htmlPath = Path.Combine(directory.Output, "report.html");
        var bytes = File.ReadAllBytes(htmlPath);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        var html = new UTF8Encoding(false, true).GetString(bytes);
        Assert.Equal(HtmlReportWriter.Render(report), html);
        AssertEmbeddedReport(report, html);
        Assert.Contains("class=\"page-image\"", html);
        Assert.Throws<ReportWriteException>(() => HtmlReportWriter.Write(directory.Output, report));
        Assert.Equal(bytes, File.ReadAllBytes(htmlPath));
        Assert.Equal(json, File.ReadAllBytes(jsonPath));
    }

    [Fact]
    public void WriteFailureHasJapaneseMessageAndInvalidPathDoesNotCreateHtml()
    {
        using var directory = new ReportTestDirectory();
        Assert.Contains("書き込めません", Assert.Throws<ReportWriteException>(() => HtmlReportWriter.Write(directory.Output, Example())).Message);
        Directory.CreateDirectory(directory.Output);
        var report = Example();
        report = report with { Pages = [report.Pages[0] with { Images = new("../external.png", null, null) }] };
        Assert.Throws<ReportWriteException>(() => HtmlReportWriter.Write(directory.Output, report));
        Assert.False(File.Exists(Path.Combine(directory.Output, "report.html")));
    }

    private static void AssertEmbeddedReport(ReportDocument report, string html)
    {
        var match = Regex.Match(html, "<script type=\"application/json\" id=\"result\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(report, ReportJson.Options), JsonNode.Parse(match.Groups[1].Value)));
    }

    private static ReportDocument Example()
    {
        var config = ConfigurationLoader.Load("""
            dpi: 144
            image_dpi: 200
            diff: { edge_tolerance: 0.2 }
            exclude:
              - { page: all, x: 2, y: 3, w: 4, h: 5, note: 出力日時 }
              - { page: 2, x: 1, y: 1, w: 1, h: 1, note: ページ限定 }
            """).ToReportConfiguration();
        var cluster = new ReportCluster(1, new(10, 20, 30, 40), new(1.25, 2.5, 3.75, 4), 81, 0.1,
            null, null, null, null, new("crops/p001_c001_a.png", "crops/p001_c001_b.png", "crops/p001_c001_diff.png"));
        return new(1, new("reportdiff", "0.1.0"), new DateTimeOffset(2026, 9, 21, 12, 34, 56, TimeSpan.FromHours(9)),
            new(new("旧 帳票.png", "png", 1, new string('a', 64)), new("新 帳票.png", "png", 1, new string('b', 64))),
            config, new("different", 1, 1, 1, 7), [new("SIZE_MISMATCH", "サイズが異なります。右と下を白で埋めました。")],
            [new(1, "different", new(200, 160), true, 84, 3, 7, 2,
                new("pages/p001_a.png", "pages/p001_b.png", "pages/p001_overlay.png"), [cluster])]);
    }
}
