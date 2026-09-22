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
        Assert.Equal($"処理中 1 / 2 ページ{Environment.NewLine}処理中 2 / 2 ページ{Environment.NewLine}レポート出力中{Environment.NewLine}相違あり: 2 ページ中 1 ページ、1 箇所 → {Path.Combine(files.Output, "report.html")}{Environment.NewLine}", result.Output);
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
        Assert.Contains("相違なし: 2 ページ中 0 ページ、0 箇所", result.Output);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClassificationFlowsFromPdfToJsonCropsAndHtml(bool reverse)
    {
        using var directory = new ReportTestDirectory();
        var a = Path.Combine(directory.Root, "分類 旧.pdf");
        var b = Path.Combine(directory.Root, "分類 新.pdf");
        File.WriteAllBytes(a, PdfFixture.CreateClassificationReport(reverse));
        File.WriteAllBytes(b, PdfFixture.CreateClassificationReport(!reverse));
        var result = await CliProcess.Run("compare", a, b, "--out", directory.Output);
        Assert.Equal(1, result.Code); Assert.Empty(result.Error);
        var report = JsonSerializer.Deserialize<ReportDocument>(File.ReadAllBytes(Path.Combine(directory.Output, "result.json")), ReportJson.Options)!;
        Assert.Equal(1, report.SchemaVersion);
        var page = Assert.Single(report.Pages);
        Assert.Equal(new[] { reverse ? "removed" : "added", reverse ? "added" : "removed", "color_changed", "changed" }, page.Clusters.Select(c => c.Kind));
        var html = File.ReadAllText(Path.Combine(directory.Output, "report.html"));
        Assert.Contains("追加（推定）", html); Assert.Contains("削除（推定）", html);
        Assert.Contains("色変更（推定）", html); Assert.Contains("緑は A だけのインク", html);
        Assert.DoesNotContain("https://", html); Assert.DoesNotContain("http://", html);
        using var overlay = directory.Read(page.Images.Overlay!);
        foreach (var cluster in page.Clusters)
        {
            Assert.Null(cluster.ShiftPx); Assert.Equal("", cluster.TextA); Assert.Equal("", cluster.TextB);
            using var diff = directory.Read(cluster.Crops.Diff);
            Assert.Equal(cluster.Kind == "removed" ? new Vec3b(0, 255, 0) : new Vec3b(0, 0, 255), diff.At<Vec3b>(diff.Height / 2, diff.Width / 2));
            var box = cluster.BboxPx;
            Assert.Equal(new Vec3b(0, 0, 255), overlay.At<Vec3b>(box.Y + box.H / 2, box.X + box.W / 2));
        }
        var embedded = Regex.Match(html, "<script type=\"application/json\" id=\"result\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(embedded.Groups[1].Value), JsonSerializer.SerializeToNode(report, ReportJson.Options)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MovementPdfProducesVectorsRelatedIdsAndStableDetection(bool reverse, bool disabled)
    {
        using var directory = new ReportTestDirectory();
        var a = Path.Combine(directory.Root, "移動 旧.pdf");
        var b = Path.Combine(directory.Root, "移動 新.pdf");
        var config = Path.Combine(directory.Root, "移動 設定.yaml");
        File.WriteAllBytes(a, PdfFixture.CreateMovementReport(reverse));
        File.WriteAllBytes(b, PdfFixture.CreateMovementReport(!reverse));
        File.WriteAllText(config, $"move: {{search_mm: {(disabled ? 0 : 5)}}}");
        var result = await CliProcess.Run("compare", a, b, "--out", directory.Output, "--config", config);
        Assert.Equal(1, result.Code); Assert.Empty(result.Error);
        var report = JsonSerializer.Deserialize<ReportDocument>(File.ReadAllBytes(Path.Combine(directory.Output, "result.json")), ReportJson.Options)!;
        var page = Assert.Single(report.Pages);
        Assert.Equal("different", report.Summary.Status);
        Assert.Equal(3, report.Summary.Clusters);
        Assert.Equal(disabled ? 0 : 5, report.Config.Move.SearchMm);
        var sign = reverse ? -1 : 1;
        for (var i = 0; i < page.Clusters.Count; i++)
        {
            var cluster = page.Clusters[i];
            Assert.Equal("", cluster.TextA); Assert.Equal("", cluster.TextB);
            if (disabled)
            {
                Assert.NotEqual("moved", cluster.Kind); Assert.Null(cluster.ShiftPx); Assert.Empty(cluster.RelatedClusterIds);
            }
            else
            {
                Assert.Equal("moved", cluster.Kind);
                Assert.Equal(new PixelShift((i < 2 ? 50 : 25) * sign, 0), cluster.ShiftPx);
                Assert.Equal(i < 2 ? new[] { i == 0 ? 2 : 1 } : [], cluster.RelatedClusterIds);
            }
        }
        var html = File.ReadAllText(Path.Combine(directory.Output, "report.html"));
        if (!disabled)
        {
            Assert.Contains($"{(reverse ? "左" : "右")} 50 px", html);
            Assert.Contains("href=\"#page-1-cluster-2\">2</a>", html);
            Assert.Contains("href=\"#page-1-cluster-1\">1</a>", html);
        }
        Assert.DoesNotContain("http://", html); Assert.DoesNotContain("https://", html);
        var embedded = Regex.Match(html, "<script type=\"application/json\" id=\"result\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(embedded.Groups[1].Value), JsonSerializer.SerializeToNode(report, ReportJson.Options)));
        // 色と画素数も実プロセスの出力まで通す。移動注釈で緑／赤の意味を変えない。
        var pairColors = new[] { new Vec3b(0, 255, 0), new Vec3b(0, 0, 255) };
        if (reverse) Array.Reverse(pairColors);
        for (var i = 0; i < 2; i++)
        {
            using var image = directory.Read(page.Clusters[i].Crops.Diff);
            using var selected = new Mat();
            var color = pairColors[i];
            Cv2.InRange(image, new Scalar(color.Item0, color.Item1, color.Item2), new Scalar(color.Item0, color.Item1, color.Item2), selected);
            Assert.True(Cv2.CountNonZero(selected) > 10);
        }
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
