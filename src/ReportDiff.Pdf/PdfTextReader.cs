using OpenCvSharp;
using ReportDiff.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Tokens;

namespace ReportDiff.Pdf;

public sealed record TextAnnotationWarning(string Code, string Message);
public sealed record PageTextAnnotations(IReadOnlyDictionary<int, string> TextByCluster,
    IReadOnlyList<TextAnnotationWarning> Warnings);

/// <summary>必要なページのテキストだけを抽出する。描画用とは独立したストリームを所有する。</summary>
public sealed class PdfTextReader(string path) : IDisposable
{
    public const int MaximumLetters = 100_000;
    public const int MaximumWords = 20_000;
    private static readonly NearestNeighbourWordExtractor WordExtractor = new(new() { MaxDegreeOfParallelism = 1 });
    private FileStream? stream;
    private PdfDocument? document;
    private bool openFailed;
    private bool disposed;

    public PageTextAnnotations Annotate(int pageNumber, Size originalSize, int dpi,
        IReadOnlyList<DifferenceCluster> clusters, IReadOnlyList<RectMm> exclusions)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (clusters.Count == 0) return new(new Dictionary<int, string>(), []);
        if (openFailed) return Failed("PDF テキスト層を開けませんでした。");
        if (document is null)
        {
            try
            {
                stream = File.OpenRead(path);
                document = PdfDocument.Open(stream);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                stream?.Dispose(); stream = null; openFailed = true;
                return Failed("PDF テキスト層を開けませんでした。");
            }
        }
        try
        {
            var page = document.GetPage(pageNumber);
            if (page.Rotation.Value != 0) return Skipped($"ページが {page.Rotation.Value} 度回転しているため注釈を省略しました。");
            if (page.Dictionary.TryGet(NameToken.UserUnit, out var unit)
                && (unit is not NumericToken number || number.Double != 1))
                return Skipped("標準と異なる UserUnit のため注釈を省略しました。");
            if (!double.IsFinite(page.Width) || !double.IsFinite(page.Height) || page.Width <= 0 || page.Height <= 0
                || originalSize.Width <= 0 || originalSize.Height <= 0
                || Math.Abs(Units.PointsToPixels(page.Width, dpi) - originalSize.Width) > 1
                || Math.Abs(Units.PointsToPixels(page.Height, dpi) - originalSize.Height) > 1)
                return Skipped("テキスト座標と描画サイズの対応を確認できないため注釈を省略しました。");
            if (page.Letters.Count > MaximumLetters) return Skipped($"文字要素が {MaximumLetters} 件を超えたため注釈を省略しました。");
            var words = page.GetWords(WordExtractor).Where(w => !string.IsNullOrWhiteSpace(w.Text)).Take(MaximumWords + 1).ToArray();
            if (words.Length > MaximumWords) return Skipped($"単語が {MaximumWords} 件を超えたため注釈を省略しました。");
            // PdfPig 0.1.16 は MediaBox と交差した CropBox の原点を補正済み。
            // 実際の描画寸法への比率で端数を合わせ、右・下の白埋めには拡大しない。
            var sx = originalSize.Width / page.Width; var sy = originalSize.Height / page.Height;
            var mapped = new List<TextWord>();
            foreach (var word in words)
            {
                var box = word.BoundingBox;
                var points = new[] { box.BottomLeft, box.BottomRight, box.TopLeft, box.TopRight };
                var left = points.Min(p => p.X) * sx; var right = points.Max(p => p.X) * sx;
                var top = (page.Height - points.Max(p => p.Y)) * sy;
                var bottom = (page.Height - points.Min(p => p.Y)) * sy;
                if (!double.IsFinite(left) || !double.IsFinite(right) || !double.IsFinite(top) || !double.IsFinite(bottom))
                    return Skipped("単語の座標が不正なため注釈を省略しました。");
                // 切り抜き境界の外にある文字を白埋め部分へ対応付けない。
                left = Math.Max(0, left); top = Math.Max(0, top);
                right = Math.Min(originalSize.Width, right); bottom = Math.Min(originalSize.Height, bottom);
                if (right > left && bottom > top) mapped.Add(new(word.Text, new(left, top, right - left, bottom - top)));
            }
            return TextAnnotations.Create(mapped, clusters, exclusions, dpi);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed("PDF テキスト層を解析できませんでした。画像の比較結果を確認してください。");
        }
    }

    private static PageTextAnnotations Skipped(string reason) => new(new Dictionary<int, string>(), [new("TEXT_ANNOTATION_SKIPPED", reason)]);
    private static PageTextAnnotations Failed(string reason) => new(new Dictionary<int, string>(), [new("TEXT_EXTRACTION_FAILED", reason)]);
    public void Dispose()
    {
        if (disposed) return;
        try { document?.Dispose(); }
        finally { stream?.Dispose(); disposed = true; }
    }
}
