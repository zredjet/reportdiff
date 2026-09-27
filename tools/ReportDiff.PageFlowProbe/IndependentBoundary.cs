using System.Reflection;
using ReportDiff.Core;
using static ReportDiff.Core.PageFlowAggregation;

/// <summary>独立ツール限定。送り候補が両端とも同一ページ対応済みで、境界を誰も跨がないことを証明する。</summary>
internal static class IndependentBoundary
{
    internal sealed record Proof(int CandidateIndex, int Boundary, IReadOnlyList<Row> SourceRows, IReadOnlyList<Row> SourceCounterparts,
        IReadOnlyList<Row> TargetRows, IReadOnlyList<Row> TargetCounterparts);
    internal static IReadOnlyList<Proof> Find(PageFlowPlan plan, bool selectionLimited = false)
    {
        if (selectionLimited || plan.Inference.Layouts.Status != "prepared") return [];
        var layouts = plan.Inference.Layouts.A.Concat(plan.Inference.Layouts.B).ToArray();
        var pagesA = plan.Inference.Layouts.A.Select(l => l.Page.Key.Page).Order().ToArray();
        var pagesB = plan.Inference.Layouts.B.Select(l => l.Page.Key.Page).Order().ToArray();
        if (!pagesA.SequenceEqual(pagesB) || !pagesA.SequenceEqual(Enumerable.Range(1, pagesA.Length)) || layouts.Any(l => !l.Regular)) return [];
        var rows = layouts.SelectMany(l => l.Body.Select((r, i) => new Row(l.Page.Key.Side, l.Page.Key.Page,
            l.BodyStart + i * l.Pitch, l.Pitch, r.Text))).ToArray();
        return Find(rows, plan.Inference.Proposals);
    }

    internal static IReadOnlyList<Proof> Find(IReadOnlyList<Row> rows, IReadOnlyList<PageFlowInference.Proposal> proposals)
    {
        var a = rows.Where(r => r.Side == PageSpace.A).OrderBy(r => r.Page).ThenBy(r => r.Start).ToArray();
        var b = rows.Where(r => r.Side == PageSpace.B).OrderBy(r => r.Page).ThenBy(r => r.Start).ToArray();
        if (a.Select(r => r.Text).Distinct().Count() != a.Length || b.Select(r => r.Text).Distinct().Count() != b.Length) return [];
        var byA = a.ToDictionary(r => r.Text); var byB = b.ToDictionary(r => r.Text);
        if (!a.Where(r => byB.ContainsKey(r.Text)).Select(r => r.Text).SequenceEqual(b.Where(r => byA.ContainsKey(r.Text)).Select(r => r.Text))) return [];
        var result = new List<Proof>();
        foreach (var (p, i) in proposals.Select((p, i) => (p, i)))
        {
            if (p.Status != "skipped" || p.Reason != "text_mismatch" || p.Source is not { } source || p.Target is not { } target
                || source.Page.Side == target.Page.Side || target.Page.Page != source.Page.Page + 1) continue;
            if (a.Where(r => byB.ContainsKey(r.Text)).Any(r => Math.Min(r.Page, byB[r.Text].Page) <= source.Page.Page
                && Math.Max(r.Page, byB[r.Text].Page) > source.Page.Page)) continue;
            var from = Counterparts(source); var to = Counterparts(target);
            if (from is not null && to is not null) result.Add(new(i, source.Page.Page, from.Value.Rows, from.Value.Counterparts, to.Value.Rows, to.Value.Counterparts));
        }
        return result.AsReadOnly();

        (Row[] Rows, Row[] Counterparts)? Counterparts(PageFlowBand band)
        {
            var contained = rows.Where(r => r.Side == band.Page.Side && r.Page == band.Page.Page && r.Start >= band.Top && r.Start + r.Length <= band.Bottom)
                .OrderBy(r => r.Start).ToArray();
            if (contained.Length == 0 || contained[0].Start != band.Top || contained[^1].Start + contained[^1].Length != band.Bottom
                || contained.Zip(contained.Skip(1), (x, y) => x.Start + x.Length == y.Start).Any(v => !v)) return null;
            var opposite = band.Page.Side == PageSpace.A ? byB : byA;
            if (contained.Any(r => !opposite.TryGetValue(r.Text, out var other) || other.Page != r.Page || other.Length != r.Length)) return null;
            return (contained, contained.Select(r => opposite[r.Text]).ToArray());
        }
    }

    internal static PageFlowPlan Recheck(PageFlowPlan original, PageFlowDocumentDescriptor document, IReadOnlyList<Proof> proofs)
    {
        if (proofs.Count == 0 || original.Decision.Ready) return original;
        var removed = proofs.Select(p => p.CandidateIndex).ToHashSet();
        var kept = Enumerable.Range(0, original.Links.Count).Where(i => !removed.Contains(i)).ToArray();
        var links = kept.Select(i => original.Links[i]).ToArray();
        var pageProofs = original.Pages.Select(p => new PageFlowPageProof(p.Number, p.Built?.Status == "built",
            p.Built?.Removed.Where(r => r.Proof == "verified_carry_range").Select(r => r.Band).ToArray() ?? [])).ToArray();
        var decision = PageFlowRangeGate.Evaluate(document, true, null,
            links.Select(l => new PageFlowCandidate(l.Source, l.Target, l.Status == "band_verified", l.Reason)).ToArray(), pageProofs);
        // 製品APIは変更しない。画像証明済みの既存C/Dをそのまま使い、文書ゲートだけ独立再評価する。
        var constructor = typeof(PageFlowPlan).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var parameters = original.Pages.ToDictionary(p => p.Number, _ => new ComparisonParameters());
        return (PageFlowPlan)constructor.Invoke([document, parameters,
            original.Inference with { Proposals = kept.Select(i => original.Inference.Proposals[i]).ToArray() },
            kept.Select(i => original.Verifications[i]).ToArray(), links, original.Pages.ToArray(), decision]);
    }
}
