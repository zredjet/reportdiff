using System.Text.Json;
using ReportDiff.Core;
using static ReportDiff.Core.PageFlowAggregation;

internal static class MultipleBoundaryAudit
{
    internal sealed record Result(int Permutations, int Rejections, int RealFlowRejections);
    internal static Result Run(PageFlowPlan plan, IReadOnlyList<Row> rows, IReadOnlyList<IndependentBoundary.Proof> proofs)
    {
        var permutations = 0; var rejections = 0; var realFlow = 0;
        foreach (var proof in proofs)
        {
            var link = plan.Inference.Proposals[proof.CandidateIndex];
            for (var seed = 0; seed < 3; seed++)
            {
                var random = new Random(seed);
                var shuffled = rows.OrderBy(_ => random.Next()).ToArray();
                Require(IndependentBoundary.Find(shuffled, [link]).Count == 1, "列挙順"); permutations++;
            }
            Require(IndependentBoundary.Find(plan, true).Count == 0, "選択範囲"); rejections++;
            foreach (var row in proof.SourceRows.Concat(proof.TargetRows).Concat(proof.SourceCounterparts).Concat(proof.TargetCounterparts).Distinct())
            {
                Reject(rows.Where(r => r != row).ToArray(), link);
                Reject(rows.Append(row).ToArray(), link);
                Reject(rows.Select(r => r == row ? r with { Page = row.Page == proof.Boundary ? proof.Boundary + 1 : proof.Boundary } : r).ToArray(), link);
                Reject(rows.Select(r => r == row ? r with { Text = "CHANGED UNMATCHED VALUE" } : r).ToArray(), link);
            }
            foreach (var altered in new[] { link with { Reason = "nonidentical_band_not_proven" }, link with { Reason = "ambiguous_displacement" },
                link with { Source = null }, link with { Source = new(link.Source!.Page, link.Source.Top + 1, link.Source.Height) } }) Reject(rows, altered);
        }
        foreach (var link in plan.Links.Where(l => l.Status == "band_verified"))
        {
            Require(IndependentBoundary.Find(rows, [link with { Status = "skipped", Reason = "text_mismatch" }]).Count == 0, "実送りを同一ページ対応済みと誤認"); realFlow++;
        }
        return new(permutations, rejections, realFlow);
        void Reject(IReadOnlyList<Row> altered, PageFlowInference.Proposal proposal)
        { Require(IndependentBoundary.Find(altered, [proposal]).Count == 0, "境界証明の破損: " + JsonSerializer.Serialize(proposal)); rejections++; }
        static void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException("非送り境界の監査失敗: " + label); }
    }
}
