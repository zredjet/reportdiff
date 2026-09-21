using System.Globalization;
using ReportDiff.Core;

namespace ReportDiff.Report;

internal static class ExclusionSnippet
{
    public static string Create(ReportPage page, ReportCluster cluster, int dpi, double marginMm)
    {
        if (!double.IsFinite(marginMm) || marginMm is < 0 or > 20)
            throw new ArgumentException("除外 YAML の余白は 0〜20 の有限の mm 値にしてください。", nameof(marginMm));
        var width = Units.PixelsToMm(page.SizePx.W, dpi);
        var height = Units.PixelsToMm(page.SizePx.H, dpi);
        var box = cluster.BboxMm;
        var left = Math.Clamp(Math.Floor((box.X - marginMm) * 2) / 2, 0, width);
        var top = Math.Clamp(Math.Floor((box.Y - marginMm) * 2) / 2, 0, height);
        var right = Math.Clamp(Math.Ceiling((box.X + box.W + marginMm) * 2) / 2, left, width);
        var bottom = Math.Clamp(Math.Ceiling((box.Y + box.H + marginMm) * 2) / 2, top, height);
        return FormattableString.Invariant($"- {{page: {page.Page}, x: {Number(left)}, y: {Number(top)}, w: {Number(right - left)}, h: {Number(bottom - top)}, note: \"\"}}");
    }

    private static string Number(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
}
