using ReportDiff.Cli;
using Xunit;

namespace ReportDiff.Tests;

/// <summary>入力変更の検出と、Windowsが変更自体を拒否する場合を区別する。</summary>
internal sealed class InputMutationAttempt(string path)
{
    private readonly byte[] original = File.ReadAllBytes(path);
    private IOException? blocked;
    internal bool Attempted { get; private set; }
    private bool Changed { get; set; }

    internal void AppendPdfComment()
    {
        Assert.False(Attempted);
        Attempted = true;
        try
        {
            File.AppendAllText(path, "\n% changed during verification\n");
            Changed = true;
        }
        catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 32)
        {
            // FileShare.Readが変更を防いだ場合も、例外を処理へ返して旧出力の保護を検査する。
            // この場合を「変更済み入力のSHA検出」に数えない。他のI/O失敗は許容しない。
            blocked = error;
            Assert.Equal(original, File.ReadAllBytes(path));
            throw;
        }
    }

    internal void AssertCliError(int code, string error)
    {
        AssertAttempt();
        Assert.Equal(2, code);
        Assert.Contains(Changed ? "入力ファイルが変わりました" : "ファイルを読み書きできません。", error);
    }

    internal void AssertException(Exception? error)
    {
        AssertAttempt();
        if (Changed)
            Assert.Contains("入力ファイルが変わりました", Assert.IsType<CommandLineException>(error).Message);
        else
            Assert.Same(blocked, error);
    }

    private void AssertAttempt()
    {
        Assert.True(Attempted);
        if (Changed)
        {
            Assert.Null(blocked);
            Assert.False(original.SequenceEqual(File.ReadAllBytes(path)));
        }
        else
        {
            Assert.True(OperatingSystem.IsWindows());
            Assert.NotNull(blocked);
            Assert.Equal(32, blocked.HResult & 0xffff);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
    }
}
