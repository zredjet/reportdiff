using System.Globalization;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;

namespace ReportDiff.Report;

/// <summary>画像をページごとに保存し、Complete で result.json を確定する。入力 Mat は所有しない。</summary>
public sealed class ReportWriter
{
    private readonly string outputDirectory;
    private readonly ReportInputs inputs;
    private readonly ReportConfiguration config;
    private readonly bool saveAllPages;
    private readonly DateTimeOffset generatedAt;
    private readonly List<ReportPage> pages = [];
    private readonly List<ReportWarning> warnings = [];
    private bool completed;
    private bool faulted;

    public ReportWriter(string outputDirectory, ReportInputs inputs, ReportConfiguration config,
        bool saveAllPages = false, DateTimeOffset? generatedAt = null)
    {
        if (inputs.A.Pages < 1 || inputs.B.Pages < 1) throw new ArgumentException("入力のページ数は 1 以上にしてください。");
        if (!double.IsFinite(config.Report.CropMarginMm) || config.Report.CropMarginMm < 0)
            throw new ArgumentException("切り出し余白は 0 以上の有限の mm 値にしてください。");
        this.outputDirectory = Path.GetFullPath(outputDirectory);
        this.inputs = inputs;
        this.config = config with { Exclude = Array.AsReadOnly(config.Exclude.ToArray()) };
        this.saveAllPages = saveAllPages;
        this.generatedAt = generatedAt ?? DateTimeOffset.Now;
        ExecuteWrite(() =>
        {
            if (Directory.Exists(this.outputDirectory) && Directory.EnumerateFileSystemEntries(this.outputDirectory).Any())
                throw new ReportWriteException("出力先は空のディレクトリを指定してください。");
            Directory.CreateDirectory(this.outputDirectory);
        });
        if (inputs.A.Pages != inputs.B.Pages)
            warnings.Add(new("PAGE_COUNT_MISMATCH", $"ページ数が異なります（A: {inputs.A.Pages}、B: {inputs.B.Pages}）。片側だけのページも相違として扱います。"));
        if (inputs.A.Type != inputs.B.Type)
            warnings.Add(new("MIXED_INPUT_TYPES", $"入力形式が異なります（A: {inputs.A.Type}、B: {inputs.B.Type}）。"));
    }

    public ReportPage AddComparedPage(int page, NormalizedPagePair images, PageComparison comparison, int dpi)
    {
        CheckPage(page);
        if (page > Math.Min(inputs.A.Pages, inputs.B.Pages)) throw new ArgumentException("両方の入力にあるページを指定してください。");
        if (dpi is < 72 or > 1200) throw new ArgumentException("DPI は 72〜1200 にしてください。");
        ValidateImage(images.A); ValidateImage(images.B);
        var size = images.A.Size();
        if (images.B.Size() != size || comparison.RawMask.Size() != size || comparison.LabelMask.Size() != size
            || comparison.RawMask.Type() != MatType.CV_8UC1 || comparison.LabelMask.Type() != MatType.CV_8UC1)
            throw new ArgumentException("画像と差分マスクのサイズ・画素形式が一致していません。");
        if (comparison.Status is not ("same" or "different" or "too_different")) throw new ArgumentException("比較結果の状態が不正です。");
        if (comparison.Clusters.Select(c => c.Id).Distinct().Count() != comparison.Clusters.Count
            || comparison.Clusters.Any(c => c.Id < 1 || c.Bounds.X < 0 || c.Bounds.Y < 0 || c.Bounds.Width < 1 || c.Bounds.Height < 1
                || (long)c.Bounds.X + c.Bounds.Width > size.Width || (long)c.Bounds.Y + c.Bounds.Height > size.Height))
            throw new ArgumentException("クラスタの番号・矩形が不正です。");

        PageImages paths = new(null, null, null);
        var clusters = new List<ReportCluster>();
        ExecuteWrite(() =>
        {
            if (comparison.Status != "same" || saveAllPages)
            {
                var prefix = "pages/" + PageStem(page);
                paths = new(prefix + "_a.png", prefix + "_b.png", prefix + "_overlay.png");
                WritePng(paths.A!, images.A); WritePng(paths.B!, images.B);
                using var overlay = ReportImages.Overlay(images.B, comparison, config.Exclude.Where(e => e.Page is null || e.Page == page), dpi);
                WritePng(paths.Overlay!, overlay);
            }
            foreach (var cluster in comparison.Clusters)
            {
                var bounds = cluster.Bounds;
                var crop = ReportImages.CropBounds(bounds, size, config.Report.CropMarginMm, dpi);
                var prefix = "crops/" + PageStem(page) + "_c" + cluster.Id.ToString("D3", CultureInfo.InvariantCulture);
                var crops = new ClusterCrops(prefix + "_a.png", prefix + "_b.png", prefix + "_diff.png");
                using var a = new Mat(images.A, crop);
                using var b = new Mat(images.B, crop);
                using var diff = ReportImages.DifferenceCrop(images.B, comparison.RawMask, crop);
                WritePng(crops.A, a); WritePng(crops.B, b); WritePng(crops.Diff, diff);
                clusters.Add(new(cluster.Id, new(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                    new(Units.PixelsToMm(bounds.X, dpi), Units.PixelsToMm(bounds.Y, dpi),
                        Units.PixelsToMm(bounds.Width, dpi), Units.PixelsToMm(bounds.Height, dpi)),
                    cluster.Pixels, cluster.FillRatio, null, null, null, null, crops));
            }
        });
        var result = new ReportPage(page, comparison.Status, new(size.Width, size.Height), images.SizeMismatch,
            comparison.RawPixels, comparison.NoiseDropped, comparison.AbsorbedGroups, comparison.MaxShiftPx,
            paths, Array.AsReadOnly(clusters.ToArray()));
        pages.Add(result);
        if (images.SizeMismatch)
            warnings.Add(new("SIZE_MISMATCH", $"{page} ページ: サイズが異なります（A: {images.OriginalSizeA.Width}×{images.OriginalSizeA.Height}px、B: {images.OriginalSizeB.Width}×{images.OriginalSizeB.Height}px）。右と下を白で埋めました。"));
        foreach (var code in comparison.Warnings)
        {
            var message = code switch
            {
                "TOO_DIFFERENT" => "差分の割合が上限を超えたため、クラスタ化を省略しました。",
                "CLUSTER_LIMIT" => "クラスタ数が上限を超えたため、画素数の多いクラスタだけを残しました。",
                _ => $"比較処理の警告: {code}"
            };
            warnings.Add(new(code, $"{page} ページ: {message}"));
        }
        return result;
    }

    public ReportPage AddUnpairedPage(int page, Mat image)
    {
        CheckPage(page);
        ValidateImage(image);
        if (page <= Math.Min(inputs.A.Pages, inputs.B.Pages)) throw new ArgumentException("片方だけにあるページを指定してください。");
        var onlyA = page <= inputs.A.Pages;
        var path = "pages/" + PageStem(page) + (onlyA ? "_a.png" : "_b.png");
        ExecuteWrite(() => WritePng(path, image));
        var result = new ReportPage(page, onlyA ? "only_in_a" : "only_in_b", new(image.Width, image.Height),
            false, 0, 0, 0, 0, new(onlyA ? path : null, onlyA ? null : path, null), []);
        pages.Add(result);
        return result;
    }

    public ReportDocument Complete()
    {
        CheckWritable();
        if (pages.Count == 0) throw new ReportWriteException("結果に出力するページがありません。");
        var sorted = pages.OrderBy(p => p.Page).ToArray();
        var different = sorted.Count(p => p.Status != "same");
        var summary = new ReportSummary(different > 0 || inputs.A.Pages != inputs.B.Pages ? "different" : "same",
            sorted.Count(p => p.Status is not ("only_in_a" or "only_in_b")), different,
            sorted.Sum(p => p.Clusters.Count), sorted.Sum(p => p.AbsorbedGroups));
        var result = new ReportDocument(1, ReportTool.Current, generatedAt, inputs, config, summary,
            Array.AsReadOnly(warnings.ToArray()), Array.AsReadOnly(sorted));
        ExecuteWrite(() =>
        {
            using var stream = new FileStream(Path.Combine(outputDirectory, "result.json"), FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(stream, result, ReportJson.Options);
        });
        completed = true;
        return result;
    }

    private void WritePng(string relativePath, Mat image)
    {
        if (!Cv2.ImEncode(".png", image, out var bytes)) throw new ReportWriteException("PNG 画像を作成できません。");
        var path = Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.Write(bytes);
    }

    private static string PageStem(int page) => "p" + page.ToString("D3", CultureInfo.InvariantCulture);
    private void CheckPage(int page)
    {
        CheckWritable();
        if (page < 1 || page > Math.Max(inputs.A.Pages, inputs.B.Pages)) throw new ArgumentException("出力するページ番号が範囲外です。");
        if (pages.Any(p => p.Page == page)) throw new ArgumentException("同じページは 2 回出力できません。");
    }
    private void CheckWritable()
    {
        if (completed || faulted) throw new ReportWriteException("この出力処理は完了または失敗しているため、続行できません。");
    }
    private static void ValidateImage(Mat image)
    {
        if (image.IsDisposed || image.Empty() || image.Type() != MatType.CV_8UC3)
            throw new ArgumentException("出力には空でない BGR 8bit 画像を指定してください。");
    }
    private void ExecuteWrite(Action write)
    {
        try { write(); }
        catch (Exception ex)
        {
            faulted = true;
            if (ex is IOException or UnauthorizedAccessException or OpenCVException or JsonException)
                throw new ReportWriteException("結果ファイルを書き込めません。出力先と空き容量を確認してください。", ex);
            throw;
        }
    }
}

public sealed class ReportWriteException(string message, Exception? inner = null) : Exception(message, inner);
