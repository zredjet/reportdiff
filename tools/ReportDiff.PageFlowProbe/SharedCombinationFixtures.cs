using System.Security.Cryptography;
using System.Text.Json;

internal static class SharedCombinationFixtures
{
    internal static void Create(string output)
    {
        if (Directory.Exists(output)) throw new ArgumentException("出力先が既に存在します。");
        Directory.CreateDirectory(output); var hashes = new Dictionary<string, string>();
        foreach (var kind in new[] { "number", "fields", "anchor-change", "carry-change" })
        {
            var a = Enumerable.Range(0, 11).Select(i => $"ROW ITEM {(char)('A' + i)}{(char)('A' + i)}").ToArray();
            a[^1] = kind == "fields" ? "SUM TOTAL 555 VAT 111" : "SUM TOTAL 555";
            if (kind == "carry-change") a[5] = "CARRY VALUE 555";
            var b = a.ToList(); b[^1] = kind == "fields" ? "SUM TOTAL 556 VAT 112" : "SUM TOTAL 556";
            if (kind == "anchor-change") b[^2] = "OTHER LAST LABEL";
            if (kind == "carry-change") b[5] = "CARRY VALUE 556";
            b.Insert(2, "FIRST ADDED"); b.Insert(9, "SECOND ADDED");
            foreach (var side in new[] { "a", "b" })
            {
                var rows = side == "a" ? a : b.ToArray();
                string[][] pages = [rows.Take(6).ToArray(), rows.Skip(6).ToArray()];
                var bytes = FlowFixture.Create(new("shared-" + kind, 11), side == "b", explicitPages: pages);
                var relative = "shared-" + kind + "/" + side + ".pdf"; var file = Path.Combine(output, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, bytes);
                hashes.Add(relative, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
        }
        File.WriteAllText(Path.Combine(output, "sha256.json"), JsonSerializer.Serialize(hashes, AggregationProbe.Json));
    }
}
