using System.Security.Cryptography;
using System.Text.Json;
using ReportDiff.Core;

// 正解は外側の照合器向け。CandidateInference / CandidateSurfaceには渡さない。
internal static class CandidateFixtures
{
    internal static void Create(string fixedInputs, string output)
    {
        using var old = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixedInputs, "observations.json")));
        foreach (var item in old.RootElement.EnumerateArray())
        {
            var test = FlowFixture.Cases.Single(c => c.Id == item.GetProperty("test").GetProperty("id").GetString());
            if (Hash(FlowFixture.Create(test, false)) != item.GetProperty("a_sha256").GetString()
                || Hash(FlowFixture.Create(test, true)) != item.GetProperty("b_sha256").GetString())
                throw new InvalidOperationException("既定レイアウトの固定PDFが変わっています。");
        }
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("出力先が空ではありません。");
        Directory.CreateDirectory(output);
        var records = new List<object>();
        foreach (var (test, layout) in new (FlowCase, FlowLayout)[]
        {
            (new("shifted-R10", 10), new(288, 420, 84, 30, 372)),
            (new("shifted-R11", 12), new(288, 456, 96, 36, 408)),
            (new("shifted-chain3", 16), new(288, 420, 84, 30, 372)),
            (new("single-support", 6), new(288, 456, 96, 36, 408)),
            (new("paired-content-tone", 10, "paired"), new()),
            (new("paired-boundary-tone", 10, "paired-edge"), new())
        })
        {
            var folder = Path.Combine(output, test.Id); Directory.CreateDirectory(folder);
            var a = FlowFixture.Create(test, false, layout); var b = FlowFixture.Create(test, true, layout);
            File.WriteAllBytes(Path.Combine(folder, "a.pdf"), a); File.WriteAllBytes(Path.Combine(folder, "b.pdf"), b);
            records.Add(new { test, layout, a_sha256 = Hash(a), b_sha256 = Hash(b),
                links = FlowFixture.Links(test).Select(l => new { link = l,
                    band_a = new { y = Px(layout.Top + l.ASlot * layout.Step), h = Px(layout.Step) },
                    band_b = new { y = Px(layout.Top + l.BSlot * layout.Step), h = Px(layout.Step) } }) });
        }
        File.WriteAllText(Path.Combine(output, "observations.json"), JsonSerializer.Serialize(records,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
    }
    private static int Px(double points) => Units.RoundPixels(points * 25.4 / 72, 300);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
