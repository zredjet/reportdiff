using System.Text.Json;
using static CausalAggregation;

internal static class AggregationAudit
{
    internal static void Run(string folder)
    {
        using var file = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "aggregation.json")));
        var checks = new List<object>();
        foreach (var record in file.RootElement.GetProperty("records").EnumerateArray())
        {
            var name = record.GetProperty("run").GetString()!;
            var input = record.GetProperty("input").Deserialize<Input>(AggregationProbe.Json)!;
            var expected = Evaluate(input);
            if (Signature(expected) != Signature(record.GetProperty("decision").Deserialize<Decision>(AggregationProbe.Json)!))
                throw new InvalidOperationException("保存結果が再評価と異なります。");
            CheckEqual("reverse_rows", input with { Rows = input.Rows.Reverse().ToArray() });
            CheckEqual("reverse_links", input with { Links = input.Links.Reverse().ToArray() });
            CheckEqual("reverse_pages_and_structures", input with { Pages = input.Pages.Reverse().Select(p => p with { Structures = p.Structures.Reverse().ToArray() }).ToArray() });
            if (expected.Status != "grouped") continue;
            Fallback("gate_failure", input with { GateReady = false });
            Fallback("limited_selection", input with { SelectionLimited = true });
            foreach (var row in input.Rows)
            {
                Fallback("missing_row", input with { Rows = input.Rows.Where(r => r != row).ToArray() });
                Fallback("duplicate_row", input with { Rows = input.Rows.Append(row).ToArray() });
            }
            foreach (var link in input.Links)
            {
                Fallback("missing_link", input with { Links = input.Links.Where(l => l != link).ToArray() });
                Fallback("duplicate_link", input with { Links = input.Links.Append(link).ToArray() });
                foreach (var altered in new[] { link with { Source = link.Source with { Start = link.Source.Start + 1 } },
                    link with { Target = link.Target with { Length = link.Target.Length + 1 } },
                    link with { Target = link.Target with { Page = link.Target.Page + 1 } },
                    link with { Text = ["UNRELATED TEXT"] }, link with { Status = "skipped" } })
                    Fallback("altered_link", input with { Links = input.Links.Select(l => l == link ? altered : l).ToArray() });
            }
            foreach (var page in input.Pages)
            {
                var limited = Evaluate(input with { Pages = input.Pages.Select(p => p.Number == page.Number ? p with { Complete = false } : p).ToArray() });
                if (limited.AggregatedDifferenceCount != expected.AggregatedDifferenceCount || limited.AggregatedDifferenceCountComplete != false)
                    throw new InvalidOperationException("上限等による網羅性falseを引き上げました。");
                checks.Add(new { run = name, check = "incomplete_preserved", status = limited.Status, reason = limited.Reason });
                foreach (var structure in page.Structures)
                {
                    Mutate("missing_structure", page with { Structures = page.Structures.Where(s => s != structure).ToArray(), DifferenceCount = page.DifferenceCount - 1 });
                    Mutate("duplicate_structure", page with { Structures = page.Structures.Append(structure).ToArray(), DifferenceCount = page.DifferenceCount + 1 });
                    Mutate("excluded_structure", page with { Structures = page.Structures.Select(s => s == structure ? s with { Excluded = true } : s).ToArray(), DifferenceCount = page.DifferenceCount - 1 });
                    if (structure.Kind == "block_moved") Mutate("wrong_displacement", page with
                    { Structures = page.Structures.Select(s => s == structure ? s with { Dy = s.Dy + 1 } : s).ToArray() });
                }
                void Mutate(string label, Page changed) => Fallback(label, input with { Pages = input.Pages.Select(p => p.Number == page.Number ? changed : p).ToArray() });
            }

            void CheckEqual(string check, Input altered)
            {
                var result = Evaluate(altered);
                if (Signature(result) != Signature(expected)) throw new InvalidOperationException("列挙順で集約が変わりました。");
                checks.Add(new { run = name, check, status = result.Status, reason = result.Reason });
            }
            void Fallback(string check, Input altered)
            {
                var result = Evaluate(altered);
                if (result.Status != "skipped" || result.Groups.Count > 0 || result.AggregatedDifferenceCount != result.DifferenceCount)
                    throw new InvalidOperationException($"{name}/{check}: 根拠を失っても集約が残りました。");
                checks.Add(new { run = name, check, status = result.Status, reason = result.Reason });
            }
        }
        File.WriteAllText(Path.Combine(folder, "aggregation-audit.json"), JsonSerializer.Serialize(checks, AggregationProbe.Json));
        Console.WriteLine($"{checks.Count}条件の順序・欠落・重複・網羅性を確認しました。");
    }
    private static string Signature(Decision d) => JsonSerializer.Serialize(d, AggregationProbe.Json);
}
