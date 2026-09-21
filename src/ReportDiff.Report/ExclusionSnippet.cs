using System.Globalization;
using ReportDiff.Core;

namespace ReportDiff.Report;

internal static class ExclusionSnippet
{
    public static string Create(ReportPage page, ReportCluster cluster, int dpi, double marginMm)
    {
        if (!double.IsFinite(marginMm) || marginMm is < 0 or > 20)
            throw new ArgumentException("除外 YAML の余白は 0〜20 の有限の mm 値にしてください。", nameof(marginMm));
        var box = cluster.BboxMm;
        var candidate = PageMap.ExclusionCandidate(new(box.X, box.Y, box.W, box.H), new(page.SizePx.W, page.SizePx.H), dpi, marginMm);
        return FormattableString.Invariant($"- {{page: {page.Page}, x: {Number(candidate.X)}, y: {Number(candidate.Y)}, w: {Number(candidate.W)}, h: {Number(candidate.H)}, note: \"\"}}");
    }

    private static string Number(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
}
