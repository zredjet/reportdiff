using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

public sealed class PdfCliIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSecondPageWithExclusionProducesOneClusterAndCompleteReport(bool reverse)
    {
        using var files = new PdfComparisonFiles();
        var inputA = reverse ? files.NewPdf : files.OldPdf;
        var inputB = reverse ? files.OldPdf : files.NewPdf;
        var result = await CliProcess.Run("compare", inputA, inputB, "--out", files.Output, "--config", files.Config);
        Assert.Equal(1, result.Code); Assert.Empty(result.Error);
        Assert.Equal($"相違あり: 2 ページ中 1 ページ、1 箇所 → {Path.Combine(files.Output, "report.html")}{Environment.NewLine}", result.Output);
        var report = files.ReadReport();
        AssertInputs(report, inputA, inputB);
        Assert.Equal("different", report.Summary.Status);
        Assert.Equal(2, report.Summary.PagesCompared); Assert.Equal(1, report.Summary.PagesDifferent);
        Assert.Equal(1, report.Summary.Clusters); Assert.Empty(report.Warnings);
        Assert.Equal(new[] { 1, 2 }, report.Pages.Select(p => p.Page));
        AssertSamePage(report.Pages[0]);
        var page = report.Pages[1];
        Assert.Equal("different", page.Status); Assert.False(page.SizeMismatch);
        AssertPageSize(page.SizePx);
        var cluster = Assert.Single(page.Clusters);
        Assert.Equal(1, cluster.Id);
        AssertVisibleChange(cluster);
        Assert.True(cluster.Pixels > 1000);
        var exclusion = Assert.Single(report.Config.Exclude);
        Assert.Equal(new ReportExclusion(2, 49, 7, 16, 7, "出力日時"), exclusion);
        // CLI で strict や低 DPI に上書きせず、通常の既定値で全処理を通す。
        Assert.Equal(300, report.Config.Dpi); Assert.Equal(300, report.Config.ImageDpi);
        Assert.Equal(0.15, report.Config.Diff.MaxShiftMm); Assert.Equal(0.3, report.Config.Diff.EdgeTolerance);
        Assert.Equal(new PageImages("pages/p002_a.png", "pages/p002_b.png", "pages/p002_overlay.png"), page.Images);
        Assert.Equal(new ClusterCrops("crops/p002_c001_a.png", "crops/p002_c001_b.png", "crops/p002_c001_diff.png"), cluster.Crops);
        var images = new[] { page.Images.A!, page.Images.B!, page.Images.Overlay!, cluster.Crops.A, cluster.Crops.B, cluster.Crops.Diff };
        AssertOutputFiles(files.Output, images);
        AssertHtmlMatchesJson(files.Output, report, images);
        AssertImageContents(files.Output, page, cluster, report.Config.Dpi);
        Assert.Empty(Directory.EnumerateDirectories(files.Root, ".reportdiff-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdenticalTwoPagePdfReturnsZeroAndDoesNotSavePageImages(bool revised)
    {
        using var files = new PdfComparisonFiles();
        var input = revised ? files.NewPdf : files.OldPdf;
        var result = await CliProcess.Run("compare", input, input, "--out", files.Output, "--config", files.Config);
        Assert.Equal(0, result.Code); Assert.Empty(result.Error);
        Assert.StartsWith("相違なし: 2 ページ中 0 ページ、0 箇所", result.Output);
        var report = files.ReadReport();
        AssertInputs(report, input, input);
        Assert.Equal(new ReportSummary("same", 2, 0, 0, 0), report.Summary);
        Assert.Equal(new[] { 1, 2 }, report.Pages.Select(p => p.Page));
        Assert.All(report.Pages, AssertSamePage);
        Assert.Empty(report.Warnings);
        AssertOutputFiles(files.Output, []);
        AssertHtmlMatchesJson(files.Output, report, []);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutMatchingExclusionBothSecondPageChangesAreDetected(bool wrongPage)
    {
        using var files = new PdfComparisonFiles();
        var args = new List<string> { "compare", files.OldPdf, files.NewPdf, "--out", files.Output };
        if (wrongPage)
        {
            File.WriteAllText(files.Config, PdfComparisonFiles.ExclusionYaml(1), new UTF8Encoding(false));
            args.AddRange(["--config", files.Config]);
        }
        var result = await CliProcess.Run(args.ToArray());
        Assert.Equal(1, result.Code); Assert.Empty(result.Error);
        var report = files.ReadReport();
        Assert.Equal(2, report.Summary.Clusters); Assert.Equal(1, report.Summary.PagesDifferent);
        AssertSamePage(report.Pages[0]);
        var clusters = report.Pages[1].Clusters;
        Assert.Equal(2, clusters.Count);
        var visible = Assert.Single(clusters, c => c.BboxMm.Y > 30);
        AssertVisibleChange(visible);
        var excluded = Assert.Single(clusters, c => c.BboxMm.Y < 15);
        Assert.InRange(excluded.BboxMm.X, 50.2, 51.4);
        Assert.InRange(excluded.BboxMm.Y, 7.9, 9.1);
        Assert.True(excluded.Pixels > 1000);
        if (wrongPage) Assert.Equal(1, Assert.Single(report.Config.Exclude).Page);
        else Assert.Empty(report.Config.Exclude);
    }

    private static void AssertInputs(ReportDocument report, string a, string b)
    {
        Assert.Equal("pdf", report.Inputs.A.Type); Assert.Equal("pdf", report.Inputs.B.Type);
        Assert.Equal(2, report.Inputs.A.Pages); Assert.Equal(2, report.Inputs.B.Pages);
        Assert.Equal(a, report.Inputs.A.Path); Assert.Equal(b, report.Inputs.B.Path);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(a))), report.Inputs.A.Sha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(b))), report.Inputs.B.Sha256);
    }

    private static void AssertSamePage(ReportPage page)
    {
        Assert.Equal("same", page.Status); Assert.Empty(page.Clusters);
        Assert.Equal(0, page.RawPixels); Assert.False(page.SizeMismatch);
        AssertPageSize(page.SizePx);
        Assert.Equal(new PageImages(null, null, null), page.Images);
    }

    private static void AssertPageSize(PixelSize size)
    {
        // PDFtoImage の整数化は理論値から 1px ずれる場合がある（既存の PDF 入力テストと同じ許容）。
        Assert.InRange(size.W, 899, 901); Assert.InRange(size.H, 1199, 1201);
    }

    private static void AssertVisibleChange(ReportCluster cluster)
    {
        // PDF のラスタ境界と許容ずれの差を許しつつ、既知の変更位置・大きさを確認する。
        Assert.InRange(cluster.BboxMm.X, 12.1, 13.3);
        Assert.InRange(cluster.BboxMm.Y, 37.5, 38.7);
        Assert.InRange(cluster.BboxMm.W, 12.1, 13.3);
        Assert.InRange(cluster.BboxMm.H, 5.75, 6.95);
    }

    private static void AssertOutputFiles(string output, string[] images)
    {
        var expected = images.Concat(["result.json", "report.html"]).Order().ToArray();
        var actual = Directory.GetFiles(output, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(output, path).Replace(Path.DirectorySeparatorChar, '/')).Order().ToArray();
        Assert.Equal(expected, actual);
        foreach (var path in images)
        {
            Assert.False(Path.IsPathRooted(path)); Assert.DoesNotContain('\\', path);
            using var image = ReadImage(output, path);
            Assert.False(image.Empty()); Assert.Equal(MatType.CV_8UC3, image.Type());
        }
    }

    private static void AssertHtmlMatchesJson(string output, ReportDocument report, string[] images)
    {
        var html = File.ReadAllText(Path.Combine(output, "report.html"), new UTF8Encoding(false, true));
        Assert.DoesNotContain("http://", html); Assert.DoesNotContain("https://", html);
        var embedded = Regex.Match(html, "<script type=\"application/json\" id=\"result\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(embedded.Success);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(embedded.Groups[1].Value),
            JsonSerializer.SerializeToNode(report, ReportJson.Options)));
        foreach (var path in images) Assert.Contains($"\"{path}\"", html);
        Assert.Contains("出力日時", html);
    }

    private static void AssertImageContents(string output, ReportPage page, ReportCluster cluster, int dpi)
    {
        using var a = ReadImage(output, page.Images.A!); using var b = ReadImage(output, page.Images.B!);
        using var overlay = ReadImage(output, page.Images.Overlay!);
        Assert.Equal(new Size(page.SizePx.W, page.SizePx.H), a.Size());
        Assert.Equal(a.Size(), b.Size()); Assert.Equal(a.Size(), overlay.Size());
        Assert.True(Cv2.Norm(a, b, NormTypes.INF) > 0);
        var changed = overlay.At<Vec3b>(Units.RoundPixels(41.275, dpi), Units.RoundPixels(19.05, dpi));
        Assert.Equal(new Vec3b(0, 0, 255), changed);
        var excluded = overlay.At<Vec3b>(Units.RoundPixels(10.5, dpi), Units.RoundPixels(57, dpi));
        Assert.True(excluded.Item0 < excluded.Item1 && excluded.Item1 == excluded.Item2); // 黄色の除外表示。
        using var cropA = ReadImage(output, cluster.Crops.A); using var cropB = ReadImage(output, cluster.Crops.B);
        using var diff = ReadImage(output, cluster.Crops.Diff);
        Assert.Equal(cropA.Size(), cropB.Size()); Assert.Equal(cropA.Size(), diff.Size());
        Assert.True(cropA.Width > cluster.BboxPx.W && cropA.Height > cluster.BboxPx.H);
        Assert.True(Cv2.Norm(cropA, cropB, NormTypes.INF) > 0);
        Assert.Equal(new Vec3b(0, 0, 255), diff.At<Vec3b>(diff.Height / 2, diff.Width / 2));
    }

    private static Mat ReadImage(string output, string path) => Cv2.ImDecode(File.ReadAllBytes(Path.Combine(output, path)), ImreadModes.Color);

    private sealed class PdfComparisonFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "reportdiff-PDF 結合試験-" + Guid.NewGuid().ToString("N"));
        public string OldPdf => Path.Combine(Root, "帳票 旧.pdf");
        public string NewPdf => Path.Combine(Root, "帳票 新.pdf");
        public string Config => Path.Combine(Root, "除外 設定.yaml");
        public string Output => Path.Combine(Root, "比較 結果");
        public PdfComparisonFiles()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(OldPdf, PdfFixture.CreateComparisonReport(false));
            File.WriteAllBytes(NewPdf, PdfFixture.CreateComparisonReport(true));
            File.WriteAllText(Config, ExclusionYaml(2), new UTF8Encoding(false));
        }
        public static string ExclusionYaml(int page) => $"exclude:\n  - {{page: {page}, x: 49, y: 7, w: 16, h: 7, note: 出力日時}}\n";
        public ReportDocument ReadReport() => JsonSerializer.Deserialize<ReportDocument>(File.ReadAllBytes(Path.Combine(Output, "result.json")), ReportJson.Options)!;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
