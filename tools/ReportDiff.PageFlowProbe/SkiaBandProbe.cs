using System.Text.Json;
using OpenCvSharp;

// 推定器が見送った帯を診断する。結果を採否へ戻さない。
internal static class SkiaBandProbe
{
    internal static void Run(string candidates, string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("出力先が空ではありません。");
        Directory.CreateDirectory(output);
        using var records = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(candidates, "candidates.json")));
        var measurements = new List<object>();
        foreach (var run in records.RootElement.EnumerateArray())
        {
            var id = run.GetProperty("id").GetString()!;
            if (run.GetProperty("reverse").GetBoolean() || run.TryGetProperty("selected", out _)
                || id is not ("skia-R10" or "skia-R11" or "skia-chain3" or "skia-fractional" or "skia-band-tone")) continue;
            var directory = Path.Combine(candidates, run.GetProperty("folder").GetString()!);
            foreach (var evidence in run.GetProperty("result").GetProperty("evidence").EnumerateArray())
            {
                var source = evidence.GetProperty("source"); var target = evidence.GetProperty("target");
                using var a = Read(source); using var b = Read(target);
                measurements.Add(ContextProbe.Check(id + "-" + source.GetProperty("page").GetInt32(), a, b,
                    Rect(source, a.Width), Rect(target, b.Width), false, new { source = "inferred_skia_band", fixture = id }, output));
            }
            Mat Read(JsonElement band) => Cv2.ImDecode(File.ReadAllBytes(Path.Combine(directory,
                $"{band.GetProperty("side").GetString()}-p{band.GetProperty("page").GetInt32()}.png")), ImreadModes.Color);
        }
        File.WriteAllText(Path.Combine(output, "context.json"), JsonSerializer.Serialize(measurements,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
    }
    private static Rect Rect(JsonElement band, int width) => new(0, band.GetProperty("start").GetInt32(), width, band.GetProperty("length").GetInt32());
}
