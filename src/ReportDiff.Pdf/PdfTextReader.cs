using OpenCvSharp;
using ReportDiff.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Tokens;

namespace ReportDiff.Pdf;

public sealed record TextAnnotationWarning(string Code, string Message);
public sealed record PageTextAnnotations(IReadOnlyDictionary<int, string> TextByCluster,
    IReadOnlyList<TextAnnotationWarning> Warnings);

/// <summary>必要なページのテキストとフォントを調べる。描画用とは独立したストリームを所有する。</summary>
public sealed class PdfTextReader(string path, TextOptions? textOptions = null) : IDisposable
{
    private const int MaxCachedPages = 4;
    private const int MaxCachedRowWords = 16;
    private readonly TextOptions options = (textOptions ?? new()).Validated();
    private static readonly NearestNeighbourWordExtractor WordExtractor = new(new() { MaxDegreeOfParallelism = 1 });
    private FileStream? stream;
    private PdfDocument? document;
    private PdfFontInspector? fontInspector;
    private bool openFailed;
    private bool disposed;
    private readonly LinkedList<(int PageNumber, Page Page, Word[]? Words)> pageCache = new();
    private readonly Dictionary<(int PageNumber, Size Size, int Dpi), RowTextResult> rowWordsCache = [];
    private readonly Dictionary<int, IReadOnlyList<PdfFontWarning>> fontWarningsCache = [];

    /// <summary>行整列用の元ページ座標。矩形下端をベースラインの代用にしない。</summary>
    public RowTextResult ReadRowWords(int pageNumber, Size originalSize, int dpi)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var cacheKey = (pageNumber, originalSize, dpi);
        if (rowWordsCache.TryGetValue(cacheKey, out var cachedResult))
            return cachedResult;
        var computed = ReadRowWordsCore(pageNumber, originalSize, dpi);
        if (rowWordsCache.Count < MaxCachedRowWords)
            rowWordsCache[cacheKey] = computed;
        return computed;
    }

    private RowTextResult ReadRowWordsCore(int pageNumber, Size originalSize, int dpi)
    {
        try
        {
            var page = GetPage(pageNumber, out var cachedWords);
            if (page.Rotation.Value != 0) return Unavailable("rotated_page");
            if (page.Dictionary.TryGet(NameToken.UserUnit, out var unit)
                && (unit is not NumericToken number || number.Double != 1)) return Unavailable("user_unit");
            if (!double.IsFinite(page.Width) || !double.IsFinite(page.Height) || page.Width <= 0 || page.Height <= 0
                || originalSize.Width <= 0 || originalSize.Height <= 0
                || Math.Abs(Units.PointsToPixels(page.Width, dpi) - originalSize.Width) > 1
                || Math.Abs(Units.PointsToPixels(page.Height, dpi) - originalSize.Height) > 1) return Unavailable("page_coordinates");
            if (page.Letters.Count > options.MaxLettersPerPage) return Unavailable("letter_limit");
            var words = cachedWords ?? GetWords(pageNumber, page);
            if (words.Length > options.MaxWordsPerPage) return Unavailable("word_limit");
            var map = PageMap.Unaligned(originalSize, originalSize);
            var result = new List<RowWord>();
            foreach (var word in words)
            {
                var clean = TextLineLayout.Clean(word.Text);
                if (clean.Length == 0) continue;
                var points = new[] { word.BoundingBox.BottomLeft, word.BoundingBox.BottomRight, word.BoundingBox.TopLeft, word.BoundingBox.TopRight };
                var bounds = map.PdfToSource(PageSpace.A, page.Width, page.Height, points.Min(p => p.X), points.Min(p => p.Y),
                    points.Max(p => p.X), points.Max(p => p.Y));
                if (!PageMap.Finite(bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top
                    || bounds.Left < 0 || bounds.Top < 0 || bounds.Right > originalSize.Width || bounds.Bottom > originalSize.Height)
                    return Unavailable("word_coordinates");
                var baselines = new List<double>();
                foreach (var letter in word.Letters)
                {
                    var start = letter.StartBaseLine; var end = letter.EndBaseLine;
                    if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) || !double.IsFinite(end.X) || !double.IsFinite(end.Y)
                        || Math.Abs(start.Y - end.Y) > 0.000001 || end.X < start.X) return Unavailable("nonhorizontal_text");
                    baselines.Add((page.Height - start.Y) * originalSize.Height / page.Height);
                }
                if (baselines.Count == 0) return Unavailable("missing_baseline");
                result.Add(new(clean, bounds, baselines.AsReadOnly()));
            }
            return new(result.Count == 0 ? "no_text" : "available", null, result.AsReadOnly());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Unavailable(openFailed ? "open_failed" : "extraction_failed");
        }
        static RowTextResult Unavailable(string detail) => new("text_unavailable", detail, []);
    }

    public IReadOnlyList<PdfFontWarning> InspectFonts(int pageNumber)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (fontWarningsCache.TryGetValue(pageNumber, out var cached))
            return cached;
        try
        {
            var page = GetPage(pageNumber, out _);
            fontInspector ??= new PdfFontInspector(document!);
            var warnings = fontInspector.Inspect(page);
            fontWarningsCache[pageNumber] = warnings;
            return warnings;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [PdfFontInspector.Incomplete("PDF のページを解析できず、使用フォントの埋め込みを確認できません。画像の比較結果を確認してください。")];
        }
    }

    public PageTextAnnotations Annotate(int pageNumber, Size originalSize, int dpi,
        IReadOnlyList<DifferenceCluster> clusters, IReadOnlyList<RectMm> exclusions, GlobalShift? shift = null)
        => AnnotateCore(pageNumber, originalSize, dpi, clusters, exclusions, null, PageSpace.B, shift);

    public PageTextAnnotations AnnotateMapped(int pageNumber, PageMap map, PageSpace side, int dpi,
        IReadOnlyList<DifferenceCluster> clusters, IReadOnlyList<RectMm> exclusions)
        => AnnotateCore(pageNumber, map.SizeOf(side), dpi, clusters, exclusions, map, side, null);

    private PageTextAnnotations AnnotateCore(int pageNumber, Size originalSize, int dpi,
        IReadOnlyList<DifferenceCluster> clusters, IReadOnlyList<RectMm> exclusions, PageMap? map, PageSpace side, GlobalShift? shift)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (clusters.Count == 0) return new(new Dictionary<int, string>(), []);
        try
        {
            var page = GetPage(pageNumber, out var cachedWords);
            if (page.Rotation.Value != 0) return Skipped($"ページが {page.Rotation.Value} 度回転しているため注釈を省略しました。");
            if (page.Dictionary.TryGet(NameToken.UserUnit, out var unit)
                && (unit is not NumericToken number || number.Double != 1))
                return Skipped("標準と異なる UserUnit のため注釈を省略しました。");
            if (!double.IsFinite(page.Width) || !double.IsFinite(page.Height) || page.Width <= 0 || page.Height <= 0
                || originalSize.Width <= 0 || originalSize.Height <= 0
                || Math.Abs(Units.PointsToPixels(page.Width, dpi) - originalSize.Width) > 1
                || Math.Abs(Units.PointsToPixels(page.Height, dpi) - originalSize.Height) > 1)
                return Skipped("テキスト座標と描画サイズの対応を確認できないため注釈を省略しました。");
            if (page.Letters.Count > options.MaxLettersPerPage) return Skipped($"文字要素が {options.MaxLettersPerPage} 件を超えたため注釈を省略しました。");
            var words = cachedWords ?? GetWords(pageNumber, page);
            if (words.Length > options.MaxWordsPerPage) return Skipped($"単語が {options.MaxWordsPerPage} 件を超えたため注釈を省略しました。");
            map ??= PageMap.Global(originalSize, originalSize, originalSize, shift);
            var mapped = new List<TextWord>();
            foreach (var word in words)
            {
                var box = word.BoundingBox;
                var points = new[] { box.BottomLeft, box.BottomRight, box.TopLeft, box.TopRight };
                var source = map.PdfToSource(side, page.Width, page.Height, points.Min(p => p.X), points.Min(p => p.Y),
                    points.Max(p => p.X), points.Max(p => p.Y));
                if (!PageMap.Finite(source))
                    return Skipped("単語の座標が不正なため注釈を省略しました。");
                if (map.MapBounds(source, side, PageSpace.Canvas) is { } canvas)
                    mapped.Add(new(word.Text, canvas.Rectangle));
            }
            return TextAnnotations.Create(mapped, clusters, exclusions, dpi, options);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed(openFailed ? "PDF テキスト層を開けませんでした。"
                : "PDF テキスト層を解析できませんでした。画像の比較結果を確認してください。");
        }
    }

    private Page GetPage(int pageNumber, out Word[]? cachedWords)
    {
        if (openFailed) throw new InvalidDataException("PDF テキスト層を開けませんでした。");
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
                throw;
            }
        }
        for (var node = pageCache.First; node is not null; node = node.Next)
        {
            if (node.Value.PageNumber != pageNumber) continue;
            if (node != pageCache.First)
            {
                pageCache.Remove(node);
                pageCache.AddFirst(node);
            }
            cachedWords = node.Value.Words;
            return node.Value.Page;
        }
        var page = document.GetPage(pageNumber) ?? throw new InvalidDataException("PDF のページを解析できませんでした。");
        if (pageCache.Count >= MaxCachedPages)
            pageCache.RemoveLast();
        pageCache.AddFirst((pageNumber, page, null));
        cachedWords = null;
        return page;
    }

    private Word[] GetWords(int pageNumber, Page page)
    {
        var words = page.GetWords(WordExtractor).Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .Take(options.MaxWordsPerPage + 1).ToArray();
        for (var node = pageCache.First; node is not null; node = node.Next)
        {
            if (node.Value.PageNumber != pageNumber) continue;
            node.Value = (pageNumber, page, words);
            break;
        }
        return words;
    }

    private static PageTextAnnotations Skipped(string reason) => new(new Dictionary<int, string>(), [new("TEXT_ANNOTATION_SKIPPED", reason)]);
    private static PageTextAnnotations Failed(string reason) => new(new Dictionary<int, string>(), [new("TEXT_EXTRACTION_FAILED", reason)]);
    public void Dispose()
    {
        if (disposed) return;
        try { document?.Dispose(); }
        finally { stream?.Dispose(); pageCache.Clear(); rowWordsCache.Clear(); fontWarningsCache.Clear(); fontInspector = null; disposed = true; }
    }
}
