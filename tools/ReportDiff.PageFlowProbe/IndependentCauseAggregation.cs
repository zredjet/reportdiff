using ReportDiff.Core;
using static ReportDiff.Core.PageFlowAggregation;

/// <summary>独立検証専用。ページ群が交わらない単一原因の送りだけを分ける。製品からは呼ばない。</summary>
internal static class IndependentCauseAggregation
{
    internal static Decision Evaluate(Input input, bool enabled = true)
    {
        var legacy = PageFlowAggregation.Evaluate(input, enabled);
        if (!enabled || legacy.Status == "grouped" || !input.GateReady || input.SelectionLimited) return legacy;
        if (input.Pages.Count > PageFlowLimits.MaximumSelectedPages || input.Rows.Count > PageFlowLimits.MaximumLines
            || input.Links.Count > PageFlowLimits.MaximumCandidates) return Skip("component_resource_limit");
        var pages = input.Pages.OrderBy(p => p.Number).ToArray();
        if (!pages.Select(p => p.Number).SequenceEqual(Enumerable.Range(1, pages.Length))) return Skip("incomplete_page_sequence");
        if (input.Rows.Any(r => r.Side is not (PageSpace.A or PageSpace.B) || r.Page < 1 || r.Page > pages.Length
            || r.Start < 0 || r.Length <= 0 || (long)r.Start + r.Length > int.MaxValue)) return Skip("invalid_body_ranges");
        if (input.Rows.GroupBy(r => (r.Side, r.Page)).Any(g => g.OrderBy(r => r.Start)
            .Zip(g.OrderBy(r => r.Start).Skip(1), (x, y) => (long)x.Start + x.Length > y.Start).Any(v => v)))
            return Skip("invalid_body_ranges");
        var a = Rows(PageSpace.A); var b = Rows(PageSpace.B);
        if (a.Select(r => r.Text).Distinct().Count() != a.Length || b.Select(r => r.Text).Distinct().Count() != b.Length)
            return Skip("ambiguous_document_text");
        var byA = a.ToDictionary(r => r.Text); var byB = b.ToDictionary(r => r.Text);
        if (!a.Where(r => byB.ContainsKey(r.Text)).Select(r => r.Text).SequenceEqual(b.Where(r => byA.ContainsKey(r.Text)).Select(r => r.Text)))
            return Skip("reordered_document_text");
        if (input.Links.Any(l => l.Status != "band_verified" || l.Reason is not null || l.Source is null || l.Target is null
            || l.Source.Page.Page < 1 || l.Target.Page.Page > pages.Length || l.Target.Page.Page != l.Source.Page.Page + 1))
            return Skip("invalid_component_link");
        var boundaries = input.Links.Select(l => l.Source!.Page.Page).ToHashSet();
        var component = new int[pages.Length + 1];
        for (var page = 2; page <= pages.Length; page++) component[page] = component[page - 1] + (boundaries.Contains(page - 1) ? 0 : 1);
        if (a.Where(r => byB.ContainsKey(r.Text)).Any(r => component[r.Page] != component[byB[r.Text].Page]))
            return Skip("crosses_unlinked_boundary");
        var groups = new List<Group>();
        foreach (var block in pages.GroupBy(p => component[p.Number]))
        {
            var numbers = block.Select(p => p.Number).ToHashSet(); var offset = numbers.Min() - 1;
            var links = input.Links.Where(l => numbers.Contains(l.Source!.Page.Page)).ToArray();
            var rows = input.Rows.Where(r => numbers.Contains(r.Page)).ToArray();
            if (links.Length == 0)
            {
                if (block.Any(p => !p.Paired || p.Structures.Count != 0 || p.Clusters < 0 || p.DifferenceCount != p.Clusters)
                    || !rows.Where(r => r.Side == PageSpace.A).OrderBy(r => r.Page).ThenBy(r => r.Start).Select(r => (r.Page, r.Start, r.Length, r.Text))
                        .SequenceEqual(rows.Where(r => r.Side == PageSpace.B).OrderBy(r => r.Page).ThenBy(r => r.Start).Select(r => (r.Page, r.Start, r.Length, r.Text))))
                    return Skip("nonneutral_unlinked_page");
                continue;
            }
            var local = new Input(true, false, rows.Select(r => r with { Page = r.Page - offset }).ToArray(),
                links.Select(l => Shift(l, -offset)).ToArray(), block.Select(p => p with { Number = p.Number - offset,
                    Structures = p.Structures.Select(s => s with { Reference = Shift(s.Reference, -offset), A = Shift(s.A, -offset), B = Shift(s.B, -offset) }).ToArray() }).ToArray());
            var decision = PageFlowAggregation.Evaluate(local);
            if (decision.Status != "grouped") return Skip("component_" + decision.Reason);
            groups.AddRange(decision.Groups.Select(g => new Group(Shift(g.Cause, offset), g.Structures.Select(r => Shift(r, offset)).ToArray(),
                g.AuxiliaryBands.Select(band => Shift(band, offset)!).ToArray(), g.Links.Select(l => Shift(l, offset)).ToArray(),
                g.Balance.Select(v => v with { Page = v.Page + offset }).ToArray())));
        }
        if (groups.Count < 2) return legacy;
        var actual = pages.SelectMany(p => p.Structures).Select(s => s.Reference).ToArray();
        var assigned = groups.SelectMany(g => g.Structures).ToArray();
        if (actual.Distinct().Count() != actual.Length || assigned.Distinct().Count() != assigned.Length
            || !actual.ToHashSet().SetEquals(assigned)) return Skip("component_membership_not_unique");
        return new("grouped", null, legacy.DifferenceCount, legacy.DifferenceCount - assigned.Length + groups.Count,
            legacy.DifferenceCountComplete, legacy.DifferenceCountComplete, groups.AsReadOnly());

        Decision Skip(string reason) => legacy with { Status = "skipped", Reason = reason, AggregatedDifferenceCount = legacy.DifferenceCount, Groups = [] };
        Row[] Rows(PageSpace side) => input.Rows.Where(r => r.Side == side).OrderBy(r => r.Page).ThenBy(r => r.Start).ToArray();
    }

    private static Reference Shift(Reference r, int offset) => r with { Page = r.Page + offset };
    private static PageFlowBand? Shift(PageFlowBand? b, int offset) => b is null ? null : new(new(b.Page.Side, b.Page.Page + offset), b.Top, b.Height);
    private static PageFlowInference.Proposal Shift(PageFlowInference.Proposal p, int offset) => p with { Source = Shift(p.Source, offset), Target = Shift(p.Target, offset) };
}
