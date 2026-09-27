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
    public static ReportDocument Compare(CompareCommand command, AppSettings settings, string output, ConsoleProgress? progress = null)
    {
        using var a = new ComparisonInput(command.InputA, settings);
        using var b = new ComparisonInput(command.InputB, settings);
        if (a.Dpi != b.Dpi)
            throw new CommandLineException("PDF と画像を混在させる場合は、設定の dpi と image_dpi を同じ値にしてください。");
        var plan = PagePairing.Create(a.PageCount, b.PageCount, command.Pages);
        var inputs = new ReportInputs(a.Describe(), b.Describe());
        var writer = new ReportWriter(output, inputs, settings.ToReportConfiguration(), command.SaveAllPages);
        var index = 0;
        foreach (var page in plan.Pages)
        {
            progress?.Page(++index, plan.Pages.Count, page.PageNumber, command.Pages is not null);
            if (page.CanCompare)
            {
                using var imageA = a.ReadPage(page.PageNumber);
                using var imageB = b.ReadPage(page.PageNumber);
                using var normalized = PageNormalizer.Normalize(imageA.Pixels, imageB.Pixels);
                var parameters = settings.ForPage(page.PageNumber, a.Dpi);
                var alignment = GlobalAligner.Estimate(normalized.A, normalized.B, parameters, settings.Align, normalized.SizeMismatch);
                var appliedShift = alignment.Status == "applied" ? alignment.EstimatedShiftPx : null;
                var map = PageMap.Global(normalized.OriginalSizeA, normalized.OriginalSizeB, normalized.A.Size(), appliedShift);
                using var correctedB = appliedShift is null ? null : map.Render(normalized.B, PageSpace.B);
                RowTextResult? wordsA = null, wordsB = null;
                using var rows = RowComparer.Compare(normalized.A, correctedB ?? normalized.B, parameters, settings.Rows,
                    () => (wordsA = a.ReadRowWords(page.PageNumber, map, PageSpace.A), wordsB = b.ReadRowWords(page.PageNumber, map, PageSpace.B)),
                    pdfPair: a.Format == InputFormat.Pdf && b.Format == InputFormat.Pdf, originalSizesEqual: !normalized.SizeMismatch,
                    minLineOverlap: settings.Text.MinLineOverlap, globalMap: map);
                var comparison = rows.Display?.Comparison ?? rows.Comparison;
                using var displayA = rows.Surface?.DisplayMap.Render(normalized.A, PageSpace.A);
                using var displayB = rows.Surface?.DisplayMap.Render(correctedB ?? normalized.B, PageSpace.B);
                var textA = rows.Surface is { } surfaceA
                    ? RowTextAnnotations.Create(wordsA!, surfaceA.DisplayMap, PageSpace.A, a.Dpi, comparison.Clusters, parameters.Exclude, settings.Text)
                    : a.Annotate(page.PageNumber, map, PageSpace.A, comparison.Clusters, parameters.Exclude);
                var textB = rows.Surface is { } surfaceB
                    ? RowTextAnnotations.Create(wordsB!, surfaceB.DisplayMap, PageSpace.B, b.Dpi, comparison.Clusters, parameters.Exclude, settings.Text)
                    : b.Annotate(page.PageNumber, map, PageSpace.B, comparison.Clusters, parameters.Exclude);
                PageTextAnnotations? structureA = null, structureB = null;
                if (rows.Display is { } display)
                {
                    var structures = display.StructuralChanges.Select(s => new DifferenceCluster(s.Id, s.DisplayBounds, 0)).ToArray();
                    structureA = RowTextAnnotations.Create(wordsA!, rows.Surface!.DisplayMap, PageSpace.A, a.Dpi, structures, parameters.Exclude, settings.Text);
                    structureB = RowTextAnnotations.Create(wordsB!, rows.Surface.DisplayMap, PageSpace.B, b.Dpi, structures, parameters.Exclude, settings.Text);
                }
                writer.AddComparedPage(page.PageNumber, normalized, comparison, a.Dpi, textA, textB, alignment, correctedB,
                    new(rows, displayA, displayB, structureA, structureB));
            }
            else
            {
                using var image = (page.HasA ? a : b).ReadPage(page.PageNumber);
                writer.AddUnpairedPage(page.PageNumber, image.Pixels);
            }
            if (page.HasA) writer.AddFontWarnings(page.PageNumber, "A", a.InspectFonts(page.PageNumber));
            if (page.HasB) writer.AddFontWarnings(page.PageNumber, "B", b.InspectFonts(page.PageNumber));
        }
        progress?.Report();
        var report = writer.Complete();
        if (!command.NoHtml) HtmlReportWriter.Write(output, report);
        progress?.EndLine();
        return report;
    }

}
