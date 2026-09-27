using System.Text.Json;
using ReportDiff.Core;
using static IndependentNumericRows;

internal static class NumericAudit
{
    internal sealed record Result(int Permutations, int Rejections, int RetainedOtherPairs);
    internal static Result Run(Input input, Decision expected)
    {
        var permutations = 0; var rejections = 0; var retained = 0;
        for (var seed = 0; seed < 5; seed++)
        {
            var random = new Random(seed);
            var shuffled = input with { Rows = input.Rows.OrderBy(_ => random.Next()).ToArray(), Pages = input.Pages.OrderBy(_ => random.Next()).ToArray() };
            Require(JsonSerializer.Serialize(Find(shuffled), AggregationProbe.Json) == JsonSerializer.Serialize(expected, AggregationProbe.Json), "列挙順"); permutations++;
        }
        if (expected.Pairs.Count == 0) return new(permutations, 0, 0);
        Reject(input with { Prepared = false }); Reject(input with { SelectionLimited = true });
        Reject(input with { Pages = input.Pages.Skip(1).ToArray() });
        Reject(input with { Pages = input.Pages.Append(input.Pages[0]).ToArray() });
        Reject(input with { Rows = Enumerable.Repeat(input.Rows[0], PageFlowLimits.MaximumLines + 1).ToArray() });
        foreach (var pair in expected.Pairs)
        {
            foreach (var row in new[] { pair.A, pair.B })
            {
                RejectPair(input with { Rows = input.Rows.Where(r => r != row).ToArray() }, pair);
                RejectPair(input with { Rows = input.Rows.Append(row).ToArray() }, pair);
                foreach (var replacement in new[] { row with { Text = "NON NUMERIC REPLACEMENT" }, row with { Top = row.Top + 1 },
                    row with { Left = row.Left + 10 }, row with { Page = row.Page + 1 }, row with { Height = row.Height + 1 } })
                    RejectPair(input with { Rows = input.Rows.Select(r => r == row ? replacement : r).ToArray() }, pair);
            }
            RejectPair(input with { Rows = input.Rows.Select(r => r.Side == PageSpace.B && r.Page == pair.B.Page && Math.Abs(r.Index - pair.B.Index) is > 0 and <= 2
                ? r with { Text = "CHANGED SUPPORT " + r.Index } : r).ToArray() }, pair);
        }
        return new(permutations, rejections, retained);
        void Reject(Input altered) { Require(Find(altered).Pairs.Count == 0, "文書条件"); rejections++; }
        void RejectPair(Input altered, Pair pair)
        {
            var actual = Find(altered);
            Require(!actual.Pairs.Any(p => p.A.Text == pair.A.Text || p.B.Text == pair.B.Text), "対応証拠の欠落: " + JsonSerializer.Serialize(pair));
            retained += actual.Pairs.Count; rejections++;
        }
        static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException("数値対応の監査失敗: " + reason); }
    }
}
