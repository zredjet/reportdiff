using System.Runtime.Versioning;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;

namespace ReportDiff.Cli;

internal static class ComparisonRunner
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    public static ReportDocument Compare(CompareCommand command, AppSettings settings, string output)
    {
        using var a = new ComparisonInput(command.InputA, settings);
        using var b = new ComparisonInput(command.InputB, settings);
        if (a.Dpi != b.Dpi)
            throw new CommandLineException("PDF と画像を混在させる場合は、設定の dpi と image_dpi を同じ値にしてください。");
        var plan = PagePairing.Create(a.PageCount, b.PageCount, command.Pages);
        var inputs = new ReportInputs(a.Describe(), b.Describe());
        var writer = new ReportWriter(output, inputs, settings.ToReportConfiguration(), command.SaveAllPages);
        foreach (var page in plan.Pages)
        {
            if (page.CanCompare)
            {
                using var imageA = a.ReadPage(page.PageNumber);
                using var imageB = b.ReadPage(page.PageNumber);
                using var normalized = PageNormalizer.Normalize(imageA.Pixels, imageB.Pixels);
                var parameters = settings.ForPage(page.PageNumber, a.Dpi);
                var alignment = GlobalAligner.Estimate(normalized.A, normalized.B, parameters, settings.Align, normalized.SizeMismatch);
                var appliedShift = alignment.Status == "applied" ? alignment.EstimatedShiftPx : null;
                using var correctedB = appliedShift is null ? null : GlobalAligner.TranslateB(normalized.B, appliedShift);
                using var comparison = PageComparer.Compare(normalized.A, correctedB ?? normalized.B, parameters);
                var textA = a.Annotate(page.PageNumber, normalized.OriginalSizeA, comparison.Clusters, parameters.Exclude);
                var textB = b.Annotate(page.PageNumber, normalized.OriginalSizeB, comparison.Clusters, parameters.Exclude, appliedShift);
                writer.AddComparedPage(page.PageNumber, normalized, comparison, a.Dpi, textA, textB, alignment, correctedB);
            }
            else
            {
                using var image = (page.HasA ? a : b).ReadPage(page.PageNumber);
                writer.AddUnpairedPage(page.PageNumber, image.Pixels);
            }
            if (page.HasA) writer.AddFontWarnings(page.PageNumber, "A", a.InspectFonts(page.PageNumber));
            if (page.HasB) writer.AddFontWarnings(page.PageNumber, "B", b.InspectFonts(page.PageNumber));
        }
        var report = writer.Complete();
        if (!command.NoHtml) HtmlReportWriter.Write(output, report);
        return report;
    }

}
