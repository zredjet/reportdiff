using static ReportDiff.Core.PageFlowInference;
using Band = ReportDiff.Core.PageFlowBand;

namespace ReportDiff.Core;

/// <summary>文字の順序・実構造ID・ページ別収支を確認した、各送り成分の単一の連続した挿入/削除原因を集約する。</summary>
public static class PageFlowAggregation
{
    public sealed record Row(PageSpace Side, int Page, int Start, int Length, string Text)
    {
        internal PageFlowRowIdentity? NumericIdentity { get; init; }
        internal PageFlowRowIdentity Identity => NumericIdentity ?? new(Text);
    }
    public sealed record Reference(int Page, int StructuralChangeId);
    public sealed record Structure(Reference Reference, string Kind, Band? A, Band? B, int? Dy, bool Excluded);
    public sealed record Page(int Number, bool Paired, int Clusters, int DifferenceCount, bool Complete,
        bool UnpairedCovered, IReadOnlyList<Structure> Structures);
    public sealed record Input(bool GateReady, bool SelectionLimited, IReadOnlyList<Row> Rows,
        IReadOnlyList<Proposal> Links, IReadOnlyList<Page> Pages);
    public sealed record Balance(int Page, int RowsA, int RowsB, int CauseDelta, int Incoming, int Outgoing);
    public sealed record Group(Reference Cause, IReadOnlyList<Reference> Structures, IReadOnlyList<Band> AuxiliaryBands,
        IReadOnlyList<Proposal> Links, IReadOnlyList<Balance> Balance);
    public sealed record Decision(string Status, string? Reason, int DifferenceCount, int? AggregatedDifferenceCount,
        bool DifferenceCountComplete, bool? AggregatedDifferenceCountComplete, IReadOnlyList<Group> Groups)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<SharedComponent>? SharedComponents { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public long SharedDescriptorBytes { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public long TerminalDescriptorBytes { get; init; }
    }
    public sealed record SharedCause(Reference Reference, Band Band, int Rows, int Delta);
    public sealed record SharedMovement(Reference Structure, IReadOnlyList<Reference> Causes, int Dy);
    public sealed record SharedLink(Proposal Link, IReadOnlyList<Reference> Structures, IReadOnlyList<Reference> Causes, int Rows,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<Band>? AuxiliaryBands = null);
    public sealed record SharedComponent(IReadOnlyList<int> Pages, IReadOnlyList<SharedCause> Causes,
        IReadOnlyList<Reference> Structures, IReadOnlyList<SharedMovement> Movements, IReadOnlyList<SharedLink> Links,
        IReadOnlyList<Balance> Balance,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<Band>? AuxiliaryBands = null);

    public static Decision Evaluate(Input input, bool enabled = true, long descriptorBytesRemaining = PageFlowLimits.MaximumDescriptorBytes)
    {
        var single = EvaluateSingle(input, enabled);
        if (!enabled || single.Status == "grouped" || !input.GateReady || input.SelectionLimited) return single;
        var independent = PageFlowComponents.Evaluate(input, single);
        return independent.Status == "grouped" ? independent : PageFlowSharedCauses.Evaluate(input, independent, descriptorBytesRemaining);
    }

    internal static Decision EvaluateSingle(Input input, bool enabled = true)
    {
        var total = input.Pages.Sum(p => p.DifferenceCount);
        var complete = !input.SelectionLimited && input.Pages.All(p => p.Paired && p.Complete);
        if (!enabled) return new("disabled", null, total, null, complete, null, []);
        if (!input.GateReady) return Skip("document_gate_not_ready");
        if (input.SelectionLimited) return Skip("selection_limited");
        if (input.Pages.Any(p => !p.Paired && !p.UnpairedCovered)) return Skip("unpaired_residual_not_proven");
        if (input.Pages.Select(p => p.Number).Distinct().Count() != input.Pages.Count
            || !input.Pages.Select(p => p.Number).Order().SequenceEqual(Enumerable.Range(1, input.Pages.Count)))
            return Skip("incomplete_page_sequence");
        if (input.Rows.Any(r => r.Side is not (PageSpace.A or PageSpace.B) || r.Page < 1
            || r.Start < 0 || r.Length <= 0 || (long)r.Start + r.Length > int.MaxValue
            || !input.Pages.Any(p => p.Number == r.Page))
            || input.Rows.GroupBy(r => (r.Side, r.Page)).Any(g => g.OrderBy(r => r.Start)
                .Zip(g.OrderBy(r => r.Start).Skip(1), (a, b) => (long)a.Start + a.Length > b.Start).Any(v => v)))
            return Skip("invalid_body_ranges");
        var structures = input.Pages.SelectMany(p => p.Structures).ToArray();
        if (input.Pages.Any(p => p.Clusters < 0 || p.DifferenceCount < 0 || p.Structures.Any(s =>
            s.Reference.Page != p.Number || s.Reference.StructuralChangeId < 1
            || s.A is { } sa && (sa.Page.Side != PageSpace.A || sa.Page.Page != p.Number)
            || s.B is { } sb && (sb.Page.Side != PageSpace.B || sb.Page.Page != p.Number))))
            return Skip("invalid_structure_reference");
        if (structures.Select(s => s.Reference).Distinct().Count() != structures.Length) return Skip("duplicate_structure_reference");
        if (input.Pages.Any(p => p.DifferenceCount != p.Clusters + p.Structures.Count(s => !s.Excluded)))
            return Skip("inconsistent_unaggregated_count");
        if (structures.Any(s => s.Excluded)) return Skip("excluded_structure_not_proven");
        if (input.Pages.Any(p => !p.Paired && (p.Structures.Count != 0 || p.Clusters != 0))
            || structures.Any(s => s.Kind switch
            {
                "inserted" => s.A is not null || s.B is null,
                "deleted" => s.A is null || s.B is not null,
                "block_moved" => s.A is null || s.B is null,
                _ => true
            })) return Skip("unsupported_structure");
        var a = Ordered(PageSpace.A); var b = Ordered(PageSpace.B);
        if (a.Length == 0 || b.Length == 0 || a.Select(r => r.Identity).Distinct().Count() != a.Length
            || b.Select(r => r.Identity).Distinct().Count() != b.Length) return Skip("ambiguous_body_text");
        var byA = a.ToDictionary(r => r.Identity); var byB = b.ToDictionary(r => r.Identity);
        var extraA = a.Where(r => !byB.ContainsKey(r.Identity)).ToArray();
        var extraB = b.Where(r => !byA.ContainsKey(r.Identity)).ToArray();
        if ((extraA.Length == 0) == (extraB.Length == 0)) return Skip("multiple_or_missing_causes");
        var sign = extraB.Length > 0 ? 1 : -1;
        var extra = sign > 0 ? extraB : extraA;
        if (extra.Select(r => r.Page).Distinct().Count() != 1
            || extra.Zip(extra.Skip(1), (x, y) => x.Start + x.Length == y.Start).Any(v => !v))
            return Skip("multiple_cause_ranges");
        var commonA = a.Where(r => byB.ContainsKey(r.Identity)).ToArray();
        var commonB = b.Where(r => byA.ContainsKey(r.Identity)).ToArray();
        if (!commonA.Select(r => r.Identity).SequenceEqual(commonB.Select(r => r.Identity))) return Skip("reordered_body_text");
        var causeBand = new Band(new(extra[0].Side, extra[0].Page), extra[0].Start, extra.Sum(r => r.Length));
        var causes = structures.Where(s => s.Kind == (sign > 0 ? "inserted" : "deleted")
            && (sign > 0 ? s.B : s.A) == causeBand).ToArray();
        if (causes.Length != 1) return Skip("cause_structure_not_unique");
        if (input.Links.Any(l => l.Source is null || l.Target is null)) return Skip("invalid_flow_direction");
        // 推定を見送ったnull端点を、送り成立の根拠へ混入させない。
        var links = input.Links.OrderBy(l => l.Source!.Page.Page).ThenBy(l => l.Source!.Top).ToArray();
        if (links.Length == 0 || links.Any(l => l.Status != "band_verified" || l.Reason is not null
            || l.Source!.Page.Side != (sign > 0 ? PageSpace.A : PageSpace.B) || l.Target!.Page.Side != (sign > 0 ? PageSpace.B : PageSpace.A)
            || l.Target!.Page.Page != l.Source!.Page.Page + 1 || l.Source!.Height != l.Target!.Height))
            return Skip("invalid_flow_direction");
        if (links.SelectMany(l => new[] { l.Source!, l.Target! }).Distinct().Count() != links.Length * 2)
            return Skip("duplicate_flow_endpoint");
        var crossed = commonA.Where(r => r.Page != byB[r.Identity].Page).ToArray();
        var balance = new List<Balance>();
        foreach (var link in links)
        {
            var source = Within(link.Source!); var target = Within(link.Target!);
            if (!Tiles(link.Source!, source) || !Tiles(link.Target!, target)
                || !source.Select(r => r.Text).SequenceEqual(target.Select(r => r.Text))
                || !source.Select(r => r.Text).SequenceEqual(link.Text)
                || source.Length != extra.Length || link.Source!.Height != causeBand.Height
                || source[^1] != Ordered(link.Source!.Page.Side).Last(r => r.Page == link.Source!.Page.Page)
                || target[0] != Ordered(link.Target!.Page.Side).First(r => r.Page == link.Target!.Page.Page))
                return Skip("flow_band_balance_not_proven");
        }
        foreach (var row in crossed)
        {
            var other = byB[row.Identity];
            if (links.Count(l => Contains(sign > 0 ? l.Source! : l.Target!, row)
                && Contains(sign > 0 ? l.Target! : l.Source!, other)) != 1) return Skip("uncovered_cross_page_row");
        }
        if (links.Sum(l => Within(l.Source!).Length) != crossed.Length) return Skip("unaccounted_flow_rows");
        foreach (var page in input.Pages.OrderBy(p => p.Number))
        {
            var na = a.Count(r => r.Page == page.Number); var nb = b.Count(r => r.Page == page.Number);
            var cause = causeBand.Page.Page == page.Number ? sign * extra.Length : 0;
            var incoming = sign * links.Where(l => l.Target!.Page.Page == page.Number).Sum(l => Within(l.Target!).Length);
            var outgoing = sign * links.Where(l => l.Source!.Page.Page == page.Number).Sum(l => Within(l.Source!).Length);
            if (nb - na != cause + incoming - outgoing) return Skip("page_flow_balance_failed");
            balance.Add(new(page.Number, na, nb, cause, incoming, outgoing));
        }
        var endpointBands = links.SelectMany(l => new[] { l.Source!, l.Target! }).ToArray();
        var auxiliary = new List<Band>();
        foreach (var band in endpointBands)
        {
            var page = input.Pages.SingleOrDefault(p => p.Number == band.Page.Page);
            if (page is null) return Skip("missing_endpoint_page");
            if (!page.Paired) { auxiliary.Add(band); continue; }
            if (structures.Count(s => s.Reference.Page == band.Page.Page
                && s.Kind == (band.Page.Side == PageSpace.A ? "deleted" : "inserted")
                && (band.Page.Side == PageSpace.A ? s.A : s.B) == band) != 1) return Skip("endpoint_structure_not_unique");
        }
        var moved = new HashSet<PageFlowRowIdentity>();
        foreach (var s in structures)
        {
            if (s == causes[0]) continue;
            if (s.Kind is "inserted" or "deleted")
            {
                if (!endpointBands.Contains(s.Kind == "inserted" ? s.B : s.A)) return Skip("unrelated_structural_change");
                continue;
            }
            if (s.Kind != "block_moved" || s.A is null || s.B is null || s.A.Page.Page != s.B.Page.Page)
                return Skip("unsupported_structure");
            var rowsA = Within(s.A); var rowsB = Within(s.B);
            if (!Tiles(s.A, rowsA) || !Tiles(s.B, rowsB) || !rowsA.Select(r => r.Identity).SequenceEqual(rowsB.Select(r => r.Identity))
                || s.Dy != sign * causeBand.Height || s.B.Top - s.A.Top != s.Dy
                || rowsA.Any(r => !moved.Add(r.Identity))) return Skip("movement_membership_not_proven");
        }
        var expectedMoved = commonA.Where(r => r.Page == byB[r.Identity].Page && r.Start != byB[r.Identity].Start).Select(r => r.Identity);
        if (!moved.SetEquals(expectedMoved)) return Skip("uncovered_movement");
        var refs = structures.Select(s => s.Reference).OrderBy(r => r.Page).ThenBy(r => r.StructuralChangeId).ToArray();
        if (refs.Length < 2) return Skip("insufficient_group_members");
        return new("grouped", null, total, total - (refs.Length - 1), complete, complete,
            Array.AsReadOnly(new[] { new Group(causes[0].Reference, Array.AsReadOnly(refs), Array.AsReadOnly(auxiliary.ToArray()), Array.AsReadOnly(links), Array.AsReadOnly(balance.ToArray())) }));

        Decision Skip(string reason) => new("skipped", reason, total, total, complete, complete, []);
        Row[] Ordered(PageSpace side) => input.Rows.Where(r => r.Side == side).OrderBy(r => r.Page).ThenBy(r => r.Start).ToArray();
        Row[] Within(Band band) => input.Rows.Where(r => Contains(band, r)).OrderBy(r => r.Start).ToArray();
    }
    private static bool Contains(Band b, Row r) => b.Page.Side == r.Side && b.Page.Page == r.Page
        && r.Start >= b.Top && r.Start + r.Length <= b.Top + b.Height;
    private static bool Tiles(Band b, Row[] rows) => rows.Length > 0 && rows[0].Start == b.Top
        && rows[^1].Start + rows[^1].Length == b.Top + b.Height
        && rows.Zip(rows.Skip(1), (x, y) => x.Start + x.Length == y.Start).All(v => v);
}
