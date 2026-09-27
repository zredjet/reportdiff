using ReportDiff.Core;
using static ReportDiff.Core.PageFlowInference;
using Line = SharedInferenceDiagnosis.Line;

/// <summary>製品未接続。末尾原因・全跨ぎ・次ページの別の支持行を結ぶ限定した証拠案。</summary>
internal static class CrossPageSupportEvidence
{
    internal sealed record Proof(PageFlowBand Cause, Proposal Candidate, IReadOnlyList<Line> SourceSupport,
        IReadOnlyList<Line> TargetSupport, IReadOnlyList<Line> Crossing, IReadOnlyList<Line> Counterparts,
        IReadOnlyList<string> Causes, int BeforeSupport, int BetweenSupport);
    internal sealed record Result(string Status, string? Reason, Proof? Evidence = null);

    internal static Line[] Rows(IReadOnlyList<Layout> layouts) => layouts.SelectMany(l => l.Body.Select((r, i) =>
        new Line(l.Page.Key, i, l.BodyStart + i * l.Pitch, l.Pitch, r.Text, r.Baseline, r.Bounds.Left))).ToArray();

    // 診断では同じ入力から証拠を再計算して全フィールドを照合する。製品用の参照結合・予算予約は別設計。
    internal static bool Matches(IReadOnlyList<Layout> layouts, RowOptions options, Proof proof)
    {
        var expected = Find(layouts, options, 300).Evidence;
        return expected is not null && proof.Cause == expected.Cause
            && proof.Candidate.Source == expected.Candidate.Source && proof.Candidate.Target == expected.Candidate.Target
            && proof.Candidate.Status == expected.Candidate.Status && proof.Candidate.Reason == expected.Candidate.Reason
            && proof.Candidate.Support == expected.Candidate.Support && proof.Candidate.Text.SequenceEqual(expected.Candidate.Text)
            && proof.SourceSupport.SequenceEqual(expected.SourceSupport) && proof.TargetSupport.SequenceEqual(expected.TargetSupport)
            && proof.Crossing.SequenceEqual(expected.Crossing) && proof.Counterparts.SequenceEqual(expected.Counterparts)
            && proof.Causes.SequenceEqual(expected.Causes) && proof.BeforeSupport == expected.BeforeSupport && proof.BetweenSupport == expected.BetweenSupport;
    }

    internal static Result Find(IReadOnlyList<Layout> layouts, RowOptions options, int dpi, bool selectionLimited = false)
    {
        options.Validated(dpi);
        if (selectionLimited) return Fail("selection_limited");
        if (layouts.Count != 4 || !layouts.Select(l => l.Page.Key).ToHashSet().SetEquals(
            new[] { new PageFlowPageKey(PageSpace.A, 1), new(PageSpace.B, 1), new(PageSpace.A, 2), new(PageSpace.B, 2) }))
            return Fail("two_paired_pages_required");
        if (layouts.Any(l => !l.Regular) || layouts.Select(l => (l.BodyStart, l.Pitch)).Distinct().Count() != 1)
            return Fail("irregular_layout");
        var rows = Rows(layouts); var a = Ordered(PageSpace.A); var b = Ordered(PageSpace.B);
        if (a.Select(r => r.Text).Distinct().Count() != a.Length || b.Select(r => r.Text).Distinct().Count() != b.Length)
            return Fail("nonunique_body");
        var onlyA = a.Where(r => b.All(x => x.Text != r.Text)).ToArray();
        var onlyB = b.Where(r => a.All(x => x.Text != r.Text)).ToArray();
        if (!((onlyA.Length == 0 && onlyB.Length == 2) || (onlyB.Length == 0 && onlyA.Length == 2)))
            return Fail("two_single_causes_required");
        var source = onlyA.Length == 0 ? a : b; var target = onlyA.Length == 0 ? b : a;
        var causes = onlyA.Length == 0 ? onlyB : onlyA;
        var lookup = target.ToDictionary(r => r.Text);
        if (!source.Select(r => r.Text).SequenceEqual(target.Where(r => source.Any(s => s.Text == r.Text)).Select(r => r.Text)))
            return Fail("reordered_common_rows");
        if (source.Any(r => Math.Abs((r.Baseline - r.Top) - (lookup[r.Text].Baseline - lookup[r.Text].Top)) > .5))
            return Fail("row_phase_mismatch");
        var first = causes[0]; var last = causes[1]; var pitch = layouts[0].Pitch;
        if (first.Page.Page != 1 || last.Page != first.Page || last.Index - first.Index < 2)
            return Fail("causes_not_separate_on_first_page");
        var edited = layouts.Single(l => l.Page.Key == last.Page);
        var original = layouts.Single(l => l.Page.Key.Page == 1 && l.Page.Key.Side != last.Page.Side);
        if (edited.Body.Count != original.Body.Count || last.Index != edited.Body.Count - 1)
            return Fail("cause_not_exactly_at_body_end");
        var before = first.Index; var between = last.Index - first.Index - 1;
        if (before < options.MinSupportBands || between < options.MinSupportBands)
            return Fail("local_cause_support");
        if (source.Where(r => r.Page.Page == 1 && lookup[r.Text].Page.Page == 1).Any(r =>
            Math.Round(lookup[r.Text].Baseline - r.Baseline) != (r.Index < first.Index ? 0 : pitch)
            || Math.Round(lookup[r.Text].Left - r.Left) != 0)) return Fail("local_displacement");
        var inferred = SharedInferenceDiagnosis.Infer(rows, layouts, options, dpi);
        if (inferred.Status != "inferred" || inferred.Proposals.Count != 1) return Fail(inferred.Reason ?? "one_crossing_required");
        var candidate = inferred.Proposals[0];
        var boundary = inferred.Boundaries.Single(x => x.Candidate is not null);
        if (candidate.Source!.Page != original.Page.Key || candidate.Target!.Page != new PageFlowPageKey(edited.Page.Key.Side, 2)
            || candidate.Source.Height != 2 * pitch || candidate.Support != 0)
            return Fail("crossing_not_whole_cumulative_displacement");
        var continuation = source.Where(r => r.Page.Page == 2 && lookup[r.Text].Page.Page == 2).ToArray();
        if (continuation.Length < options.MinSupportBands || continuation.Length != source.Count(r => r.Page.Page == 2)
            || continuation.Any(r => Math.Round(lookup[r.Text].Baseline - r.Baseline) != candidate.Source.Height
                || Math.Round(lookup[r.Text].Left - r.Left) != 0 || lookup[r.Text].Top < candidate.Target.Bottom))
            return Fail("independent_next_page_support");
        // Supportは元ページ内の0のまま残す。送り帯を独立支持の数へ混ぜない。
        return new("proposed", null, new(new(last.Page, last.Top, last.Height), candidate,
            continuation, continuation.Select(r => lookup[r.Text]).ToArray(), boundary.Crossing, boundary.Counterparts,
            causes.Select(r => r.Text).ToArray(), before, between));
        Line[] Ordered(PageSpace side) => rows.Where(r => r.Page.Side == side).OrderBy(r => r.Page.Page).ThenBy(r => r.Top).ToArray();
        static Result Fail(string reason) => new("skipped", reason);
    }
}
