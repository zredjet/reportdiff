namespace ReportDiff.Cli;

/// <summary>ページ単位の進捗。端末だけで同一行を更新し、ログには通常の行を残す。</summary>
internal sealed class ConsoleProgress(TextWriter output, bool quiet, Func<int>? terminalWidth = null) : IDisposable
{
    private int activeColumns;
    private bool useInline = terminalWidth is not null;

    public static ConsoleProgress Create(TextWriter output, bool quiet) => new(output, quiet,
        ReferenceEquals(output, Console.Out) && !Console.IsOutputRedirected
            && Environment.GetEnvironmentVariable("TERM") != "dumb" ? ReadWidth : null);

    public void File(string path)
    {
        if (quiet) return;
        EndLine();
        output.WriteLine("対象: " + CliApplication.OneLine(path));
        output.Flush();
    }

    public void Page(int index, int total, int originalPage, bool selected) =>
        Show($"処理中 {index} / {total} ページ" + (selected ? $"（元ページ {originalPage}）" : ""));

    public void Report(bool directory = false) => Show(directory ? "一覧レポート出力中" : "レポート出力中");

    private void Show(string text)
    {
        if (quiet) return;
        // この行は固定の日本語・全角括弧と ASCII 数字だけ。任意のパスは別行に出す。
        var columns = text.Sum(c => c <= 0x7f ? 1 : 2);
        var width = useInline ? terminalWidth!() : 0;
        if (Math.Max(columns, activeColumns) < width)
        {
            output.Write('\r');
            output.Write(text);
            output.Write(new string(' ', Math.Max(0, activeColumns - columns)));
            activeColumns = columns;
        }
        else
        {
            useInline = false;
            EndLine();
            output.WriteLine(text);
        }
        output.Flush();
    }

    public void EndLine()
    {
        if (activeColumns == 0) return;
        output.WriteLine();
        output.Flush();
        activeColumns = 0;
    }

    public void Dispose() => EndLine();

    private static int ReadWidth()
    {
        try { return Console.WindowWidth; }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { return 0; }
    }
}
