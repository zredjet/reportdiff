using System.Runtime.Versioning;
using System.Text;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;

namespace ReportDiff.Cli;

public static class CliApplication
{
    /// <summary>標準入出力の差し替えを可能にし、プロセスを終了せず終了コードを返す。</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args is ["--version"])
            {
                output.WriteLine($"{ReportTool.Current.Name} {ReportTool.Current.Version}");
                return 0;
            }
            var command = CommandLine.Parse(args);
            var settings = ConfigurationLoader.Load(ReadConfiguration(command.Config), command.Profile, command.Dpi);
            ReportDocument result;
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                result = Compare(command, settings);
            else throw new CommandLineException("この実行環境では比較処理に対応していません。");
            if (!command.Quiet)
            {
                var status = result.Summary.Status == "same" ? "相違なし" : "相違あり";
                var path = Path.Combine(command.Output, command.NoHtml ? "result.json" : "report.html");
                output.WriteLine($"{status}: {result.Pages.Count} ページ中 {result.Summary.PagesDifferent} ページ、{result.Summary.Clusters} 箇所 → {OneLine(path)}");
            }
            return result.Summary.Status == "same" ? 0 : 1;
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                CommandLineException or ConfigurationException or ImageReadException or PdfReadException
                    or PageSelectionException or ReportWriteException => ex.Message,
                IOException or UnauthorizedAccessException => "ファイルを読み書きできません。パス・アクセス権・空き容量を確認してください。",
                ArgumentException or NotSupportedException => "引数またはパスが不正です。入力・設定・出力先を確認してください。",
                _ when HasNativeLoadFailure(ex) => "画像・PDF の実行ライブラリを読み込めません。実行環境とネイティブランタイムの配置を確認してください。",
                _ => "比較処理を完了できません。画像サイズ・DPI・設定値と実行環境を確認してください。"
            };
            error.WriteLine("エラー: " + OneLine(message));
            return 2;
        }
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    private static ReportDocument Compare(CompareCommand command, AppSettings settings)
    {
        using var a = new ComparisonInput(command.InputA, settings);
        using var b = new ComparisonInput(command.InputB, settings);
        if (a.Dpi != b.Dpi)
            throw new CommandLineException("PDF と画像を混在させる場合は、設定の dpi と image_dpi を同じ値にしてください。");
        var plan = PagePairing.Create(a.PageCount, b.PageCount, command.Pages);
        var inputs = new ReportInputs(a.Describe(), b.Describe());
        var protectedFiles = command.Config is null ? new[] { command.InputA, command.InputB }
            : [command.InputA, command.InputB, command.Config];
        using var workspace = new OutputWorkspace(command.Output, command.Force, protectedFiles);
        var writer = new ReportWriter(workspace.StagingPath, inputs, settings.ToReportConfiguration(), command.SaveAllPages);
        foreach (var page in plan.Pages)
        {
            if (page.CanCompare)
            {
                using var imageA = a.ReadPage(page.PageNumber);
                using var imageB = b.ReadPage(page.PageNumber);
                using var normalized = PageNormalizer.Normalize(imageA.Pixels, imageB.Pixels);
                var parameters = settings.ForPage(page.PageNumber, a.Dpi);
                using var comparison = PageComparer.Compare(normalized.A, normalized.B, parameters);
                var textA = a.Annotate(page.PageNumber, normalized.OriginalSizeA, comparison.Clusters, parameters.Exclude);
                var textB = b.Annotate(page.PageNumber, normalized.OriginalSizeB, comparison.Clusters, parameters.Exclude);
                writer.AddComparedPage(page.PageNumber, normalized, comparison, a.Dpi, textA, textB);
            }
            else
            {
                using var image = (page.HasA ? a : b).ReadPage(page.PageNumber);
                writer.AddUnpairedPage(page.PageNumber, image.Pixels);
            }
        }
        var report = writer.Complete();
        if (!command.NoHtml) HtmlReportWriter.Write(workspace.StagingPath, report);
        workspace.Commit();
        return report;
    }

    private static string? ReadConfiguration(string? path)
    {
        if (path is null) return null;
        try { return File.ReadAllText(path, new UTF8Encoding(false, true)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new CommandLineException($"設定ファイルを読み込めません。パス・アクセス権・文字コードを確認してください: {path}");
        }
    }

    private static string OneLine(string text) => string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));
    private static bool HasNativeLoadFailure(Exception ex) => ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
        || (ex.InnerException is not null && HasNativeLoadFailure(ex.InnerException));
}
