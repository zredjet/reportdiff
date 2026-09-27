using OpenCvSharp;

namespace ReportDiff.Core;

public sealed record PageFlowAdoptionResult(bool Accepted, string Reason, string? Detail = null,
    double? Score = null, double? ScoreGap = null, IReadOnlyList<int>? SupportBands = null,
    int? BaselineRawPixels = null, int? CandidateRawPixels = null, double? Improvement = null);

/// <summary>範囲成立とは別に、製品の行整列の支持条件・画素再探索・改善率を確認する。</summary>
public static class PageFlowAdoption
{
    public static PageFlowAdoptionResult Evaluate(PageFlowPlan plan, int page, Mat a, Mat b,
        ComparisonParameters parameters, RowOptions options, RowTextResult textA, RowTextResult textB, double minimumLineOverlap = .5)
    {
        options.Validated(parameters.Dpi);
        if (!plan.Decision.Ready) return new(false, "document_gate_not_ready");
        plan.VerifyComparisonImage(new(PageSpace.A, page), a); plan.VerifyComparisonImage(new(PageSpace.B, page), b);
        if (textA.Status != "available" || textB.Status != "available") return new(false, "text_unavailable");
        try
        {
            var originalA = TextLineLayout.Lines(textA.Words, minimumLineOverlap);
            var originalB = TextLineLayout.Lines(textB.Words, minimumLineOverlap);
            if (RowSelector.ColumnConflict(originalA, originalB, options, parameters.Dpi)) return new(false, "column_conflict");
        }
        catch (RowResourceLimitException ex) { return new(false, "resource_limit", ex.Message); }
        var la = plan.Inference.Layouts.A.Single(l => l.Page.Key.Page == page);
        var lb = plan.Inference.Layouts.B.Single(l => l.Page.Key.Page == page);
        var surface = plan.Pages.Single(p => p.Number == page).Built!.Surface!;
        // 連続した同じ移動の表示帯をまとめ、各区間に複数の独立した支持行を要求する。
        var segments = new List<PageSegment>();
        foreach (var s in surface.DisplayMap.Segments)
        {
            var last = segments.LastOrDefault();
            if (last is not null && last.AStart is not null && last.BStart is not null && s.AStart is not null && s.BStart is not null
                && last.AStart + last.Length == s.AStart && last.BStart + last.Length == s.BStart)
                segments[^1] = last with { Length = last.Length + s.Length };
            else segments.Add(s);
        }
        var map = new PageMap(a.Size(), b.Size(), surface.DisplayMap.CanvasSize, segments);
        var exclusions = RowExclusions.Rectangles(parameters).Select(r => PageMap.ContinuousPixels(r, parameters.Dpi)).ToArray();
        var maximum = Units.RoundPixels(options.MaxShiftMm, parameters.Dpi);
        var linesA = Lines(la.Page.Lines, 0); var linesB = Lines(lb.Page.Lines, maximum);
        if (linesA.Length == 0 || linesB.Length == 0) return new(false, "insufficient_support", "excluded_text");
        var matches = new List<RowMatch>();
        for (var i = 0; i < linesA.Length; i++)
        {
            var line = linesA[i];
            var band = segments.FirstOrDefault(s => s.AStart is int start && s.BStart is not null
                && line.Bounds.Top >= start && line.Bounds.Bottom <= start + s.Length);
            if (band is null) continue;
            var choices = Enumerable.Range(0, linesB.Length).Where(j => linesB[j].Words[0].Text == line.Words[0].Text
                && linesB[j].Bounds.Top >= band.BStart && linesB[j].Bounds.Bottom <= band.BStart + band.Length
                && Math.Abs(line.Baseline - linesB[j].Baseline - band.Dy!.Value) <= .5).ToArray();
            if (choices.Length == 1) matches.Add(new(i, choices[0]));
        }
        var validated = RowGroupValidator.Validate(a, b, linesA, linesB, matches, parameters, options);
        if (validated.Groups is null) return new(false, validated.Reason, validated.Detail);
        if (validated.Groups.Any(g => g.Matches.Any(m => (int)Math.Round(linesA[m.A].Baseline - linesB[m.B].Baseline) != g.Dy)))
            return new(false, "ambiguous", "refined_mapping_changed");
        // 全文の完全一致なので単語一致率は1。別の文字対応を曖昧さの解消に使わない。
        var eligible = new bool[checked(linesA.Length * linesB.Length)];
        foreach (var match in matches) eligible[match.A * linesB.Length + match.B] = true;
        var candidate = new CanonicalRowCandidate(new(map, [], validated.Groups, surface), []);
        var scores = RowSupport.Score(a, b, linesA, linesB, [candidate], parameters, options,
            new([matches], null, eligible, linesB.Length));
        if (scores is null) return new(false, "insufficient_support", "common_support");
        using var baseline = PageComparer.Compare(a, b, parameters);
        using var compared = plan.Compare(page, a, b);
        var unchangedMap = surface.OmittedBands.Count == 0 && segments.All(s => s.AStart == s.BStart && s.AStart is not null);
        var improvement = baseline.RawPixels == 0 ? (unchangedMap ? 0 : (double?)null)
            : (baseline.RawPixels - compared.Content.RawPixels) / (double)baseline.RawPixels;
        var accepted = unchangedMap || improvement >= options.MinImprovement;
        return new(accepted, accepted ? "applied" : "low_improvement", baseline.RawPixels == 0 && !unchangedMap ? "baseline_zero" : null,
            scores[0].Score, validated.Groups.Where(g => g.Gap is not null).Select(g => g.Gap).DefaultIfEmpty(null).Min(), scores[0].BandsPerSegment,
            baseline.RawPixels, compared.Content.RawPixels, improvement);

        RowLine[] Lines(IReadOnlyList<PageFlowLine> lines, int radius) => lines.Where(l => !exclusions.Any(e =>
            l.Bounds.Left < e.Right && l.Bounds.Right > e.Left && l.Bounds.Top < e.Bottom + radius && l.Bounds.Bottom > e.Top - radius))
            .Select(l => new RowLine([new(l.Text, l.Bounds, [l.Baseline])], l.Bounds, l.Baseline)).ToArray();
    }
}
