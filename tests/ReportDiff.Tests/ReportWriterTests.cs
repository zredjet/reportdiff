using System.Text;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

public sealed class ReportWriterTests
{
    [Fact]
    public void JsonMatchesSchemaAndEffectiveSettingsAndCropsMatchPixels()
    {
        using var directory = new ReportTestDirectory();
        var settings = ConfigurationLoader.Load("""
            dpi: 144
            image_dpi: 200
            diff: { max_shift_mm: 0, color_threshold: 2, edge_tolerance: 0 }
            cluster: { merge_x_mm: 0, merge_y_mm: 0, min_pixels: 4, max_clusters_per_page: 2, max_diff_ratio: 1 }
            exclude:
              - { page: all, x: 2, y: 2, w: 0.5, h: 0.5, note: 出力日時 }
              - { page: 2, x: 1, y: 1, w: 1, h: 1, note: ページ限定 }
            report: { crop_margin_mm: 0.5 }
            """);
        using var a = White(80, 60);
        using var b = White(80, 60);
        Cv2.Rectangle(b, new Rect(20, 20, 10, 8), Scalar.All(0), -1);
        b.Set(50, 70, new Vec3b(0, 0, 0));
        using var pair = PageNormalizer.Normalize(a, b);
        using var comparison = PageComparer.Compare(pair.A, pair.B, settings.ForPage(1, 144));
        var timestamp = new DateTimeOffset(2026, 9, 21, 12, 34, 56, TimeSpan.FromHours(9));
        var writer = new ReportWriter(directory.Output, Inputs(), settings.ToReportConfiguration(), generatedAt: timestamp);
        writer.AddComparedPage(1, pair, comparison, 144);
        var result = writer.Complete();
        var json = File.ReadAllText(Path.Combine(directory.Output, "result.json"), Encoding.UTF8);
        var loaded = JsonSerializer.Deserialize<ReportDocument>(json, ReportJson.Options)!;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Keys(root, "schema_version", "tool", "generated_at", "inputs", "config", "summary", "warnings", "pages");
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(new ReportTool("reportdiff", "0.1.1"), loaded.Tool);
        Assert.Equal(timestamp, loaded.GeneratedAt);
        Assert.Equal(result.Inputs, loaded.Inputs);
        Keys(root.GetProperty("config"), "dpi", "image_dpi", "diff", "ink", "cluster", "move", "align", "text", "exclude", "report");
        Keys(root.GetProperty("config").GetProperty("align"), "enabled", "max_shift_mm", "min_score", "min_score_gap", "min_improvement", "coarse_max_side_samples", "refine_radius_samples", "min_support_cells", "min_support_rows", "min_support_columns", "min_ink_area_mm2");
        var clusterConfig = root.GetProperty("config").GetProperty("cluster");
        Keys(clusterConfig, "merge_x_mm", "merge_y_mm", "min_pixels", "max_clusters_per_page", "max_diff_ratio", "reading_band_mm");
        Assert.Equal(settings.Diff, loaded.Config.Diff);
        Assert.Equal(settings.Cluster, loaded.Config.Cluster);
        Assert.Equal(144, loaded.Config.Dpi); Assert.Equal(200, loaded.Config.ImageDpi);
        Assert.Equal(0.5, loaded.Config.Report.CropMarginMm);
        Assert.Null(loaded.Config.Exclude[0].Page);
        Assert.Equal("all", root.GetProperty("config").GetProperty("exclude")[0].GetProperty("page").GetString());
        Assert.Equal("出力日時", loaded.Config.Exclude[0].Note);
        Assert.Equal(2, loaded.Config.Exclude[1].Page);
        Assert.Equal(new ReportSummary("different", 1, 1, 1, 0), loaded.Summary);
        var page = Assert.Single(loaded.Pages);
        Keys(root.GetProperty("pages")[0], "page", "status", "size_px", "size_mismatch", "raw_pixels", "noise_dropped", "absorbed_groups", "max_shift_px", "global_shift_px", "alignment", "images", "clusters");
        Keys(root.GetProperty("pages")[0].GetProperty("alignment"), "status", "reason", "estimated_shift_px", "score_before", "score_after", "score_gap", "coarse_score_gap", "support_cells");
        Keys(root.GetProperty("pages")[0].GetProperty("images"), "a", "b", "overlay", "b_original");
        Assert.Null(page.GlobalShiftPx); Assert.Null(page.Images.BOriginal);
        Assert.Equal(AlignmentResult.Disabled, page.Alignment); Assert.Equal(settings.Align, loaded.Config.Align);
        Assert.Equal(new PixelSize(80, 60), page.SizePx);
        Assert.Equal(81, page.RawPixels); Assert.Equal(1, page.NoiseDropped);
        var cluster = Assert.Single(page.Clusters);
        var clusterJson = root.GetProperty("pages")[0].GetProperty("clusters")[0];
        Keys(clusterJson, "id", "bbox_px", "bbox_mm", "pixels", "fill_ratio", "kind", "shift_px", "text_a", "text_b", "crops", "related_cluster_ids");
        Assert.Equal(new PixelBox(20, 20, 10, 8), cluster.BboxPx);
        Assert.Equal(3.5277777777777777, cluster.BboxMm.X, 10);
        Assert.Equal(1.7638888888888888, cluster.BboxMm.W, 10);
        Assert.Equal(80, cluster.Pixels); Assert.Equal(1, cluster.FillRatio);
        Assert.Equal("added", clusterJson.GetProperty("kind").GetString());
        Assert.Empty(cluster.RelatedClusterIds);
        Assert.Equal(settings.Move, loaded.Config.Move);
        foreach (var key in new[] { "shift_px", "text_a", "text_b" }) Assert.Equal(JsonValueKind.Null, clusterJson.GetProperty(key).ValueKind);
        Assert.Equal("pages/p001_overlay.png", page.Images.Overlay);
        Assert.Equal("crops/p001_c001_diff.png", cluster.Crops.Diff);
        foreach (var path in new[] { page.Images.A!, page.Images.B!, page.Images.Overlay!, cluster.Crops.A, cluster.Crops.B, cluster.Crops.Diff })
        {
            Assert.DoesNotContain('\\', path); Assert.False(Path.IsPathRooted(path));
            Assert.True(File.Exists(Path.Combine(directory.Output, path)));
        }
        using var savedA = directory.Read(page.Images.A!);
        using var savedB = directory.Read(page.Images.B!);
        Assert.Equal(0, Cv2.Norm(a, savedA, NormTypes.INF)); Assert.Equal(0, Cv2.Norm(b, savedB, NormTypes.INF));
        using var cropA = directory.Read(cluster.Crops.A);
        using var cropB = directory.Read(cluster.Crops.B);
        using var diff = directory.Read(cluster.Crops.Diff);
        Assert.Equal(new Size(16, 14), cropA.Size()); Assert.Equal(cropA.Size(), cropB.Size());
        Assert.Equal(new Vec3b(255, 255, 255), cropA.At<Vec3b>(4, 4));
        Assert.Equal(new Vec3b(0, 0, 0), cropB.At<Vec3b>(4, 4));
        Assert.Equal(new Vec3b(0, 0, 255), diff.At<Vec3b>(4, 4));
        Assert.Equal(new Vec3b(255, 255, 255), diff.At<Vec3b>(0, 0));
        Assert.Equal(0, Cv2.Norm(b, pair.B, NormTypes.INF));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SamePageImagesAreOptionalButMetricsAreAlwaysWritten(bool saveAll)
    {
        using var directory = new ReportTestDirectory();
        using var a = White(40, 30); using var b = White(50, 40);
        using var pair = PageNormalizer.Normalize(a, b);
        using var raw = new Mat(pair.A.Size(), MatType.CV_8UC1, Scalar.All(0));
        using var comparison = new PageComparison("same", [], 0, 3, 7, 2, raw.Clone(), raw.Clone(), []);
        var writer = new ReportWriter(directory.Output, Inputs(), Settings().ToReportConfiguration(), saveAll);
        writer.AddComparedPage(1, pair, comparison, 300);
        var result = directory.CompleteAndRead(writer);
        var page = Assert.Single(result.Pages);
        Assert.Equal("same", result.Summary.Status);
        Assert.Equal(7, result.Summary.AbsorbedGroups);
        Assert.Equal(3, page.NoiseDropped); Assert.Equal(2, page.MaxShiftPx);
        Assert.True(page.SizeMismatch);
        Assert.Contains(result.Warnings, w => w.Code == "SIZE_MISMATCH" && w.Message.Contains("白"));
        Assert.Equal(saveAll, page.Images.A is not null);
        Assert.Equal(saveAll, Directory.Exists(Path.Combine(directory.Output, "pages")));
        Assert.Empty(page.Clusters);
        Assert.False(Directory.Exists(Path.Combine(directory.Output, "crops")));
        if (saveAll) Assert.Equal(3, Directory.GetFiles(Path.Combine(directory.Output, "pages")).Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnpairedPagesHaveOnlyExistingImagesAndDoNotCountAsCompared(bool onlyA)
    {
        using var directory = new ReportTestDirectory();
        using var image = White(40, 30);
        var inputs = Inputs(onlyA ? 3 : 1, onlyA ? 1 : 3);
        inputs = inputs with { B = inputs.B with { Type = "pdf" } };
        var writer = new ReportWriter(directory.Output, inputs, Settings().ToReportConfiguration());
        writer.AddUnpairedPage(3, image);
        writer.AddUnpairedPage(2, image);
        var result = directory.CompleteAndRead(writer);
        Assert.Equal(new[] { 2, 3 }, result.Pages.Select(p => p.Page));
        Assert.Equal(new ReportSummary("different", 0, 2, 0, 0), result.Summary);
        Assert.Equal(new[] { "PAGE_COUNT_MISMATCH", "MIXED_INPUT_TYPES" }, result.Warnings.Select(w => w.Code));
        foreach (var page in result.Pages)
        {
            Assert.Equal(onlyA ? "only_in_a" : "only_in_b", page.Status);
            Assert.Equal(onlyA, page.Images.A is not null); Assert.Equal(!onlyA, page.Images.B is not null);
            Assert.Null(page.Images.Overlay); Assert.Empty(page.Clusters); Assert.False(page.SizeMismatch);
        }
        Assert.Equal(2, Directory.GetFiles(Path.Combine(directory.Output, "pages")).Length);
    }

    [Fact]
    public void PageCountMismatchRemainsDifferentWhenOnlyCommonPageIsSelected()
    {
        using var directory = new ReportTestDirectory();
        using var a = White(40, 30);
        using var pair = PageNormalizer.Normalize(a, a);
        using var comparison = PageComparer.Compare(pair.A, pair.B, new());
        var writer = new ReportWriter(directory.Output, Inputs(1, 2), Settings().ToReportConfiguration());
        writer.AddComparedPage(1, pair, comparison, 300);
        var result = directory.CompleteAndRead(writer);
        Assert.Equal(new ReportSummary("different", 1, 0, 0, 0), result.Summary);
        Assert.Equal("PAGE_COUNT_MISMATCH", Assert.Single(result.Warnings).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TooDifferentAndClusterLimitAreRecorded(bool limit)
    {
        using var directory = new ReportTestDirectory();
        using var a = White(80, 60); using var b = White(80, 60);
        if (limit)
        {
            Cv2.Rectangle(b, new Rect(5, 20, 5, 5), Scalar.All(0), -1);
            Cv2.Rectangle(b, new Rect(50, 20, 10, 10), Scalar.All(0), -1);
        }
        else b.SetTo(Scalar.All(0));
        var settings = Settings() with { Cluster = new() { MergeXMm = 0, MergeYMm = 0, MaxClustersPerPage = 1 } };
        using var pair = PageNormalizer.Normalize(a, b);
        using var comparison = PageComparer.Compare(pair.A, pair.B, settings.ForPage(1, 300));
        var writer = new ReportWriter(directory.Output, Inputs(), settings.ToReportConfiguration());
        writer.AddComparedPage(1, pair, comparison, 300);
        var result = directory.CompleteAndRead(writer);
        Assert.Equal(limit ? "CLUSTER_LIMIT" : "TOO_DIFFERENT", Assert.Single(result.Warnings).Code);
        Assert.Equal(limit ? "different" : "too_different", result.Pages[0].Status);
        Assert.Equal(limit ? 1 : 0, result.Summary.Clusters);
        Assert.NotNull(result.Pages[0].Images.Overlay);
        if (limit) Assert.Equal(3, Directory.GetFiles(Path.Combine(directory.Output, "crops")).Length);
    }

    [Fact]
    public void OverlayShowsNestedContoursExclusionsAndClusterNumbers()
    {
        using var directory = new ReportTestDirectory();
        using var b = White(200, 160);
        using var pair = PageNormalizer.Normalize(b, b);
        using var raw = new Mat(b.Size(), MatType.CV_8UC1, Scalar.All(0));
        using var label = raw.Clone();
        Cv2.Rectangle(label, new Rect(20, 40, 160, 100), Scalar.All(255), 1);
        Cv2.Rectangle(label, new Rect(80, 80, 30, 20), Scalar.All(255), -1);
        using var comparison = new PageComparison("different", [new(1, new(20, 40, 160, 100), 516), new(2, new(80, 80, 30, 20), 600)],
            0, 0, 0, 0, raw.Clone(), label.Clone(), []);
        var settings = Settings() with { Exclude = [new(null, 0, 0, 10, 10, "出力日時"), new(2, 30, 0, 10, 10)] };
        var writer = new ReportWriter(directory.Output, Inputs(), settings.ToReportConfiguration());
        var page = writer.AddComparedPage(1, pair, comparison, 72);
        writer.Complete();
        using var overlay = directory.Read(page.Images.Overlay!);
        Assert.Equal(new Vec3b(0, 0, 255), overlay.At<Vec3b>(80, 95)); // 外枠に囲まれた内側のクラスタの上辺。
        Assert.Equal(new Vec3b(0, 0, 255), overlay.At<Vec3b>(100, 20));
        Assert.Equal(new Vec3b(128, 255, 255), overlay.At<Vec3b>(5, 5));
        Assert.Equal(new Vec3b(255, 255, 255), overlay.At<Vec3b>(5, 95)); // 2 ページ限定の除外は描かない。
        // 輪郭も raw もない、クラスタ上方の領域に番号の赤い画素がある。
        Assert.True(CountRed(overlay, new Rect(20, 18, 20, 20)) > 3);
        Assert.True(CountRed(overlay, new Rect(80, 58, 20, 20)) > 3);
        Assert.Equal(0, Cv2.CountNonZero(raw));
        Assert.Equal(0, Cv2.Norm(b, pair.B, NormTypes.INF));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(28, 18)]
    public void CropMarginIsClippedAtPageEdges(int x, int y)
    {
        using var directory = new ReportTestDirectory();
        using var a = White(30, 20); using var b = White(30, 20);
        Cv2.Rectangle(b, new Rect(x, y, 2, 2), Scalar.All(0), -1);
        var settings = Settings() with { Report = new() { CropMarginMm = 1 } };
        using var pair = PageNormalizer.Normalize(a, b);
        using var comparison = PageComparer.Compare(pair.A, pair.B, settings.ForPage(1, 72));
        var writer = new ReportWriter(directory.Output, Inputs(), settings.ToReportConfiguration());
        var page = writer.AddComparedPage(1, pair, comparison, 72);
        writer.Complete();
        using var crop = directory.Read(Assert.Single(page.Clusters).Crops.B);
        Assert.Equal(new Size(5, 5), crop.Size());
        Assert.Equal(new Vec3b(0, 0, 0), crop.At<Vec3b>(y == 0 ? 0 : 4, x == 0 ? 0 : 4));
    }

    [Fact]
    public void GoldenFrameAndInnerNumberChangeProduceTwoSeparateCrops()
    {
        using var directory = new ReportTestDirectory();
        using var a = GoldenData.Image("D11", "a"); using var b = GoldenData.Image("D11", "b");
        using var pair = PageNormalizer.Normalize(a, b);
        var settings = new AppSettings();
        using var comparison = PageComparer.Compare(pair.A, pair.B, settings.ForPage(1, 300));
        var writer = new ReportWriter(directory.Output, Inputs(), settings.ToReportConfiguration());
        writer.AddComparedPage(1, pair, comparison, 300);
        var result = directory.CompleteAndRead(writer);
        Assert.Equal(2, result.Summary.Clusters);
        var clusters = result.Pages[0].Clusters;
        Assert.Equal(new PixelBox(568, 127, 28, 38), clusters[1].BboxPx);
        Assert.Equal(6, Directory.GetFiles(Path.Combine(directory.Output, "crops")).Length);
        using var overlay = directory.Read(result.Pages[0].Images.Overlay!);
        // 内側クラスタの輪郭は、raw の赤塗りより外にあるため別途確認できる。
        var contourPixels = 0;
        for (var y = 130; y < 170; y++)
        for (var x = 548; x < 620; x++)
            if (comparison.RawMask.At<byte>(y, x) == 0 && comparison.LabelMask.At<byte>(y, x) != 0
                && overlay.At<Vec3b>(y, x) == new Vec3b(0, 0, 255)) contourPixels++;
        Assert.True(contourPixels > 10, "枠内の数値の周囲に、raw の塗りとは別の赤い輪郭が必要です。");
    }

    [Fact]
    public void InputMetadataHashesBytesThroughDotNet()
    {
        using var directory = new ReportTestDirectory();
        var path = Path.Combine(directory.Root, "入力 帳票.pdf");
        File.WriteAllBytes(path, "abc"u8.ToArray());
        var input = ReportInput.FromFile(path, InputFormat.Pdf, 3);
        Assert.Equal(path, input.Path); Assert.Equal("pdf", input.Type); Assert.Equal(3, input.Pages);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", input.Sha256);
    }

    [Fact]
    public void ExistingOutputAndDuplicatePagesAreNotOverwritten()
    {
        using var directory = new ReportTestDirectory();
        Directory.CreateDirectory(directory.Output);
        var sentinel = Path.Combine(directory.Output, "既存.txt");
        File.WriteAllText(sentinel, "そのまま");
        Assert.Contains("空", Assert.Throws<ReportWriteException>(() => new ReportWriter(directory.Output, Inputs(), Settings().ToReportConfiguration())).Message);
        Assert.Equal("そのまま", File.ReadAllText(sentinel));
        File.Delete(sentinel);
        using var a = White(40, 30); using var pair = PageNormalizer.Normalize(a, a);
        using var comparison = PageComparer.Compare(a, a, new());
        var writer = new ReportWriter(directory.Output, Inputs(), Settings().ToReportConfiguration(), true);
        writer.AddComparedPage(1, pair, comparison, 300);
        Assert.Throws<ArgumentException>(() => writer.AddComparedPage(1, pair, comparison, 300));
        writer.Complete();
        Assert.Throws<ReportWriteException>(() => writer.Complete());
        Assert.Throws<ReportWriteException>(() => writer.AddComparedPage(1, pair, comparison, 300));
    }

    [Fact]
    public void ImageWriteFailurePreventsSuccessfulCompletion()
    {
        using var directory = new ReportTestDirectory();
        var writer = new ReportWriter(directory.Output, Inputs(), Settings().ToReportConfiguration(), true);
        File.WriteAllText(Path.Combine(directory.Output, "pages"), "ディレクトリを作れない状態");
        using var a = White(40, 30); using var pair = PageNormalizer.Normalize(a, a);
        using var comparison = PageComparer.Compare(a, a, new());
        Assert.Contains("書き込めません", Assert.Throws<ReportWriteException>(() => writer.AddComparedPage(1, pair, comparison, 300)).Message);
        Assert.Throws<ReportWriteException>(() => writer.Complete());
        Assert.False(File.Exists(Path.Combine(directory.Output, "result.json")));
    }

    [Fact]
    public void MixedChangeCropHasGreenRemovalAndRedAdditionWhileOverlayStaysRed()
    {
        using var directory = new ReportTestDirectory();
        using var a = White(100, 80); using var b = White(100, 80);
        Cv2.Rectangle(a, new Rect(20, 20, 5, 8), Scalar.All(0), -1);
        Cv2.Rectangle(b, new Rect(30, 20, 5, 8), Scalar.All(0), -1);
        var settings = Settings() with { Move = new() { SearchMm = 0 }, Report = new() { CropMarginMm = 0 } };
        using var pair = PageNormalizer.Normalize(a, b);
        using var comparison = PageComparer.Compare(pair.A, pair.B, settings.ForPage(1, 300));
        var writer = new ReportWriter(directory.Output, Inputs(), settings.ToReportConfiguration());
        writer.AddComparedPage(1, pair, comparison, 300);
        var result = directory.CompleteAndRead(writer);
        var page = Assert.Single(result.Pages);
        var cluster = Assert.Single(page.Clusters);
        Assert.Equal("changed", cluster.Kind);
        using var diff = directory.Read(cluster.Crops.Diff);
        using var overlay = directory.Read(page.Images.Overlay!);
        Assert.Equal(new Vec3b(0, 255, 0), diff.At<Vec3b>(1, 1));
        Assert.Equal(new Vec3b(0, 0, 255), diff.At<Vec3b>(1, 11));
        Assert.Equal(new Vec3b(255, 255, 255), diff.At<Vec3b>(1, 7));
        Assert.Equal(new Vec3b(0, 0, 255), overlay.At<Vec3b>(21, 21));
        Assert.Equal(0, Cv2.Norm(a, pair.A, NormTypes.INF));
        Assert.Equal(0, Cv2.Norm(b, pair.B, NormTypes.INF));
    }

    private static AppSettings Settings() => ConfigurationLoader.Load(profile: "strict");
    private static ReportInputs Inputs(int countA = 1, int countB = 1) => new(new("旧.png", "png", countA, new string('a', 64)), new("新.png", "png", countB, new string('b', 64)));
    private static Mat White(int width, int height) => new(height, width, MatType.CV_8UC3, Scalar.All(255));
    private static void Keys(JsonElement value, params string[] expected) =>
        Assert.Equal(expected.Order(), value.EnumerateObject().Select(p => p.Name).Order());
    private static int CountRed(Mat image, Rect bounds)
    {
        var count = 0;
        for (var y = bounds.Y; y < bounds.Bottom; y++)
        for (var x = bounds.X; x < bounds.Right; x++)
        {
            var pixel = image.At<Vec3b>(y, x);
            if (pixel.Item2 > pixel.Item0 + 20 && pixel.Item2 > pixel.Item1 + 20) count++;
        }
        return count;
    }
}

internal sealed class ReportTestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"reportdiff-結果 試験-{Guid.NewGuid():N}");
    public string Output => Path.Combine(Root, "比較 結果");
    public ReportTestDirectory() => Directory.CreateDirectory(Root);
    public Mat Read(string path) => Cv2.ImDecode(File.ReadAllBytes(Path.Combine(Output, path)), ImreadModes.Color);
    public ReportDocument CompleteAndRead(ReportWriter writer)
    {
        writer.Complete();
        return JsonSerializer.Deserialize<ReportDocument>(File.ReadAllBytes(Path.Combine(Output, "result.json")), ReportJson.Options)!;
    }
    public void Dispose() => Directory.Delete(Root, true);
}
