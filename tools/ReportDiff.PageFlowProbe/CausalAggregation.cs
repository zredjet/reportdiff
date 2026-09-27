using static CandidateInference;

// 独立検証用。文字の順序・実構造ID・ページ別の収支をすべて確認した単一原因だけを扱う。
internal static class CausalAggregation
{
    internal sealed record Row(string Side, int Page, int Start, int Length, string Text);
    internal sealed record Reference(int Page, int StructuralChangeId);
    internal sealed record Structure(Reference Reference, string Kind, Band? A, Band? B, int? Dy, bool Excluded);
    internal sealed record Page(int Number, bool Paired, int Clusters, int DifferenceCount, bool Complete,
        bool UnpairedCovered, IReadOnlyList<Structure> Structures);
    internal sealed record Input(bool GateReady, bool SelectionLimited, IReadOnlyList<Row> Rows,
        IReadOnlyList<Proposal> Links, IReadOnlyList<Page> Pages);
    internal sealed record Balance(int Page, int RowsA, int RowsB, int CauseDelta, int Incoming, int Outgoing);
    internal sealed record Group(Reference Cause, IReadOnlyList<Reference> Structures, IReadOnlyList<Band> AuxiliaryBands,
        IReadOnlyList<Proposal> Links, IReadOnlyList<Balance> Balance);
    internal sealed record Decision(string Status, string? Reason, int DifferenceCount, int? AggregatedDifferenceCount,
        bool DifferenceCountComplete, bool? AggregatedDifferenceCountComplete, IReadOnlyList<Group> Groups);

    internal static Decision Evaluate(Input input, bool enabled = true)
    {
        var total = input.Pages.Sum(p => p.DifferenceCount);
        var complete = !input.SelectionLimited && input.Pages.All(p => p.Complete);
        if (!enabled) return new("disabled", null, total, null, complete, null, []);
        if (!input.GateReady) return Skip("document_gate_not_ready");
        if (input.SelectionLimited) return Skip("selection_limited");
        if (input.Pages.Any(p => !p.Paired && !p.UnpairedCovered)) return Skip("unpaired_residual_not_proven");
        if (input.Pages.Select(p => p.Number).Distinct().Count() != input.Pages.Count
            || !input.Pages.Select(p => p.Number).Order().SequenceEqual(Enumerable.Range(1, input.Pages.Count)))
            return Skip("incomplete_page_sequence");
        var structures = input.Pages.SelectMany(p => p.Structures).ToArray();
        if (structures.Select(s => s.Reference).Distinct().Count() != structures.Length) return Skip("duplicate_structure_reference");
        if (input.Pages.Any(p => p.DifferenceCount != p.Clusters + p.Structures.Count(s => !s.Excluded)))
            return Skip("inconsistent_unaggregated_count");
        if (structures.Any(s => s.Excluded)) return Skip("excluded_structure_not_proven");
        var a = Ordered("a"); var b = Ordered("b");
        if (a.Length == 0 || b.Length == 0 || a.Select(r => r.Text).Distinct().Count() != a.Length
            || b.Select(r => r.Text).Distinct().Count() != b.Length) return Skip("ambiguous_body_text");
        var byA = a.ToDictionary(r => r.Text); var byB = b.ToDictionary(r => r.Text);
        var extraA = a.Where(r => !byB.ContainsKey(r.Text)).ToArray();
        var extraB = b.Where(r => !byA.ContainsKey(r.Text)).ToArray();
        if ((extraA.Length == 0) == (extraB.Length == 0)) return Skip("multiple_or_missing_causes");
        var sign = extraB.Length > 0 ? 1 : -1;
        var extra = sign > 0 ? extraB : extraA;
        if (extra.Select(r => r.Page).Distinct().Count() != 1
            || extra.Zip(extra.Skip(1), (x, y) => x.Start + x.Length == y.Start).Any(v => !v))
            return Skip("multiple_cause_ranges");
        var commonA = a.Where(r => byB.ContainsKey(r.Text)).ToArray();
        var commonB = b.Where(r => byA.ContainsKey(r.Text)).ToArray();
        if (!commonA.Select(r => r.Text).SequenceEqual(commonB.Select(r => r.Text))) return Skip("reordered_body_text");
        var causeBand = new Band(extra[0].Side, extra[0].Page, extra[0].Start, extra.Sum(r => r.Length));
        var causes = structures.Where(s => s.Kind == (sign > 0 ? "inserted" : "deleted")
            && (sign > 0 ? s.B : s.A) == causeBand).ToArray();
        if (causes.Length != 1) return Skip("cause_structure_not_unique");
        var links = input.Links.OrderBy(l => l.Source.Page).ThenBy(l => l.Source.Start).ToArray();
        if (links.Length == 0 || links.Any(l => l.Status != "band_verified"
            || l.Source.Side != (sign > 0 ? "a" : "b") || l.Target.Side != (sign > 0 ? "b" : "a")
            || l.Target.Page != l.Source.Page + 1 || l.Source.Length != l.Target.Length))
            return Skip("invalid_flow_direction");
        if (links.SelectMany(l => new[] { l.Source, l.Target }).Distinct().Count() != links.Length * 2)
            return Skip("duplicate_flow_endpoint");
        var crossed = commonA.Where(r => r.Page != byB[r.Text].Page).ToArray();
        var balance = new List<Balance>();
        foreach (var link in links)
        {
            var source = Within(link.Source); var target = Within(link.Target);
            if (!Tiles(link.Source, source) || !Tiles(link.Target, target)
                || !source.Select(r => r.Text).SequenceEqual(target.Select(r => r.Text))
                || !source.Select(r => r.Text).SequenceEqual(link.Text)
                || source.Length != extra.Length || link.Source.Length != causeBand.Length
                || source[^1] != Ordered(link.Source.Side).Last(r => r.Page == link.Source.Page)
                || target[0] != Ordered(link.Target.Side).First(r => r.Page == link.Target.Page))
                return Skip("flow_band_balance_not_proven");
        }
        foreach (var row in crossed)
        {
            var other = byB[row.Text];
            if (links.Count(l => Contains(sign > 0 ? l.Source : l.Target, row)
                && Contains(sign > 0 ? l.Target : l.Source, other)) != 1) return Skip("uncovered_cross_page_row");
        }
        if (links.Sum(l => Within(l.Source).Length) != crossed.Length) return Skip("unaccounted_flow_rows");
        foreach (var page in input.Pages.OrderBy(p => p.Number))
        {
            var na = a.Count(r => r.Page == page.Number); var nb = b.Count(r => r.Page == page.Number);
            var cause = causeBand.Page == page.Number ? sign * extra.Length : 0;
            var incoming = sign * links.Where(l => l.Target.Page == page.Number).Sum(l => Within(l.Target).Length);
            var outgoing = sign * links.Where(l => l.Source.Page == page.Number).Sum(l => Within(l.Source).Length);
            if (nb - na != cause + incoming - outgoing) return Skip("page_flow_balance_failed");
            balance.Add(new(page.Number, na, nb, cause, incoming, outgoing));
        }
        var endpointBands = links.SelectMany(l => new[] { l.Source, l.Target }).ToArray();
        var auxiliary = new List<Band>();
        foreach (var band in endpointBands)
        {
            var page = input.Pages.SingleOrDefault(p => p.Number == band.Page);
            if (page is null) return Skip("missing_endpoint_page");
            if (!page.Paired) { auxiliary.Add(band); continue; }
            if (structures.Count(s => s.Reference.Page == band.Page
                && s.Kind == (band.Side == "a" ? "deleted" : "inserted")
                && (band.Side == "a" ? s.A : s.B) == band) != 1) return Skip("endpoint_structure_not_unique");
        }
        var moved = new HashSet<string>();
        foreach (var s in structures)
        {
            if (s == causes[0]) continue;
            if (s.Kind is "inserted" or "deleted")
            {
                if (!endpointBands.Contains(s.Kind == "inserted" ? s.B : s.A)) return Skip("unrelated_structural_change");
                continue;
            }
            if (s.Kind != "block_moved" || s.A is null || s.B is null || s.A.Page != s.B.Page)
                return Skip("unsupported_structure");
            var rowsA = Within(s.A); var rowsB = Within(s.B);
            if (!Tiles(s.A, rowsA) || !Tiles(s.B, rowsB) || !rowsA.Select(r => r.Text).SequenceEqual(rowsB.Select(r => r.Text))
                || s.Dy != sign * causeBand.Length || s.B.Start - s.A.Start != s.Dy
                || rowsA.Any(r => !moved.Add(r.Text))) return Skip("movement_membership_not_proven");
        }
        var expectedMoved = commonA.Where(r => r.Page == byB[r.Text].Page && r.Start != byB[r.Text].Start).Select(r => r.Text);
        if (!moved.SetEquals(expectedMoved)) return Skip("uncovered_movement");
        var refs = structures.Select(s => s.Reference).OrderBy(r => r.Page).ThenBy(r => r.StructuralChangeId).ToArray();
        if (refs.Length < 2) return Skip("insufficient_group_members");
        return new("grouped", null, total, total - (refs.Length - 1), complete, complete,
            [new(causes[0].Reference, refs, auxiliary, links, balance)]);

        Decision Skip(string reason) => new("skipped", reason, total, total, complete, complete, []);
        Row[] Ordered(string side) => input.Rows.Where(r => r.Side == side).OrderBy(r => r.Page).ThenBy(r => r.Start).ToArray();
        Row[] Within(Band band) => input.Rows.Where(r => Contains(band, r)).OrderBy(r => r.Start).ToArray();
    }
    private static bool Contains(Band b, Row r) => b.Side == r.Side && b.Page == r.Page
        && r.Start >= b.Start && r.Start + r.Length <= b.Start + b.Length;
    private static bool Tiles(Band b, Row[] rows) => rows.Length > 0 && rows[0].Start == b.Start
        && rows[^1].Start + rows[^1].Length == b.Start + b.Length
        && rows.Zip(rows.Skip(1), (x, y) => x.Start + x.Length == y.Start).All(v => v);
}
