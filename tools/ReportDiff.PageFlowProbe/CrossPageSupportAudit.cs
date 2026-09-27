using System.Text.Json;
using ReportDiff.Core;
using static ReportDiff.Core.PageFlowInference;

internal static class CrossPageSupportAudit
{
    internal static object Run(Layout[] layouts, RowOptions options, CrossPageSupportEvidence.Result expected)
    {
        var permutations = 0; var rejections = new Dictionary<string, int>();
        if (expected.Evidence is null) return new { permutations, rejections };
        var proof = expected.Evidence;
        var changedProofs = new[] {
            proof with { Cause = new(proof.Cause.Page, proof.Cause.Top + 1, proof.Cause.Height) },
            proof with { Cause = new(new(proof.Cause.Page.Side, 2), proof.Cause.Top, proof.Cause.Height) },
            proof with { Candidate = proof.Candidate with { Source = null } },
            proof with { Candidate = proof.Candidate with { Target = new(proof.Candidate.Target!.Page, proof.Candidate.Target.Top + 1, proof.Candidate.Target.Height) } },
            proof with { Candidate = proof.Candidate with { Support = 2 } },
            proof with { Candidate = proof.Candidate with { Text = proof.Candidate.Text.Take(1).ToArray() } },
            proof with { SourceSupport = proof.SourceSupport.Skip(1).ToArray() },
            proof with { TargetSupport = proof.TargetSupport.Reverse().ToArray() },
            proof with { TargetSupport = proof.TargetSupport.Append(proof.TargetSupport[0]).ToArray() },
            proof with { Crossing = proof.Crossing.Take(1).ToArray() },
            proof with { Counterparts = proof.SourceSupport.Take(2).ToArray() },
            proof with { Causes = proof.Causes.Reverse().ToArray() },
            proof with { BeforeSupport = proof.BeforeSupport + 1 },
            proof with { BetweenSupport = proof.BetweenSupport + 1 }
        };
        foreach (var changed in changedProofs)
        {
            if (CrossPageSupportEvidence.Matches(layouts, options, changed)) throw new InvalidOperationException("差し替えた証拠を採用");
            rejections["proof_fields"] = rejections.GetValueOrDefault("proof_fields") + 1;
        }
        for (var seed = 0; seed < 5; seed++)
        {
            var rng = new Random(seed); var actual = CrossPageSupportEvidence.Find(layouts.OrderBy(_ => rng.Next()).ToArray(), options, 300);
            if (JsonSerializer.Serialize(actual, AggregationProbe.Json) != JsonSerializer.Serialize(expected, AggregationProbe.Json)) throw new InvalidOperationException("列挙順で証拠が変化");
            permutations++;
        }
        Reject("selection", layouts, selected: true);
        foreach (var l in layouts)
        {
            Reject("missing_page", layouts.Where(x => x != l).ToArray());
            Reject("duplicate_page", layouts.Append(l).ToArray());
            for (var i = 0; i < l.Body.Count; i++)
            {
                var index = i; var row = l.Body[i];
                foreach (var kind in new[] { "text", "left", "baseline", "cut", "duplicate" })
                {
                    var changed = kind switch
                    {
                        "text" => row with { Text = "UNMATCHED " + row.Text },
                        "left" => row with { Bounds = row.Bounds with { Left = row.Bounds.Left + 1 } },
                        "baseline" => row with { Baseline = row.Baseline + 1 },
                        "cut" => row with { Bounds = row.Bounds with { Top = l.BodyStart - 1 } },
                        _ => row with { Text = l.Body[(i + 1) % l.Body.Count].Text }
                    };
                    // 原因行の本文・左端・ベースラインは移動の支持ではない。
                    if (expected.Evidence.Causes.Contains(row.Text) && kind is "text" or "left" or "baseline") continue;
                    Reject(kind, layouts.Select(x => x == l ? l with { Body = l.Body.Select((r, j) => j == index ? changed : r).ToArray() } : x).ToArray());
                }
            }
        }
        return new { permutations, rejections };
        void Reject(string kind, Layout[] changed, bool selected = false)
        {
            if (CrossPageSupportEvidence.Find(changed, options, 300, selected).Evidence is not null)
                throw new InvalidOperationException("破損した証拠を採用: " + kind);
            rejections[kind] = rejections.GetValueOrDefault(kind) + 1;
        }
    }
}
