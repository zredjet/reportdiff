using System.Runtime.Versioning;
using OpenCvSharp;
using PDFtoImage;
using PDFtoImage.Exceptions;
using SkiaSharp;

namespace ReportDiff.Pdf;

/// <summary>PDF のストリームを所有する。ページ番号は 1 始まり。呼び出し側で using によって破棄する。</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class PdfReader : IDisposable
{
    public const int MaximumPagePixels = 16000;
    // 別の PdfReader インスタンスからの呼び出しも含め、PDFium の処理を逐次実行する。
    private static readonly object PdfiumLock = new();
    private readonly FileStream stream;
    private bool disposed;

    private PdfReader(FileStream stream, int pageCount)
    {
        this.stream = stream;
        PageCount = pageCount;
    }

    public int PageCount { get; }

    public static PdfReader Open(string path)
    {
        FileStream? stream = null;
        try
        {
            // ファイルのパスは .NET だけで扱い、PDFium にはストリームを渡す。
            stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[5];
            var length = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (InputFormatDetector.Detect(header[..length]) != InputFormat.Pdf)
                throw new PdfReadException("PDF 形式のファイルを指定してください。");
            lock (PdfiumLock)
            {
                stream.Position = 0;
                var pageCount = Conversion.GetPageCount(stream, leaveOpen: true);
                if (pageCount < 1) throw new PdfReadException("PDF に読み込めるページがありません。");
                var reader = new PdfReader(stream, pageCount);
                stream = null; // 所有権を reader に移す。
                return reader;
            }
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            throw new PdfReadException($"PDF ファイルを読み込めません。パス・破損・パスワード保護を確認してください: {path}", ex);
        }
        finally { stream?.Dispose(); }
    }

    /// <summary>指定ページだけを描画する。戻り値の LoadedImage は呼び出し側が所有する。</summary>
    public LoadedImage ReadPage(int pageNumber, int dpi = 300)
    {
        lock (PdfiumLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pageNumber < 1 || pageNumber > PageCount)
                throw new PdfReadException($"PDF のページ番号は 1〜{PageCount} を指定してください: {pageNumber}");
            if (dpi is < 72 or > 1200) throw new PdfReadException("PDF の DPI は 72〜1200 にしてください。");
            try
            {
                // ラスタ画像を確保する前に、選択したページの寸法だけを確認する。
                stream.Position = 0;
                var size = Conversion.GetPageSize(stream, page: pageNumber - 1, leaveOpen: true);
                ValidateDimensions(size.Width / 72.0 * dpi, size.Height / 72.0 * dpi, pageNumber);
                stream.Position = 0;
                using var bitmap = Conversion.ToImage(stream, page: pageNumber - 1, leaveOpen: true,
                    options: new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true,
                        AntiAliasing: PdfAntiAliasing.All, BackgroundColor: SKColors.White));
                ValidateDimensions(bitmap.Width, bitmap.Height, pageNumber);
                return new LoadedImage(ToBgr(bitmap), InputFormat.Pdf, dpi);
            }
            catch (Exception ex) when (ex is PdfException or IOException or ArgumentException
                or InvalidOperationException or OpenCVException)
            {
                throw new PdfReadException($"PDF の {pageNumber} ページを描画できません。破損していないか確認してください。", ex);
            }
        }
    }

    private static void ValidateDimensions(double width, double height, int pageNumber)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1)
            throw new PdfReadException($"PDF の {pageNumber} ページの寸法が不正です。");
        if (width > MaximumPagePixels || height > MaximumPagePixels)
            throw new PdfReadException($"PDF の {pageNumber} ページが 1 辺 {MaximumPagePixels}px を超えます。dpi を下げてください。");
    }

    private static Mat ToBgr(SKBitmap bitmap)
    {
        if (bitmap.ColorType != SKColorType.Bgra8888)
        {
            using var converted = bitmap.Copy(SKColorType.Bgra8888)
                ?? throw new PdfReadException("PDF の画素形式を変換できません。");
            return ToBgr(converted);
        }
        // SKBitmap のストライドを維持し、破棄前に独立した BGR Mat へコピーする。
        using var bgra = Mat.FromPixelData(bitmap.Height, bitmap.Width, MatType.CV_8UC4,
            bitmap.GetPixels(), bitmap.RowBytes);
        var bgr = new Mat();
        try
        {
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            return bgr;
        }
        catch { bgr.Dispose(); throw; }
    }

    public void Dispose()
    {
        lock (PdfiumLock)
        {
            if (disposed) return;
            stream.Dispose();
            disposed = true;
        }
    }
}

public sealed class PdfReadException(string message, Exception? inner = null) : Exception(message, inner);
