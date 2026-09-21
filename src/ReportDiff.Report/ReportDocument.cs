using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReportDiff.Core;
using ReportDiff.Pdf;

namespace ReportDiff.Report;

public sealed record ReportDocument(int SchemaVersion, ReportTool Tool, DateTimeOffset GeneratedAt,
    ReportInputs Inputs, ReportConfiguration Config, ReportSummary Summary,
    IReadOnlyList<ReportWarning> Warnings, IReadOnlyList<ReportPage> Pages);
public sealed record ReportTool(string Name, string Version)
{
    public static ReportTool Current { get; } = new("reportdiff", "0.1.2");
}
public sealed record ReportInputs(ReportInput A, ReportInput B);
public sealed record ReportInput(string Path, string Type, int Pages, string Sha256)
{
    public static ReportInput FromFile(string path, InputFormat format, int pages)
    {
        if (format == InputFormat.Unknown || !Enum.IsDefined(format) || pages < 1)
            throw new ArgumentException("入力形式とページ数を確認してください。");
        using var stream = File.OpenRead(path);
        return new(path, format.ToString().ToLowerInvariant(), pages, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }
}
public sealed record ReportSummary(string Status, int PagesCompared, int PagesDifferent, int Clusters, int AbsorbedGroups);
public sealed record ReportWarning(string Code, string Message);
public sealed record PixelSize(int W, int H);
public sealed record PixelBox(int X, int Y, int W, int H);
public sealed record MillimeterBox(double X, double Y, double W, double H);
public sealed record PixelShift(int Dx, int Dy);
public sealed record PageImages(string? A, string? B, string? Overlay)
{
    public string? BOriginal { get; init; }
}
public sealed record ClusterCrops(string A, string B, string Diff);
public sealed record RawEvidencePadding(int Right, int Bottom);
public sealed record RawEvidenceSource(bool Missing, PixelSize? OriginalSizePx, RawEvidencePadding? PaddingPx, string Image);
public sealed record RawEvidence(string Method, string CoordinateSystem, int Dpi, PixelSize CanvasSizePx,
    RawEvidenceSource A, RawEvidenceSource B, string Overlay);
public sealed record ReportCluster(int Id, PixelBox BboxPx, MillimeterBox BboxMm, int Pixels, double FillRatio,
    string? Kind, PixelShift? ShiftPx, string? TextA, string? TextB, ClusterCrops Crops)
{
    public IReadOnlyList<int> RelatedClusterIds { get; init; } = [];
}
public sealed record ReportPage(int Page, string Status, PixelSize SizePx, bool SizeMismatch, int RawPixels,
    int NoiseDropped, int AbsorbedGroups, int MaxShiftPx, PageImages Images, IReadOnlyList<ReportCluster> Clusters)
{
    public PixelShift? GlobalShiftPx { get; init; }
    public AlignmentResult Alignment { get; init; } = AlignmentResult.Disabled;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RawEvidence? RawEvidence { get; init; }
}

// CLI への逆参照を作らず、設定の実効値を出力用の型で受け取る。
public sealed record ReportConfiguration(int Dpi, int ImageDpi, DiffOptions Diff, ClusterOptions Cluster,
    IReadOnlyList<ReportExclusion> Exclude, ReportOutputOptions Report)
{
    public InkOptions Ink { get; init; } = new();
    public MoveOptions Move { get; init; } = new();
    public AlignOptions Align { get; init; } = new();
    public TextOptions Text { get; init; } = new();
}
public sealed record ReportOutputOptions(double CropMarginMm)
{
    public double SnippetMarginMm { get; init; } = 1.0;
    public bool RawOverlay { get; init; }
}
public sealed record ReportExclusion(
    [property: JsonConverter(typeof(ExclusionPageConverter))] int? Page,
    double X, double Y, double W, double H, string Note);

public sealed class ExclusionPageConverter : JsonConverter<int?>
{
    public override bool HandleNull => true;
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.GetString() == "all") return null;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var page) && page > 0) return page;
        throw new JsonException("除外領域の page は all または 1 以上の整数である必要があります。");
    }
    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteStringValue("all");
        else writer.WriteNumberValue(value.Value);
    }
}

public static class ReportJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
