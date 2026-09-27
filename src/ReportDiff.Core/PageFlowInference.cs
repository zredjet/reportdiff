namespace ReportDiff.Core;

/// <summary>選択した元ページの記述から、完全一致の本文行と固定部を使って隣接ページへの送り候補を推定する。</summary>
public static class PageFlowInference
{
    public sealed record Layout(PageFlowPageDescriptor Page, IReadOnlyList<PageFlowLine> Body, int HeaderEnd, int FooterStart,
        int BodyStart, int BodyEnd, int Pitch)
    {
        public bool Regular => Pitch > 0 && BodyEnd - BodyStart == Body.Count * Pitch
            && Body.Select((line, i) => line.Bounds.Top >= BodyStart + i * Pitch
                && line.Bounds.Bottom <= BodyStart + (i + 1) * Pitch).All(x => x);
    }
    public sealed record Prepared(string Status, string? Reason, IReadOnlyList<Layout> A, IReadOnlyList<Layout> B);
    public sealed record Proposal(PageFlowBand? Source, PageFlowBand? Target, IReadOnlyList<string> Text, int Support, string Status, string? Reason)
    {
        // 端点未確定でも、再検証する元の境界と方向を失わない。本文・帯の証明ではない。
        internal PageFlowPageKey? AmbiguousSource { get; init; }
    }
    public sealed record Inference(Prepared Layouts, IReadOnlyList<Proposal> Proposals)
    {
        internal PageFlowDocumentDescriptor? Document { get; init; }
        internal Prepared? BoundLayouts { get; init; }
        internal IReadOnlyList<Proposal>? BoundProposals { get; init; }
        internal int MaximumShiftPixels { get; init; }
        internal int MinimumSupport { get; init; }
    }

    // options のうち候補探索に使うのは MaxShiftMm / MinSupportBands。ページ内の採用評価とは別の段階。
    public static Inference Find(PageFlowDocumentDescriptor document, RowOptions options, int dpi)
    {
        options.Validated(dpi);
        var a = document.Pages.Where(p => p.Key.Side == PageSpace.A).OrderBy(p => p.Key.Page).ToArray();
        var b = document.Pages.Where(p => p.Key.Side == PageSpace.B).OrderBy(p => p.Key.Page).ToArray();
        var prepared = Prepare(a, b);
        if (prepared.Status != "prepared") return new(prepared, []);
        var proposals = new List<Proposal>();
        Direction(prepared.A, prepared.B);
        Direction(prepared.B, prepared.A);
        if (proposals.Count > PageFlowLimits.MaximumCandidates) return new(new("skipped", "flow_candidate_limit", [], []), []);
        var frozen = Array.AsReadOnly(proposals.ToArray());
        return new(prepared, frozen) { Document = document, BoundLayouts = prepared, BoundProposals = frozen,
            MaximumShiftPixels = Units.RoundPixels(options.MaxShiftMm, dpi), MinimumSupport = options.MinSupportBands };

        void Direction(IReadOnlyList<Layout> source, IReadOnlyList<Layout> target)
        {
            var countSource = Counts(source); var countTarget = Counts(target);
            foreach (var s in source)
            {
                if (proposals.Count > PageFlowLimits.MaximumCandidates) return;
                var same = target.SingleOrDefault(p => p.Page.Key.Page == s.Page.Key.Page);
                var next = target.SingleOrDefault(p => p.Page.Key.Page == s.Page.Key.Page + 1);
                if (same is null || next is null) continue;
                var shifts = s.Body.Where(l => countSource[Text(l)] == 1 && countTarget.GetValueOrDefault(Text(l)) == 1)
                    .Select(l => (Source: l, Target: same.Body.SingleOrDefault(t => Text(t) == Text(l))))
                    .Where(p => p.Target is not null && Math.Round(p.Target.Bounds.Left - p.Source.Bounds.Left) == 0)
                    .Select(p => (int)Math.Round(p.Target!.Baseline - p.Source.Baseline))
                    .Where(dy => dy > 0 && dy <= Units.RoundPixels(options.MaxShiftMm, dpi))
                    .GroupBy(dy => dy).Where(g => g.Count() >= options.MinSupportBands).ToArray();
                if (shifts.Length == 0) continue;
                if (shifts.Length != 1)
                {
                    proposals.Add(new(null, null, [], 0,
                        "skipped", "ambiguous_displacement") { AmbiguousSource = s.Page.Key });
                    continue;
                }
                var length = shifts[0].Key;
                if (s.BodyEnd < length || (long)next.BodyStart + length > next.Page.Size.Height)
                {
                    proposals.Add(new(null, null, [], shifts[0].Count(), "skipped", "unproven_body_bounds"));
                    continue;
                }
                var from = new PageFlowBand(s.Page.Key, s.BodyEnd - length, length);
                var to = new PageFlowBand(next.Page.Key, next.BodyStart, length);
                string? reason = null;
                if (!s.Regular || length % s.Pitch != 0 || length >= s.BodyEnd - s.BodyStart
                    || to.Top + length > next.FooterStart) reason = "unproven_body_bounds";
                var linesA = Within(s, from); var linesB = Within(next, to);
                var textA = linesA.Select(Text).ToArray(); var textB = linesB.Select(Text).ToArray();
                if (reason is null && (Crosses(s, from) || Crosses(next, to) || linesA.Length == 0 || linesB.Length == 0))
                    reason = "cut_crosses_text";
                if (reason is null && (textA.Any(t => countSource[t] != 1) || textB.Any(t => countTarget[t] != 1)))
                    reason = "repeated_body_text";
                if (reason is null && !textA.SequenceEqual(textB)) reason = "text_mismatch";
                proposals.Add(new(from, to, Array.AsReadOnly(textA), shifts[0].Count(), reason is null ? "candidate" : "skipped", reason));
            }
        }
    }

    private static Prepared Prepare(IReadOnlyList<PageFlowPageDescriptor> a, IReadOnlyList<PageFlowPageDescriptor> b)
    {
        if (a.Concat(b).Any(p => p.TextStatus != "available")) return new("skipped", "text_unavailable", [], []);
        var shared = a.Select(p => p.Key.Page).Intersect(b.Select(p => p.Key.Page)).ToHashSet();
        if (shared.Count < 2) return new("skipped", "fixed_parts_support", [], []);
        var paired = a.Concat(b).Where(p => shared.Contains(p.Key.Page)).ToArray();
        var size = paired[0].Size;
        if (a.Concat(b).Any(p => p.Size != size)) return new("skipped", "size_mismatch", [], []);
        var fixedKeys = paired[0].Lines.Select(Key).ToHashSet();
        foreach (var page in paired.Skip(1)) fixedKeys.IntersectWith(page.Lines.Select(Key));
        var headerCount = paired.Min(p => p.Lines.TakeWhile(l => fixedKeys.Contains(Key(l))).Count());
        var footerCount = paired.Min(p => p.Lines.Reverse().TakeWhile(l => fixedKeys.Contains(Key(l))).Count());
        if (headerCount < 2 || footerCount < 2 || paired.Any(p => headerCount + footerCount >= p.Lines.Count))
            return new("skipped", "fixed_parts_support", [], []);
        var first = paired[0];
        var header = first.Lines.Take(headerCount).Select(Key).ToArray();
        var footer = first.Lines.TakeLast(footerCount).Select(Key).ToArray();
        var headerEnd = (int)Math.Ceiling(first.Lines[headerCount - 1].Bounds.Bottom);
        var footerStart = (int)Math.Floor(first.Lines[^footerCount].Bounds.Top);
        if (headerEnd <= 0 || footerStart >= size.Height || footerStart <= headerEnd)
            return new("skipped", "unproven_body_bounds", [], []);
        foreach (var page in a.Concat(b))
        {
            if (!page.Lines.Take(headerCount).Select(Key).SequenceEqual(header)
                || !page.Lines.TakeLast(footerCount).Select(Key).SequenceEqual(footer))
                return new("skipped", "fixed_parts_mismatch", [], []);
            if (!page.SameRowHashes(first, 0, 0, headerEnd)
                || !page.SameRowHashes(first, footerStart, footerStart, page.Size.Height - footerStart))
                return new("skipped", "fixed_parts_pixels", [], []);
        }
        var bodyByPage = a.Concat(b).ToDictionary(p => p, p => p.Lines.Skip(headerCount).SkipLast(footerCount).ToArray());
        var pitches = bodyByPage.Values.SelectMany(lines => lines.Zip(lines.Skip(1), (x, y) =>
            (int)Math.Round(y.Baseline - x.Baseline))).Distinct().ToArray();
        if (pitches.Length != 1 || pitches[0] <= 0) return new("skipped", "nonuniform_row_pitch", [], []);
        var layouts = new Dictionary<PageFlowPageDescriptor, Layout>();
        foreach (var (page, lines) in bodyByPage)
        {
            var ink = Enumerable.Range(headerEnd, footerStart - headerEnd).Where(y => page.RowHasNonwhite(y)).ToArray();
            if (lines.Length == 0 || ink.Length == 0) return new("skipped", "empty_body", [], []);
            layouts[page] = new(page, Array.AsReadOnly(lines), headerEnd, footerStart, ink[0], ink[^1] + 1, pitches[0]);
        }
        return new("prepared", null, Array.AsReadOnly(a.Select(p => layouts[p]).ToArray()), Array.AsReadOnly(b.Select(p => layouts[p]).ToArray()));
    }

    internal static string Text(PageFlowLine line) => line.Text;
    private static (string Text, double Baseline, double Left, double Right) Key(PageFlowLine line) =>
        (line.Text, Math.Round(line.Baseline), Math.Round(line.Bounds.Left), Math.Round(line.Bounds.Right));
    private static Dictionary<string, int> Counts(IReadOnlyList<Layout> pages) => pages.SelectMany(p => p.Body)
        .GroupBy(Text).ToDictionary(g => g.Key, g => g.Count());
    private static PageFlowLine[] Within(Layout page, PageFlowBand band) => page.Body.Where(l => l.Bounds.Top >= band.Top
        && l.Bounds.Bottom <= band.Top + band.Height).ToArray();
    private static bool Crosses(Layout page, PageFlowBand band) => page.Page.Lines
        .Any(w => (w.Bounds.Top < band.Top && w.Bounds.Bottom > band.Top)
            || (w.Bounds.Top < band.Top + band.Height && w.Bounds.Bottom > band.Top + band.Height));
}
