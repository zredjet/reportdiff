using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;

namespace ReportDiff.Tests;

internal static class GoldenData
{
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "golden");
    public static JsonElement[] Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(DirectoryPath, "expected.json")));
        return document.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.Clone()).ToArray();
    }

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
            Cluster = new()
            {
                MergeXMm = p.GetProperty("merge_x_mm").GetDouble(), MergeYMm = p.GetProperty("merge_y_mm").GetDouble(),
                MinPixels = p.GetProperty("min_pixels").GetInt32(), MaxDiffRatio = p.GetProperty("max_diff_ratio").GetDouble()
            },
            Exclude = p.GetProperty("exclude_mm").EnumerateArray()
                .Select(e => new RectMm(e[0].GetDouble(), e[1].GetDouble(), e[2].GetDouble(), e[3].GetDouble())).ToArray()
        };
    }

    public static Mat Image(string id, string side) =>
        Cv2.ImDecode(File.ReadAllBytes(Path.Combine(DirectoryPath, $"{id}_{side}.png")), ImreadModes.Color);
}
