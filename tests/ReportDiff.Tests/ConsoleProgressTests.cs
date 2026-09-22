using ReportDiff.Cli;
using Xunit;

namespace ReportDiff.Tests;

public sealed class ConsoleProgressTests
{
    [Fact]
    public void TerminalUpdatesOneLineClearsOldTextAndClosesBeforeNextFile()
    {
        using var output = new StringWriter();
        using (var progress = new ConsoleProgress(output, false, () => 100))
        {
            progress.File("請求書/長い名前の帳票.pdf");
            progress.Page(2, 3, 5, true);
            progress.Report();
            progress.File("次の帳票.pdf");
            progress.Page(1, 1, 1, false);
        }
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Equal("対象: 請求書/長い名前の帳票.pdf", lines[0]);
        Assert.StartsWith("\r処理中 2 / 3 ページ（元ページ 5）\rレポート出力中", lines[1]);
        Assert.EndsWith(new string(' ', 19), lines[1]);
        Assert.Equal("対象: 次の帳票.pdf", lines[2]);
        Assert.Equal("\r処理中 1 / 1 ページ", lines[3]);
        Assert.EndsWith(Environment.NewLine, output.ToString());
    }

    [Theory]
    [InlineData(0)] [InlineData(10)] [InlineData(19)]
    public void RedirectedOrNarrowOutputUsesPlainLines(int width)
    {
        using var output = new StringWriter();
        using var progress = new ConsoleProgress(output, false, width == 0 ? null : () => width);
        progress.Page(1, 2, 1, false); progress.Page(2, 2, 2, false); progress.Report();
        Assert.Equal(string.Join(Environment.NewLine, "処理中 1 / 2 ページ", "処理中 2 / 2 ページ", "レポート出力中", ""), output.ToString());
    }

    [Fact]
    public void ShrinkingTerminalClosesPreviousLineAndFallsBack()
    {
        var width = 100;
        using var output = new StringWriter();
        using var progress = new ConsoleProgress(output, false, () => width);
        progress.Page(1, 2, 1, false);
        width = 10;
        progress.Page(2, 2, 2, false);
        Assert.Equal("\r処理中 1 / 2 ページ" + Environment.NewLine + "処理中 2 / 2 ページ" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void QuietDoesNotWriteOrInspectTerminal()
    {
        using var output = new StringWriter();
        using (var progress = new ConsoleProgress(output, true, () => throw new InvalidOperationException()))
        {
            progress.File("帳票.pdf"); progress.Page(1, 1, 1, false); progress.Report();
        }
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void FileNamesCannotInjectTerminalControls()
    {
        using var output = new StringWriter();
        using var progress = new ConsoleProgress(output, false);
        progress.File("帳票\n\r\u001b[31m.pdf");
        Assert.Equal("対象: 帳票   [31m.pdf" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task SelectedPdfPagesIncludeOriginalNumbersAndUnpairedPage()
    {
        using var files = new DirectoryTestFiles();
        var a = files.Bytes("旧.pdf", PdfFixture.CreatePages((72, 72), (72, 72), (72, 72)));
        var b = files.Bytes("新.pdf", PdfFixture.CreatePages((72, 72), (72, 72)));
        var result = await CliProcess.Run("compare", a, b, "--out", files.Output, "--pages", "2-3", "--dpi", "72", "--no-html");
        Assert.Equal(1, result.Code); Assert.Empty(result.Error);
        Assert.StartsWith(string.Join(Environment.NewLine, "処理中 1 / 2 ページ（元ページ 2）", "処理中 2 / 2 ページ（元ページ 3）", "レポート出力中", ""), result.Output);
        Assert.Contains("相違あり:", result.Output);
    }

    [Fact]
    public async Task BatchClosesFailedPageAndContinuesWithNextFile()
    {
        using var files = new DirectoryTestFiles();
        var pdf = PdfFixture.CreatePages((72, 72), (17000, 72));
        files.Bytes("A/巨大.pdf", pdf); files.Bytes("B/巨大.pdf", pdf);
        files.Image("A/次.png"); files.Image("B/次.png"); files.Image("A/片側.png");
        var result = await files.Run("--dpi", "72", "--no-html");
        Assert.Equal(2, result.Code);
        Assert.Contains("対象: 巨大.pdf" + Environment.NewLine + "処理中 1 / 2 ページ" + Environment.NewLine + "処理中 2 / 2 ページ" + Environment.NewLine + "対象: 次.png", result.Output);
        Assert.DoesNotContain("対象: 片側.png", result.Output);
        Assert.Contains("一覧レポート出力中", result.Output);
        Assert.Contains("巨大.pdf", result.Error);
    }
}
