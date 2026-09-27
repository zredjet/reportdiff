using System.Security.Cryptography;
using System.Text.Json;

internal static class CausalFixtures
{
    internal static void Create(string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("出力先が空ではありません。");
        Directory.CreateDirectory(output);
        var records = new List<object>();
        foreach (var test in new FlowCase[] { new("two-inserts", 10, "two-inserts"),
            new("two-inserts-tone", 10, "two-inserts-tone"), new("flow-number-change", 10, "number") })
        {
            var folder = Path.Combine(output, test.Id); Directory.CreateDirectory(folder);
            var a = FlowFixture.Rows(test, false); var b = FlowFixture.Rows(test, true).ToList();
            if (test.Mutation.StartsWith("two-inserts", StringComparison.Ordinal)) b.Insert(9, "SECOND ADDED");
            if (test.Mutation == "number") b[^1] = "SUM TOTAL 556";
            var left = FlowFixture.Create(test, false, explicitPages: a.Chunk(6).ToArray());
            var right = FlowFixture.Create(test with { Mutation = test.Mutation == "two-inserts-tone" ? "paired" : "none" }, true,
                explicitPages: b.Chunk(6).ToArray());
            File.WriteAllBytes(Path.Combine(folder, "a.pdf"), left); File.WriteAllBytes(Path.Combine(folder, "b.pdf"), right);
            records.Add(new { test, a_sha256 = Hash(left), b_sha256 = Hash(right), expected_rows_a = a, expected_rows_b = b,
                links = new[] { new { link = new FlowLink(1, 5, 2, 0, a[5]), band_a = new { y = 800, h = 100 }, band_b = new { y = 300, h = 100 } } } });
        }
        File.WriteAllText(Path.Combine(output, "observations.json"), JsonSerializer.Serialize(records, AggregationProbe.Json));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
