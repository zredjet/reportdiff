using OpenCvSharp;

namespace ReportDiff.Core;

internal static class AnchoredAdoption
{
    internal static PageFlowAdoptionResult Evaluate(AnchoredContentPlan plan, int page, int candidateRaw,
        Func<PageFlowPageKey, Mat> read, Func<PageFlowPageKey, RowTextResult> readText)
    {
        using var a = plan.ReadVerified(new(PageSpace.A, page), read);
        using var b = plan.ReadVerified(new(PageSpace.B, page), read);
        var da = plan.Descriptor(new(PageSpace.A, page)); var db = plan.Descriptor(new(PageSpace.B, page));
        var display = plan.Displays.Single(d => d.Page == page);
        var p = plan.Settings.Comparison; var options = plan.Settings.Rows;
        using var reserve = new AnchoredReservations(plan.Budget);
        reserve.Add(AnchoredAllocation.Reference, checked(32L * (da.Lines.Count + db.Lines.Count + display.Segments.Count)
            + (long)da.Lines.Count * db.Lines.Count));
        var originalA = OriginalLines(da); var originalB = OriginalLines(db);
        if (RowSelector.ColumnConflict(originalA, originalB, options, p.Dpi)) return new(false, "column_conflict");
        var segments = new List<PageSegment>();
        foreach (var segment in display.Segments)
        {
            var s = segment.Band; var last = segments.LastOrDefault();
            if (last is not null && s.A is not null && s.B is not null && last.AStart is not null && last.BStart is not null
                && last.AStart + last.Length == s.A.Top && last.BStart + last.Length == s.B.Top)
                segments[^1] = last with { Length = last.Length + s.Height };
            else segments.Add(new(s.Top, s.Height, s.A?.Top, s.B?.Top));
        }
        var map = new PageMap(a.Size(), b.Size(), display.Size, segments);
        var aa = Lines(da); var bb = Lines(db);
        var matches = new List<RowMatch>();
        for (var i = 0; i < aa.Length; i++)
        {
            var line = aa[i]; var band = segments.FirstOrDefault(s => s.AStart is int start && s.BStart is not null
                && line.Bounds.Top >= start && line.Bounds.Bottom <= start + s.Length);
            if (band is null) continue;
            var choices = Enumerable.Range(0, bb.Length).Where(j => line.Words[0].Text == bb[j].Words[0].Text
                && bb[j].Bounds.Top >= band.BStart && bb[j].Bounds.Bottom <= band.BStart + band.Length
                && Math.Abs(line.Baseline - bb[j].Baseline - band.Dy!.Value) <= .5).ToArray();
            if (choices.Length == 1) matches.Add(new(i, choices[0]));
        }
        var validation = RowGroupValidator.Validate(a, b, aa, bb, matches, p, options);
        if (validation.Groups is null) return new(false, validation.Reason, validation.Detail);
        if (validation.Groups.Any(g => g.Matches.Any(m => (int)Math.Round(aa[m.A].Baseline - bb[m.B].Baseline) != g.Dy)))
            return new(false, "ambiguous", "refined_mapping_changed");
        var eligible = new bool[checked(aa.Length * bb.Length)];
        foreach (var m in matches) eligible[m.A * bb.Length + m.B] = true;
        var scores = RowSupport.ScoreMaps(a, b, aa, bb, [map], p, options, new([matches], null, eligible, bb.Length));
        if (scores is null) return new(false, "insufficient_support", "common_support");
        using var baseline = PageComparer.Compare(a, b, p);
        double? improvement = baseline.RawPixels == 0 ? null : (baseline.RawPixels - candidateRaw) / (double)baseline.RawPixels;
        var accepted = improvement >= options.MinImprovement;
        return new(accepted, accepted ? "applied" : "low_improvement", baseline.RawPixels == 0 ? "baseline_zero" : null,
            scores[0].Score, validation.Groups.Select(g => g.Gap).DefaultIfEmpty(null).Min(), scores[0].BandsPerSegment,
            baseline.RawPixels, candidateRaw, improvement);

        IReadOnlyList<RowLine> OriginalLines(PageFlowPageDescriptor descriptor)
        {
            // 連結済み本文だけでは左右の列の矛盾を検出できない。元単語を再取得して同じ行記述に結ぶ。
            var text = readText(descriptor.Key);
            if (text.Status != "available") throw new InvalidOperationException("flow_text_changed: 元の文字層を再取得できません。");
            reserve.Add(AnchoredAllocation.Reference, checked(32L * text.Words.Count));
            reserve.Add(AnchoredAllocation.TextCharacter, checked(3L * text.Words.Sum(w => (long)w.Text.Length + 1)));
            var lines = TextLineLayout.Lines(text.Words, plan.Settings.MinimumLineOverlap);
            if (lines.Count != descriptor.Lines.Count || lines.Where((l, i) =>
                string.Join(' ', l.Words.Select(w => w.Text)) != descriptor.Lines[i].Text
                || l.Bounds != descriptor.Lines[i].Bounds || l.Baseline != descriptor.Lines[i].Baseline).Any())
                throw new InvalidOperationException("flow_text_changed: 文字層の行配置が変わりました。");
            return lines;
        }
    }

    private static RowLine[] Lines(PageFlowPageDescriptor page) => page.Lines.Select(l =>
        new RowLine([new(l.Text, l.Bounds, [l.Baseline])], l.Bounds, l.Baseline)).ToArray();
}
