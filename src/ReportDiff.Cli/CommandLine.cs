using System.Globalization;

namespace ReportDiff.Cli;

internal sealed record CompareCommand(string InputA, string InputB, string Output, string? Config,
    string? Profile, int? Dpi, string? Pages, bool SaveAllPages, bool NoHtml, bool Force, bool Quiet)
{
    public bool IsDirectory { get; init; }
    public string? Rules { get; init; }
}

internal static class CommandLine
{
    public const string Usage = "使い方: reportdiff compare <A> <B> --out <dir> [--config <file.yaml>] [--profile normal|strict|loose] [--dpi <n>] [--pages <1-3,5>] [--save-all-pages] [--no-html] [--force] [--quiet] / reportdiff compare-dir <dirA> <dirB> --out <dir> [--rules <rules.yaml>] [compare と同じオプション] / reportdiff --version";

    public static CompareCommand Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("compare" or "compare-dir")) throw new CommandLineException(Usage);
        var inputs = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var positionalOnly = false;
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!positionalOnly && arg == "--") { positionalOnly = true; continue; }
            if (!positionalOnly && arg.StartsWith('-'))
            {
                if (arg is "--save-all-pages" or "--no-html" or "--force" or "--quiet")
                {
                    if (!flags.Add(arg)) throw new CommandLineException($"オプションが重複しています: {arg}");
                }
                else if (arg is "--out" or "--config" or "--profile" or "--dpi" or "--pages" || (args[0] == "compare-dir" && arg == "--rules"))
                {
                    if (values.ContainsKey(arg)) throw new CommandLineException($"オプションが重複しています: {arg}");
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new CommandLineException($"{arg} の値を指定してください。");
                    values.Add(arg, args[i]);
                }
                else throw new CommandLineException($"未知のオプションです: {arg}");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(arg)) throw new CommandLineException("入力ファイルのパスを指定してください。");
                inputs.Add(arg);
            }
        }
        if (inputs.Count != 2) throw new CommandLineException("比較する入力ファイル A と B を 2 つ指定してください。 " + Usage);
        if (!values.TryGetValue("--out", out var output)) throw new CommandLineException("出力先 --out <dir> を指定してください。");
        int? dpi = null;
        if (values.TryGetValue("--dpi", out var text))
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                throw new CommandLineException("--dpi は 72〜1200 の整数を指定してください。");
            dpi = parsed;
        }
        return new(inputs[0], inputs[1], output, values.GetValueOrDefault("--config"), values.GetValueOrDefault("--profile"),
            dpi, values.GetValueOrDefault("--pages"), flags.Contains("--save-all-pages"), flags.Contains("--no-html"),
            flags.Contains("--force"), flags.Contains("--quiet"))
        { IsDirectory = args[0] == "compare-dir", Rules = values.GetValueOrDefault("--rules") };
    }
}

internal sealed class CommandLineException(string message) : Exception(message);
