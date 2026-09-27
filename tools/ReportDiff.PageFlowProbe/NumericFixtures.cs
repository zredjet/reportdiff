using System.Security.Cryptography;
using System.Text.Json;

internal static class NumericFixtures
{
    internal sealed record Case(string Id, string A, string B, int Pairs, bool Gate, bool Adopt, bool Grouped, bool? AliasAdopt = null);
    internal static IReadOnlyList<Case> Create(string output)
    {
        var cases = new List<Case>(); var manifest = new List<object>();
        Add("terminal-number", 9, "SUM TOTAL 555", "SUM TOTAL 556");
        Add("terminal-minus", 9, "SUM TOTAL 555", "SUM TOTAL -555");
        Add("terminal-decimal", 9, "SUM TOTAL 555", "SUM TOTAL 55.5");
        Add("terminal-digits", 9, "SUM TOTAL 555", "SUM TOTAL 1555");
        Add("terminal-fields", 9, "SUM TOTAL 555 VAT 111", "SUM TOTAL 556 VAT 112");
        Add("middle-number", 7, "ROW VALUE 555", "ROW VALUE 556");
        Add("page-head-number", 6, "ROW VALUE 555", "ROW VALUE 556");
        Add("document-head-number", 0, "ROW VALUE 555", "ROW VALUE 556", insert: 3);
        Add("weak-actual-support", 9, "SUM TOTAL 555", "SUM TOTAL 556", extra: "faint", adopt: false, grouped: false, aliasAdopt: true);
        Add("carry-number", 5, "ROW VALUE 555", "ROW VALUE 556", pairs: 0, gate: false, adopt: false, grouped: false);
        Add("repeated-label", 9, "SUM TOTAL 555", "SUM TOTAL 556", extra: "label", pairs: 0, gate: false, adopt: false, grouped: false);
        Add("changed-label", 9, "SUM TOTAL 555", "SUM GRAND 556", pairs: 0, gate: false, adopt: false, grouped: false);
        Add("comma-format", 9, "SUM TOTAL 1,000", "SUM TOTAL 1,001", pairs: 0, gate: false, adopt: false, grouped: false);
        Add("alphanumeric", 9, "SUM TOTAL A555", "SUM TOTAL A556", pairs: 0, gate: false, adopt: false, grouped: false);
        Add("numeric-only", 9, "555", "556", pairs: 0, gate: false, adopt: false, grouped: false);
        Add("missing-support", 9, "SUM TOTAL 555", "SUM TOTAL 556", extra: "support", pairs: 0, gate: false, adopt: false, grouped: false);
        Add("reordered", 9, "SUM TOTAL 555", "SUM TOTAL 556", extra: "reorder", pairs: 0, gate: false, adopt: false, grouped: false);
        Add("adjacent-changes", 7, "ROW VALUE 555", "ROW VALUE 556", extra: "adjacent", pairs: 0, gate: false, adopt: false, grouped: false);
        Add("separated-changes", 2, "ROW VALUE 555", "ROW VALUE 556", extra: "separated", pairs: 2);
        Add("carry-pixel-change", 9, "SUM TOTAL 555", "SUM TOTAL 556", mutation: "pixels", gate: false, adopt: false, grouped: false);
        Add("carry-text-change", 9, "SUM TOTAL 555", "SUM TOTAL 556", extra: "carry", gate: false, adopt: false, grouped: false);
        Add("number-only", 9, "SUM TOTAL 555", "SUM TOTAL 556", insert: -1, gate: false, adopt: false, grouped: false);
        Add("unchanged-control", 9, "SUM TOTAL 555", "SUM TOTAL 555", pairs: 0);
        Copy("multiple-terminal", "nonflow-terminal-number", 1);
        Copy("multiple-final", "independent-number-change", 1);
        File.WriteAllText(Path.Combine(output, "fixtures.json"), JsonSerializer.Serialize(manifest, AggregationProbe.Json));
        return cases.AsReadOnly();

        void Add(string id, int row, string before, string after, int insert = 2, string extra = "", string mutation = "none",
            int pairs = 1, bool gate = true, bool adopt = true, bool grouped = true, bool? aliasAdopt = null)
        {
            var a = FlowFixture.Rows(new(id, 10), false); a[row] = before;
            if (extra == "label") a[7] = "SUM TOTAL 777";
            if (extra == "adjacent") a[8] = "NEXT VALUE 111";
            if (extra == "separated") a[8] = "NEXT VALUE 111";
            var b = a.ToList(); b[row] = after;
            if (extra == "support") b[8] = "DIFFERENT LAST SUPPORT";
            if (extra == "reorder") (b[6], b[7]) = (b[7], b[6]);
            if (extra is "adjacent" or "separated") b[8] = "NEXT VALUE 112";
            if (extra == "carry") b[5] = "DIFFERENT CARRY TEXT";
            if (insert >= 0) b.Insert(insert, "NEW ADDED");
            var folder = Path.Combine(output, id); Directory.CreateDirectory(folder);
            var faint = extra == "faint" ? new HashSet<string> { a[7], a[8] } : null;
            Write(folder, id, "a", FlowFixture.Create(new(id, 10), false, explicitPages: a.Chunk(6).ToArray(), faintRows: faint), a.Chunk(6).ToArray());
            Write(folder, id, "b", FlowFixture.Create(new(id, 10, mutation), true, explicitPages: b.Chunk(6).ToArray(), faintRows: faint), b.Chunk(6).ToArray());
            cases.Add(new(id, Path.Combine(folder, "a.pdf"), Path.Combine(folder, "b.pdf"), pairs, gate, adopt, grouped, aliasAdopt));
        }
        void Copy(string id, string prior, int pairs)
        {
            var folder = Path.Combine(output, id); Directory.CreateDirectory(folder);
            const string fixedFolder = "tests/ReportDiff.Tests/Fixtures/page-flow-multiple";
            var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(fixedFolder, "sha256.json")))!;
            var old = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixedFolder, "aggregation.json")));
            using (old)
            foreach (var side in new[] { "a", "b" })
            {
                var record = old.RootElement.EnumerateArray().Single(r => r.GetProperty("run").GetString() == prior + "-ab");
                var rows = record.GetProperty("input").GetProperty("rows").Deserialize<ReportDiff.Core.PageFlowAggregation.Row[]>(AggregationProbe.Json)!
                    .Where(r => (int)r.Side == (side == "a" ? 0 : 1)).GroupBy(r => r.Page).OrderBy(g => g.Key)
                    .Select(g => g.OrderBy(r => r.Start).Select(r => r.Text).ToArray()).ToArray();
                var bytes = File.ReadAllBytes(Path.Combine(fixedFolder, prior, side + ".pdf"));
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hashes[prior + "/" + side + ".pdf"])
                    throw new InvalidOperationException("固定した複数原因PDFが変更されています。");
                Write(folder, id, side, bytes, rows);
            }
            cases.Add(new(id, Path.Combine(folder, "a.pdf"), Path.Combine(folder, "b.pdf"), pairs, true, true, true));
        }
        void Write(string folder, string id, string side, byte[] bytes, string[][] rows)
        {
            File.WriteAllBytes(Path.Combine(folder, side + ".pdf"), bytes);
            manifest.Add(new { id, side, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), pages = rows.Length, expected_rows = rows });
        }
    }
}
