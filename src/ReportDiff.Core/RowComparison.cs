using OpenCvSharp;

namespace ReportDiff.Core;

internal sealed record RowDecision(string Status, string Reason, string? Detail = null, RowSelection? Selection = null,
    int? BaselineRawPixels = null, int? CandidateRawPixels = null, double? Improvement = null);

/// <summary>比較結果と、採用した場合の内容比較画像・表示投影を所有する。入力A/Bは所有しない。</summary>
public sealed record RowAlignmentInfo(string Status, string Reason, string? Detail, bool Suspected, double? Score,
    double? ScoreGap, int? Hypotheses, IReadOnlyList<int>? SupportBands, int? BaselineRawPixels, int? CandidateRawPixels, double? Improvement);

public sealed class RowComparison : IDisposable
{
    internal RowComparison(PageComparison comparison, RowDecision decision, Mat? contentA = null, Mat? contentB = null,
        RowDisplayProjection? display = null)
    { Comparison = comparison; Decision = decision; ContentA = contentA; ContentB = contentB; Display = display; }
    public PageComparison Comparison { get; }
    internal RowDecision Decision { get; }
    public RowAlignmentInfo Alignment => new(Decision.Status, Decision.Reason, Decision.Detail, Decision.Selection?.Suspected ?? false,
        Decision.Selection?.Score, Decision.Selection?.ScoreGap, Decision.Selection?.Hypotheses, Decision.Selection?.SupportBands,
        Decision.BaselineRawPixels, Decision.CandidateRawPixels, Decision.Improvement);
    public RowComparisonSurface? Surface => Decision.Status == "applied" ? Decision.Selection!.Candidate!.Layout.Surface : null;
    public Mat? ContentA { get; }
    public Mat? ContentB { get; }
    public RowDisplayProjection? Display { get; }
    public string Status => Display?.Comparison.Status ?? Comparison.Status;
    public int DifferenceCount => Display?.DifferenceCount ?? Comparison.Clusters.Count;
    public void Dispose() { Comparison.Dispose(); ContentA?.Dispose(); ContentB?.Dispose(); Display?.Dispose(); }
}

/// <summary>最良の一意な仮説だけを既存の比較器へ渡し、生差分の改善率で採否を決める。</summary>
public static class RowComparer
{
    public static RowComparison Compare(Mat a, Mat b, ComparisonParameters parameters, RowOptions options,
        Func<(RowTextResult A, RowTextResult B)> readText, bool pdfPair = true, bool originalSizesEqual = true,
        double minLineOverlap = 0.5, PageMap? globalMap = null)
    {
        options.Validated(parameters.Dpi);
        PageComparison? baseline = PageComparer.Compare(a, b, parameters);
        PageComparison? candidate = null; Mat? ca = null; Mat? cb = null; RowDisplayProjection? display = null;
        try
        {
            if (!options.Enabled) return Fallback(new("disabled", "disabled"));
            if (Cv2.Norm(a, b, NormTypes.INF) == 0) return Fallback(new("skipped", "identical"));
            if (!pdfPair) return Fallback(new("skipped", "no_text", "not_pdf"));
            if (!originalSizesEqual) return Fallback(new("skipped", "size_mismatch"));
            var text = readText();
            var selection = RowSelector.Select(a, b, text.A, text.B, parameters, options, minLineOverlap);
            if (selection.Candidate is null) return Fallback(new("skipped", selection.Reason, selection.Detail, selection));
            if (baseline.RawPixels == 0) return Fallback(new("skipped", "low_improvement", "baseline_zero", selection, 0));
            var surface = selection.Candidate.Layout.Surface;
            ca = surface.ContentMap.Render(a, PageSpace.A); cb = surface.ContentMap.Render(b, PageSpace.B);
            // A基準の設定をDで確定してCへ転写。C上の矩形面積で優先順位を決め直さない。
            candidate = parameters.Regions.Count == 0 && parameters.Exclude.Count == 0
                ? PageComparer.Compare(ca, cb, parameters, true, retainProjection: true)
                : RegionalComparer.Compare(ca, cb, parameters with { Exclude = [] }, RegionMap.ForRows(surface, parameters), retainProjection: true);
            using (var displayRaw = surface.ToDisplay(candidate.RawMask))
                if (Cv2.CountNonZero(displayRaw) != candidate.RawPixels)
                    throw new InvalidOperationException("内容比較面から表示面への転写で生差分の画素数が変わりました。");
            var improvement = (baseline.RawPixels - candidate.RawPixels) / (double)baseline.RawPixels;
            if (improvement < options.MinImprovement)
                return Fallback(new("skipped", "low_improvement", Selection: selection, BaselineRawPixels: baseline.RawPixels,
                    CandidateRawPixels: candidate.RawPixels, Improvement: improvement));
            display = RowProjection.Create(candidate, surface, a, b, parameters, selection.Candidate.EquivalentPositions, globalMap);
            var result = new RowComparison(candidate, new("applied", "applied", Selection: selection,
                BaselineRawPixels: baseline.RawPixels, CandidateRawPixels: candidate.RawPixels, Improvement: improvement), ca, cb, display);
            candidate = null; ca = cb = null; display = null;
            return result;

            RowComparison Fallback(RowDecision decision)
            {
                var result = new RowComparison(baseline!, decision);
                baseline = null;
                return result;
            }
        }
        finally { baseline?.Dispose(); candidate?.Dispose(); ca?.Dispose(); cb?.Dispose(); display?.Dispose(); }
    }
}
