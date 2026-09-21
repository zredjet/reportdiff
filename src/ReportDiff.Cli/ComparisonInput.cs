using System.Runtime.Versioning;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;

namespace ReportDiff.Cli;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
internal sealed class ComparisonInput : IDisposable
{
    private readonly string path;
    private readonly PdfReader? pdf;
    private readonly PdfTextReader? text;
    public InputFormat Format { get; }
    public int PageCount => pdf?.PageCount ?? 1;
    public int Dpi { get; }

    public ComparisonInput(string path, AppSettings settings)
    {
        this.path = path;
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            var length = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            Format = InputFormatDetector.Detect(header[..length]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommandLineException($"入力ファイルを読み込めません: {path}");
        }
        if (Format == InputFormat.Unknown) throw new CommandLineException($"入力は PDF・PNG・JPEG・BMP・TIFF のいずれかにしてください: {path}");
        if (Format == InputFormat.Pdf) { pdf = PdfReader.Open(path); text = new PdfTextReader(path, settings.Text); }
        Dpi = Format == InputFormat.Pdf ? settings.Dpi : settings.ImageDpi;
    }

    public ReportInput Describe() => ReportInput.FromFile(path, Format, PageCount);
    public LoadedImage ReadPage(int page) => pdf is null ? ImageReader.Read(path, Dpi) : pdf.ReadPage(page, Dpi);
    public IReadOnlyList<PdfFontWarning> InspectFonts(int page) => text?.InspectFonts(page) ?? [];
    public PageTextAnnotations? Annotate(int page, Size originalSize, IReadOnlyList<DifferenceCluster> clusters,
        IReadOnlyList<RectMm> exclusions, GlobalShift? shift = null) => text?.Annotate(page, originalSize, Dpi, clusters, exclusions, shift);
    public void Dispose()
    {
        try { text?.Dispose(); }
        finally { pdf?.Dispose(); }
    }
}
