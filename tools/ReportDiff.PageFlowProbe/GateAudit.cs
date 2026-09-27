using System.Text.Json;
using static CandidateInference;

internal static class GateAudit
{
    internal static void Run(string folder)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
        using var input = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "candidates.json")));
        var checks = new List<object>();
        foreach (var run in input.RootElement.EnumerateArray())
        {
            var result = run.GetProperty("result"); var name = run.GetProperty("folder").GetString()!;
            var layout = new Prepared(result.GetProperty("status").GetString()!, result.GetProperty("reason").GetString(), [], []);
            var links = result.GetProperty("links").Deserialize<Proposal[]>(options)!;
            var pages = result.GetProperty("pages").EnumerateArray().Select(p =>
            {
                var paired = !p.GetProperty("status").GetString()!.StartsWith("only_in_", StringComparison.Ordinal);
                var built = p.GetProperty("status").GetString() == "built";
                var bands = !paired ? p.GetProperty("verified_bands").Deserialize<Band[]>(options)! : built
                    ? p.GetProperty("removed").EnumerateArray().Where(r => r.GetProperty("proof").GetString() == "verified_carry_range")
                        .Select(r => r.GetProperty("band").Deserialize<Band>(options)!).ToArray() : [];
                return new DocumentGate.Page(p.GetProperty("page").GetInt32(), paired, built, bands);
            }).ToArray();
            var decision = DocumentGate.Evaluate(layout, links, pages);
            var saved = result.GetProperty("gate").Deserialize<DocumentGate.Decision>(options)!;
            if (Signature(decision) != Signature(saved)) throw new InvalidOperationException("保存した採否と再評価が異なります。");
            foreach (var (reverseLinks, reversePages) in new[] { (true, false), (false, true), (true, true) })
            {
                var alternate = DocumentGate.Evaluate(layout, reverseLinks ? links.Reverse().ToArray() : links,
                    reversePages ? pages.Reverse().ToArray() : pages);
                if (Signature(alternate) != Signature(decision)) throw new InvalidOperationException("列挙順で文書の採否が変化しました。");
            }
            var missingRange = 0; var failedPage = 0; var rejectedCandidate = 0;
            if (decision.Ready)
            {
                foreach (var page in pages)
                foreach (var band in page.ProofBands)
                {
                    MustFallback(DocumentGate.Evaluate(layout, links, pages.Select(p => p.Number == page.Number
                        ? p with { ProofBands = p.ProofBands.Where(b => b != band).ToArray() } : p).ToArray()));
                    missingRange++;
                }
                foreach (var page in pages.Where(p => p.Paired))
                {
                    MustFallback(DocumentGate.Evaluate(layout, links, pages.Select(p => p.Number == page.Number
                        ? p with { MapBuilt = false } : p).ToArray()));
                    failedPage++;
                }
                for (var i = 0; i < links.Length; i++)
                {
                    MustFallback(DocumentGate.Evaluate(layout, links.Select((p, j) => j == i
                        ? p with { Status = "skipped", Reason = "injected_failure" } : p).ToArray(), pages));
                    rejectedCandidate++;
                }
                MustFallback(DocumentGate.Evaluate(layout with { Status = "skipped", Reason = "injected_failure" }, links, pages));
            }
            checks.Add(new { run = name, decision.Ready, order_permutations = 3,
                missing_range_checks = missingRange, failed_page_checks = failedPage,
                rejected_candidate_checks = rejectedCandidate, failed_layout_checks = decision.Ready ? 1 : 0 });
        }
        File.WriteAllText(Path.Combine(folder, "gate-audit.json"), JsonSerializer.Serialize(checks, options));
        Console.WriteLine($"{checks.Count}実行の列挙順と、一つの根拠を失った場合の全体見送りを確認しました。");

        string Signature(DocumentGate.Decision d) => JsonSerializer.Serialize(new { d.Ready,
            reasons = d.Reasons.Order(), links = d.SelectedLinks.Select(p => JsonSerializer.Serialize(p, options)).Order(),
            selections = d.Selections.OrderBy(p => p.Page) }, options);
        static void MustFallback(DocumentGate.Decision d)
        {
            if (d.Ready || d.SelectedLinks.Count != 0 || d.Selections.Any(s => s.Choice == "candidate"))
                throw new InvalidOperationException("一つの根拠の不足が部分採用を残しました。");
        }
    }
}
