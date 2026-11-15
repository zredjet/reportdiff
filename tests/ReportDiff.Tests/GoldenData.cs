using System.Collections.Concurrent;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;

namespace ReportDiff.Tests;

internal static class GoldenData
{
    private sealed record CachedRawDifference(int Rows, int Cols, byte[] MaskBytes, int AbsorbedGroups, int MaxShiftPx);

    private static readonly JsonElement[] CachedCases = LoadCases();
    private static readonly ConcurrentDictionary<(string Id, string Side), byte[]> CachedPngBytes = new();
    private static readonly ConcurrentDictionary<string, Lazy<CachedRawDifference>> CachedUnoptimizedDifferences = new();

    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "golden");

    private static JsonElement[] LoadCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(DirectoryPath, "expected.json")));
        return document.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.Clone()).ToArray();
    }

    public static JsonElement[] Cases() => CachedCases;

    public static ComparisonParameters Parameters(JsonElement test)
    {
        var p = test.GetProperty("params");
        return new()
        {
            Dpi = p.GetProperty("dpi").GetInt32(),
            Diff = new()
            {
                MaxShiftMm = p.GetProperty("max_shift_mm").GetDouble(),
                ColorThreshold = p.GetProperty("color_threshold").GetDouble(),
                EdgeTolerance = p.GetProperty("edge_tolerance").GetDouble()
            },
            Ink = new()
            {
                BackgroundRadiusMm = p.GetProperty("ink_background_radius_mm").GetDouble(),
                ContrastThreshold = p.GetProperty("ink_contrast_threshold").GetDouble()
            },
            Cluster = new()
            {
                MergeXMm = p.GetProperty("merge_x_mm").GetDouble(), MergeYMm = p.GetProperty("merge_y_mm").GetDouble(),
                MinPixels = p.GetProperty("min_pixels").GetInt32(), MaxDiffRatio = p.GetProperty("max_diff_ratio").GetDouble(),
                ReadingBandMm = p.GetProperty("reading_band_mm").GetDouble()
            },
            Exclude = p.GetProperty("exclude_mm").EnumerateArray()
                .Select(e => new RectMm(e[0].GetDouble(), e[1].GetDouble(), e[2].GetDouble(), e[3].GetDouble())).ToArray()
        };
    }

    public static Mat Image(string id, string side)
    {
        var bytes = CachedPngBytes.GetOrAdd((id, side), static key =>
            File.ReadAllBytes(Path.Combine(DirectoryPath, $"{key.Id}_{key.Side}.png")));
        return Cv2.ImDecode(bytes, ImreadModes.Color);
    }

    public static RawDifference UnoptimizedDifference(string id, Mat a, Mat b, ComparisonParameters parameters)
    {
        var cached = CachedUnoptimizedDifferences.GetOrAdd(id, _ => new Lazy<CachedRawDifference>(() =>
        {
            using var diff = TolerantDifference.Calculate(a, b, parameters, false);
            return new(diff.RawMask.Rows, diff.RawMask.Cols, MatBuffers.Bytes(diff.RawMask), diff.AbsorbedGroups, diff.MaxShiftPx);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        var mask = new Mat(cached.Rows, cached.Cols, MatType.CV_8UC1);
        cached.MaskBytes.CopyTo(mask.AsSpan<byte>());
        return new(mask, cached.AbsorbedGroups, cached.MaxShiftPx);
    }
}
