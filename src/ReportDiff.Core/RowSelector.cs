using System.Globalization;
using OpenCvSharp;

namespace ReportDiff.Core;

internal sealed record RowSelection(string Reason, string? Detail = null, CanonicalRowCandidate? Candidate = null,
    bool Suspected = false, double? Score = null, double? ScoreGap = null, int Hypotheses = 0, IReadOnlyList<int>? SupportBands = null);

internal static class RowSelector
{
    internal const int MaximumHypotheses = 64;

    internal static RowSelection Select(Mat a, Mat b, RowTextResult textA, RowTextResult textB, ComparisonParameters parameters,
        RowOptions options, double minLineOverlap = 0.5)
    {
        options.Validated(parameters.Dpi);
        if (textA.Status == "text_unavailable" || textB.Status == "text_unavailable")
            return new("text_unavailable", textA.Detail ?? textB.Detail);
        if (textA.Words.Count == 0 || textB.Words.Count == 0) return new("no_text");
        try
        {
            // 除外した文字も切断からは保護する。対応・スコアの支持には使わない。
            var cutLinesA = TextLineLayout.Lines(textA.Words, minLineOverlap);
            var cutLinesB = TextLineLayout.Lines(textB.Words, minLineOverlap);
            // Aの設定座標は行整列前。Bは候補が未確定なので、許される縦移動の全範囲を除外から避ける。
            var exclusions = RowExclusions.Rectangles(parameters).Select(r => PageMap.ContinuousPixels(r, parameters.Dpi)).ToArray();
            var maximumShift = Units.RoundPixels(options.MaxShiftMm, parameters.Dpi);
            var wordsA = textA.Words.Where(w => !exclusions.Any(e => Intersects(w.Bounds, e, 0))).ToArray();
            var wordsB = textB.Words.Where(w => !exclusions.Any(e => Intersects(w.Bounds, e, maximumShift))).ToArray();
            if (wordsA.Length == 0 || wordsB.Length == 0) return new("insufficient_support", "excluded_text");
            var linesA = TextLineLayout.Lines(wordsA, minLineOverlap); var linesB = TextLineLayout.Lines(wordsB, minLineOverlap);
            if (ColumnConflict(linesA, linesB, options, parameters.Dpi)) return new("column_conflict", Suspected: true);
            var matches = RowMatching.Find(linesA, linesB, options, parameters.Dpi);
            if (matches.Detail is not null) return new("resource_limit", matches.Detail);
            var suspected = matches.Paths.Any(path => path.GroupBy(m => (int)Math.Round(linesA[m.A].Baseline - linesB[m.B].Baseline))
                .Any(g => g.Key != 0 && g.Select(m => m.A).Distinct().Count() >= options.MinSupportBands));
            if (matches.Paths.Count == 0) return new("insufficient_support", "no_line_matches");
            var candidates = new List<CanonicalRowCandidate>(); var failures = new List<RowCandidateBuild>();
            foreach (var path in matches.Paths)
            {
                var built = RowCandidateBuilder.Build(a, b, linesA, linesB, path, parameters, options, cutLinesA, cutLinesB);
                if (built.Candidate is null) { failures.Add(built); continue; }
                var canonical = RowCanonicalizer.Normalize(a, b, built.Candidate, parameters);
                if (canonical is null) { failures.Add(new(null, "no_bands", "canonical_neighborhood")); continue; }
                if (candidates.Any(c => c.Layout.DisplayMap.Segments.SequenceEqual(canonical.Layout.DisplayMap.Segments)
                    && c.Layout.Kinds.SequenceEqual(canonical.Layout.Kinds))) continue;
                if (candidates.Count == MaximumHypotheses) return new("resource_limit", "hypothesis_limit", Suspected: suspected, Hypotheses: candidates.Count + 1);
                candidates.Add(canonical);
            }
            // 検証しきれない有力候補があれば、別の候補だけを使って一意とはしない。
            var unresolved = failures.FirstOrDefault(f => f.Reason is "ambiguous" or "resource_limit");
            if (unresolved is not null) return new(unresolved.Reason, unresolved.Detail, Suspected: suspected, Hypotheses: candidates.Count);
            if (candidates.Count == 0)
            {
                var failure = failures.OrderBy(f => FailureOrder(f.Reason)).ThenBy(f => f.Detail, StringComparer.Ordinal).FirstOrDefault();
                return new(failure?.Reason ?? "insufficient_support", failure?.Detail, Suspected: suspected);
            }
            var scores = RowSupport.Score(a, b, linesA, linesB, candidates, parameters, options, matches);
            if (scores is null) return new("insufficient_support", "common_support", Suspected: suspected, Hypotheses: candidates.Count);
            var order = Enumerable.Range(0, candidates.Count).OrderByDescending(i => scores[i].Score).ToArray();
            var best = order[0]; double? gap = order.Length == 1 ? null : scores[best].Score - scores[order[1]].Score;
            if (gap < options.MinScoreGap) return new("ambiguous", "competing_hypotheses", Suspected: suspected,
                Score: scores[best].Score, ScoreGap: gap, Hypotheses: candidates.Count);
            return new("candidate", Candidate: candidates[best], Suspected: suspected, Score: scores[best].Score, ScoreGap: gap,
                Hypotheses: candidates.Count, SupportBands: scores[best].BandsPerSegment);
        }
        catch (RowResourceLimitException ex) { return new("resource_limit", ex.Message); }
    }

    private static int FailureOrder(string reason) => reason switch { "no_bands" => 0, "too_many_segments" => 1, "insufficient_support" => 2, _ => 3 };
    private static bool Intersects(PageBounds word, Rect2d exclusion, int verticalRadius) => word.Left < exclusion.Right
        && word.Right > exclusion.Left && word.Top < exclusion.Bottom + verticalRadius && word.Bottom > exclusion.Top - verticalRadius;

    internal static bool ColumnConflict(IReadOnlyList<RowLine> a, IReadOnlyList<RowLine> b, RowOptions options, int dpi)
    {
        var wordsA = a.SelectMany((line, i) => line.Words.Select(word => (Word: word, Line: i, line.Baseline))).GroupBy(w => w.Word.Text)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var wordsB = b.SelectMany((line, i) => line.Words.Select(word => (Word: word, Line: i, line.Baseline))).GroupBy(w => w.Word.Text)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var radius = Units.RoundPixels(options.RefineMm, dpi); var maximum = Units.RoundPixels(options.MaxShiftMm, dpi);
        var pairs = wordsA.Where(w => wordsB.ContainsKey(w.Key)).Select(w => (A: w.Value, B: wordsB[w.Key]))
            .Where(p => Math.Min(p.A.Word.Bounds.Right, p.B.Word.Bounds.Right) > Math.Max(p.A.Word.Bounds.Left, p.B.Word.Bounds.Left)
                && Math.Abs(p.A.Baseline - p.B.Baseline) <= maximum).ToArray();
        // 同じ行で横に分かれた内容が、複数行にわたり別々の移動を支持するか。
        // ページ全体の外接矩形を使うと、共通見出しが左右の列を結んで矛盾を隠す。
        var serialA = SerialAnchors(a); var serialB = SerialAnchors(b);
        var stable = pairs.Where(p => !serialA.Contains(p.A.Word) || !serialB.Contains(p.B.Word)).ToArray();
        var conflicts = new List<(int A, int B1, int B2, int Dy1, int Dy2)>();
        var checkedPairs = 0;
        foreach (var line in stable.GroupBy(p => p.A.Line))
        {
            var words = line.OrderBy(p => p.A.Word.Bounds.Left).ToArray();
            for (var i = 0; i < words.Length; i++)
            for (var j = i + 1; j < words.Length; j++)
            {
                if (++checkedPairs > RowMatching.MaximumTraceSteps) throw new RowResourceLimitException("column_conflict_limit");
                var one = words[i]; var two = words[j];
                var dy1 = (int)Math.Round(one.A.Baseline - one.B.Baseline);
                var dy2 = (int)Math.Round(two.A.Baseline - two.B.Baseline);
                if (Math.Abs(dy1 - dy2) <= 2 * radius || one.A.Word.Bounds.Right > two.A.Word.Bounds.Left
                    || one.B.Word.Bounds.Right > two.B.Word.Bounds.Left) continue;
                conflicts.Add((line.Key, one.B.Line, two.B.Line, dy1, dy2));
            }
        }
        if (conflicts.GroupBy(c => (c.Dy1, c.Dy2)).Any(g => g.Select(c => c.A).Distinct().Count() >= options.MinSupportBands
            && g.Select(c => c.B1).Distinct().Count() >= options.MinSupportBands
            && g.Select(c => c.B2).Distinct().Count() >= options.MinSupportBands)) return true;
        return false;
    }
    private static HashSet<RowWord> SerialAnchors(IReadOnlyList<RowLine> lines)
    {
        // 同じ横位置で3行以上連続して+1になる番号は、行の恒久的な識別子ではない。
        // 列の矛盾の根拠からだけ外す。単語LCS・画像評価・内容比較には数字を全て残す。
        var result = new HashSet<RowWord>();
        var numeric = lines.SelectMany((line, i) => line.Words.Select(w => (Word: w, Line: i)))
            .Where(p => long.TryParse(p.Word.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(p => (p.Word, p.Line, Value: long.Parse(p.Word.Text, CultureInfo.InvariantCulture)))
            .GroupBy(p => (int)Math.Round(p.Word.Bounds.Left));
        foreach (var column in numeric)
        {
            var run = new List<RowWord>(); var previousLine = -2; long previousValue = long.MaxValue;
            foreach (var entry in column.OrderBy(p => p.Line))
            {
                if (entry.Line != previousLine + 1 || previousValue == long.MaxValue || entry.Value != previousValue + 1)
                { Finish(); run.Clear(); }
                run.Add(entry.Word); previousLine = entry.Line; previousValue = entry.Value;
            }
            Finish();
            void Finish() { if (run.Count >= 3) result.UnionWith(run); }
        }
        return result;
    }

}
