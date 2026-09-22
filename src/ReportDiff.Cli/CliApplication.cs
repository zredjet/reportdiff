using System.Text;
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
            if (command.IsDirectory) return DirectoryComparison.Run(command, output, error);
            using var progress = ConsoleProgress.Create(output, command.Quiet);
            var settings = command.ApplyReportOptions(ConfigurationLoader.Load(ReadConfiguration(command.Config), command.Profile, command.Dpi));
            ReportDocument result;
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                var protectedFiles = command.Config is null ? new[] { command.InputA, command.InputB }
                    : [command.InputA, command.InputB, command.Config];
                using var workspace = new OutputWorkspace(command.Output, command.Force, protectedFiles);
                result = ComparisonRunner.Compare(command, settings, workspace.StagingPath, progress);
                workspace.Commit();
            }
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
            var message = ErrorMessage(ex);
            error.WriteLine("エラー: " + OneLine(message));
            return 2;
        }
    }

    internal static string ErrorMessage(Exception ex) => ex switch
    {
        CommandLineException or ConfigurationException or ImageReadException or PdfReadException
            or PageSelectionException or ReportWriteException => ex.Message,
        IOException or UnauthorizedAccessException => "ファイルを読み書きできません。パス・アクセス権・空き容量を確認してください。",
        ArgumentException or NotSupportedException => "引数またはパスが不正です。入力・設定・出力先を確認してください。",
        _ when HasNativeLoadFailure(ex) => "画像・PDF の実行ライブラリを読み込めません。実行環境とネイティブランタイムの配置を確認してください。",
        _ => "比較処理を完了できません。画像サイズ・DPI・設定値と実行環境を確認してください。"
    };

    internal static string? ReadConfiguration(string? path)
    {
        if (path is null) return null;
        try { return File.ReadAllText(path, new UTF8Encoding(false, true)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new CommandLineException($"設定ファイルを読み込めません。パス・アクセス権・文字コードを確認してください: {path}");
        }
    }

    internal static string OneLine(string text) => string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));
    private static bool HasNativeLoadFailure(Exception ex) => ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
        || (ex.InnerException is not null && HasNativeLoadFailure(ex.InnerException));
}
