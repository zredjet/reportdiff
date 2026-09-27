using System.Text.Json;
using ReportDiff.Core;
using static ReportDiff.Core.PageFlowAggregation;

internal static class MultipleCauseAudit
{
    internal sealed record Result(int Permutations, int Rejections, int Completeness, int ContentPreserved);
    internal static Result Run(Input input, Func<Input, Decision>? evaluate = null)
    {
        evaluate ??= x => IndependentCauseAggregation.Evaluate(x);
        var expected = evaluate(input);
        if (expected.Status != "grouped") return new(0, 0, 0, 0);
        var permutations = 0; var rejections = 0; var completeness = 0; var content = 0;
        for (var seed = 0; seed < 5; seed++)
        {
            var random = new Random(seed);
            var shuffled = input with { Rows = input.Rows.OrderBy(_ => random.Next()).ToArray(), Links = input.Links.OrderBy(_ => random.Next()).ToArray(),
                Pages = input.Pages.OrderBy(_ => random.Next()).Select(p => p with { Structures = p.Structures.OrderBy(_ => random.Next()).ToArray() }).ToArray() };
            Require(Serialize(evaluate(shuffled)) == Serialize(expected), "列挙順"); permutations++;
        }
        Reject(input with { GateReady = false }); Reject(input with { SelectionLimited = true });
        for (var i = 0; i < input.Rows.Count; i++)
        {
            Reject(input with { Rows = input.Rows.Where((_, j) => i != j).ToArray() });
            Reject(input with { Rows = input.Rows.Append(input.Rows[i]).ToArray() });
            var changed = input.Rows.ToArray(); changed[i] = changed[i] with { Start = changed[i].Start + 1 };
            Reject(input with { Rows = changed });
        }
        for (var i = 0; i < input.Links.Count; i++)
        {
            var link = input.Links[i];
            Reject(input with { Links = input.Links.Where((_, j) => i != j).ToArray() });
            Reject(input with { Links = input.Links.Append(link).ToArray() });
            foreach (var altered in new[] { link with { Status = "candidate" }, link with { Reason = "failed" }, link with { Source = null },
                link with { Source = new(link.Source!.Page, link.Source.Top + 1, link.Source.Height) }, link with { Text = ["UNKNOWN"] } })
                Reject(input with { Links = input.Links.Select((l, j) => i == j ? altered : l).ToArray() });
        }
        for (var i = 0; i < input.Pages.Count; i++)
        {
            var page = input.Pages[i];
            Reject(input with { Pages = input.Pages.Where((_, j) => i != j).ToArray() });
            Reject(input with { Pages = input.Pages.Append(page).ToArray() });
            Reject(Patch(page with { DifferenceCount = page.DifferenceCount + 1 }));
            var incomplete = evaluate(Patch(page with { Complete = false }));
            Require(incomplete.Status == "grouped" && incomplete.AggregatedDifferenceCount == expected.AggregatedDifferenceCount
                && !incomplete.DifferenceCountComplete && incomplete.AggregatedDifferenceCountComplete == false, "網羅性"); completeness++;
            var changed = evaluate(Patch(page with { Clusters = page.Clusters + 1, DifferenceCount = page.DifferenceCount + 1 }));
            Require(changed.Status == "grouped" && changed.AggregatedDifferenceCount == expected.AggregatedDifferenceCount + 1, "内容クラスタ保持"); content++;
            for (var j = 0; j < page.Structures.Count; j++)
            {
                var s = page.Structures[j];
                Reject(Patch(page with { Structures = page.Structures.Where((_, k) => j != k).ToArray(), DifferenceCount = page.DifferenceCount - 1 }));
                Reject(Patch(page with { Structures = page.Structures.Append(s).ToArray(), DifferenceCount = page.DifferenceCount + 1 }));
                Reject(Patch(page with { Structures = page.Structures.Select((v, k) => j == k ? v with { Excluded = true } : v).ToArray(), DifferenceCount = page.DifferenceCount - 1 }));
                if (s.Kind == "block_moved") Reject(Patch(page with { Structures = page.Structures.Select((v, k) => j == k ? v with { Dy = v.Dy + 1 } : v).ToArray() }));
            }
            Input Patch(Page replacement) => input with { Pages = input.Pages.Select((p, j) => i == j ? replacement : p).ToArray() };
        }
        return new(permutations, rejections, completeness, content);

        void Reject(Input altered)
        {
            var result = evaluate(altered);
            Require(result.Status == "skipped" && result.Groups.Count == 0 && result.AggregatedDifferenceCount == result.DifferenceCount, "証拠破損: " + Serialize(altered));
            rejections++;
        }
        static string Serialize<T>(T value) => JsonSerializer.Serialize(value, AggregationProbe.Json);
        static void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException("複数原因の監査失敗: " + label); }
    }
}
