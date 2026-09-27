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
    public static ReportDocument Compare(CompareCommand command, AppSettings settings, string output, ConsoleProgress? progress = null,
        Action<string, AnchoredContentPlan, string>? anchoredStage = null)
    {
        using var a = new ComparisonInput(command.InputA, settings);
        using var b = new ComparisonInput(command.InputB, settings);
        if (a.Dpi != b.Dpi)
            throw new CommandLineException("PDF と画像を混在させる場合は、設定の dpi と image_dpi を同じ値にしてください。");
        var plan = PagePairing.Create(a.PageCount, b.PageCount, command.Pages);
        var inputs = new ReportInputs(a.Describe(), b.Describe());
        if (a.InitialSha256 is { } initialA && initialA != inputs.A.Sha256 || b.InitialSha256 is { } initialB && initialB != inputs.B.Sha256)
            throw new CommandLineException("送りの検証中に入力ファイルが変わりました。入力を固定して再実行してください。");
        var flow = settings.Rows.CarryEnabled ? PageFlowRun.Prepare(a, b, plan, settings, command.Pages is not null, progress) : null;
        using var anchored = AnchoredRun.Prepare(a, b, inputs, flow, plan, settings, command.Pages is not null);
        var anchoredReport = anchored?.Save(command, output, progress, anchoredStage);
        if (anchoredReport is not null) return anchoredReport;
        var writer = new ReportWriter(output, inputs, settings.ToReportConfiguration(), command.SaveAllPages);
        var index = 0;
        foreach (var page in plan.Pages)
        {
            progress?.Page(++index, plan.Pages.Count, page.PageNumber, command.Pages is not null);
            if (page.CanCompare)
            {
                using var imageA = a.ReadPage(page.PageNumber);
                using var imageB = b.ReadPage(page.PageNumber);
                flow?.VerifyAndSave(writer, page.PageNumber, PageSpace.A, imageA.Pixels);
                flow?.VerifyAndSave(writer, page.PageNumber, PageSpace.B, imageB.Pixels);
                using var normalized = PageNormalizer.Normalize(imageA.Pixels, imageB.Pixels);
                var parameters = settings.ForPage(page.PageNumber, a.Dpi);
                var alignment = flow?.Applied == true ? flow.Alignment(page.PageNumber)
                    : GlobalAligner.Estimate(normalized.A, normalized.B, parameters, settings.Align, normalized.SizeMismatch);
                var appliedShift = alignment.Status == "applied" ? alignment.EstimatedShiftPx : null;
                var map = PageMap.Global(normalized.OriginalSizeA, normalized.OriginalSizeB, normalized.A.Size(), appliedShift);
                using var correctedB = appliedShift is null ? null : map.Render(normalized.B, PageSpace.B);
                RowTextResult? wordsA = null, wordsB = null;
                using var carry = flow?.Applied == true ? flow.Plan!.Compare(page.PageNumber, normalized.A, correctedB ?? normalized.B) : null;
                if (carry is not null)
                    (wordsA, wordsB) = (a.ReadRowWords(page.PageNumber, map, PageSpace.A), b.ReadRowWords(page.PageNumber, map, PageSpace.B));
                using var rows = carry is not null ? null : RowComparer.Compare(normalized.A, correctedB ?? normalized.B, parameters, settings.Rows,
                    () => (wordsA = a.ReadRowWords(page.PageNumber, map, PageSpace.A), wordsB = b.ReadRowWords(page.PageNumber, map, PageSpace.B)),
                    pdfPair: a.Format == InputFormat.Pdf && b.Format == InputFormat.Pdf, originalSizesEqual: !normalized.SizeMismatch,
                    minLineOverlap: settings.Text.MinLineOverlap, globalMap: map);
                var surface = carry is not null ? flow!.Plan!.Pages.Single(p => p.Number == page.PageNumber).Built!.Surface : rows!.Surface;
                var projection = carry?.Display ?? rows?.Display;
                var comparison = projection?.Comparison ?? rows!.Comparison;
                using var displayA = surface?.DisplayMap.Render(normalized.A, PageSpace.A);
                using var displayB = surface?.DisplayMap.Render(correctedB ?? normalized.B, PageSpace.B);
                var textA = surface is { } surfaceA
                    ? RowTextAnnotations.Create(wordsA!, surfaceA.DisplayMap, PageSpace.A, a.Dpi, comparison.Clusters, parameters.Exclude, settings.Text)
                    : a.Annotate(page.PageNumber, map, PageSpace.A, comparison.Clusters, parameters.Exclude);
                var textB = surface is { } surfaceB
                    ? RowTextAnnotations.Create(wordsB!, surfaceB.DisplayMap, PageSpace.B, b.Dpi, comparison.Clusters, parameters.Exclude, settings.Text)
                    : b.Annotate(page.PageNumber, map, PageSpace.B, comparison.Clusters, parameters.Exclude);
                PageTextAnnotations? structureA = null, structureB = null;
                if (projection is { } display)
                {
                    var structures = display.StructuralChanges.Select(s => new DifferenceCluster(s.Id, s.DisplayBounds, 0)).ToArray();
                    structureA = RowTextAnnotations.Create(wordsA!, surface!.DisplayMap, PageSpace.A, a.Dpi, structures, parameters.Exclude, settings.Text);
                    structureB = RowTextAnnotations.Create(wordsB!, surface!.DisplayMap, PageSpace.B, b.Dpi, structures, parameters.Exclude, settings.Text);
                }
                var reportPage = writer.AddComparedPage(page.PageNumber, normalized, comparison, a.Dpi, textA, textB, alignment, correctedB,
                    new(rows, displayA, displayB, structureA, structureB, carry, carry is null ? null : surface, flow?.Adoptions.GetValueOrDefault(page.PageNumber)));
                flow?.Record(reportPage, carry);
            }
            else
            {
                using var image = (page.HasA ? a : b).ReadPage(page.PageNumber);
                flow?.VerifyAndSave(writer, page.PageNumber, page.HasA ? PageSpace.A : PageSpace.B, image.Pixels);
                var reportPage = writer.AddUnpairedPage(page.PageNumber, image.Pixels);
                flow?.Record(reportPage);
            }
            if (page.HasA) writer.AddFontWarnings(page.PageNumber, "A", a.InspectFonts(page.PageNumber));
            if (page.HasB) writer.AddFontWarnings(page.PageNumber, "B", b.InspectFonts(page.PageNumber));
        }
        progress?.Report();
        if (flow is not null && (a.Describe().Sha256 != inputs.A.Sha256 || b.Describe().Sha256 != inputs.B.Sha256))
            throw new CommandLineException("送りの検証中に入力ファイルが変わりました。入力を固定して再実行してください。");
        var completedFlow = flow?.Complete();
        if (anchored is not null) completedFlow = completedFlow! with { AnchoredContent = anchored.Audit() };
        var report = writer.Complete(completedFlow);
        if (!command.NoHtml) HtmlReportWriter.Write(output, report);
        progress?.EndLine();
        return report;
    }

}
