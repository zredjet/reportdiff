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
    private const int MaxCachedPages = 4;
    private readonly string path;
    private readonly PdfReader? pdf;
    private readonly PdfTextReader? text;
    private readonly LinkedList<(int Page, Mat Pixels)> pageCache = new();
    public InputFormat Format { get; }
    internal string? InitialSha256 { get; }
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
            if (settings.Rows.CarryEnabled && Format == InputFormat.Pdf)
            {
                stream.Position = 0;
                InitialSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
            }
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
    public LoadedImage ReadPage(int page)
    {
        for (var node = pageCache.First; node is not null; node = node.Next)
        {
            if (node.Value.Page != page) continue;
            if (node != pageCache.First)
            {
                pageCache.Remove(node);
                pageCache.AddFirst(node);
            }
            return new LoadedImage(node.Value.Pixels.Clone(), Format, Dpi);
        }
        using var loaded = pdf is null ? ImageReader.Read(path, Dpi) : pdf.ReadPage(page, Dpi);
        var pixels = loaded.TakePixels();
        if (pageCache.Count >= MaxCachedPages)
        {
            var last = pageCache.Last!;
            pageCache.RemoveLast();
            last.Value.Pixels.Dispose();
        }
        pageCache.AddFirst((page, pixels));
        return new LoadedImage(pixels.Clone(), Format, Dpi);
    }
    public IReadOnlyList<PdfFontWarning> InspectFonts(int page) => text?.InspectFonts(page) ?? [];
    public RowTextResult ReadRowWords(int page, PageMap map, PageSpace side) => text is null ? new("no_text", null, [])
        : RowTextAnnotations.ToAligned(text.ReadRowWords(page, map.SizeOf(side), Dpi), map, side);
    public PageTextAnnotations? Annotate(int page, PageMap map, PageSpace side, IReadOnlyList<DifferenceCluster> clusters,
        IReadOnlyList<RectMm> exclusions) => text?.AnnotateMapped(page, map, side, Dpi, clusters, exclusions);
    public void Dispose()
    {
        foreach (var (_, pixels) in pageCache)
            pixels.Dispose();
        pageCache.Clear();
        try { text?.Dispose(); }
        finally { pdf?.Dispose(); }
    }
}
