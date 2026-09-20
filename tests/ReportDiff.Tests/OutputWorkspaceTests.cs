using ReportDiff.Cli;
using System.Text;
using Xunit;

namespace ReportDiff.Tests;

public sealed class OutputWorkspaceTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMacOS => OperatingSystem.IsMacOS();
    private const string WindowsLinks = "Windows のリンク・ジャンクションは権限を含めて実機確認する。";

    [Fact]
    public void FailedReplacementRestoresOldOutput()
    {
        using var files = new WorkspaceFiles();
        using (var workspace = new OutputWorkspace(files.Output, true))
        {
            // 保存元が失われた場合を作り、旧出力の退避後の Move 失敗を通す。
            Assert.Throws<DirectoryNotFoundException>(() => workspace.Commit());
            Assert.Equal("旧結果", File.ReadAllText(files.Sentinel));
        }
        Assert.Equal(new[] { files.Output }, Directory.GetFileSystemEntries(files.Root));
    }

    [Fact]
    public void NewFilesInEmptyDestinationAreNotOverwrittenWithoutForce()
    {
        using var files = new WorkspaceFiles();
        File.Delete(files.Sentinel);
        using (var workspace = new OutputWorkspace(files.Output, false))
        {
            Directory.CreateDirectory(workspace.StagingPath);
            File.WriteAllText(Path.Combine(workspace.StagingPath, "result.json"), "新結果");
            File.WriteAllText(files.Sentinel, "後から保存");
            Assert.Throws<CommandLineException>(() => workspace.Commit());
        }
        Assert.Equal("後から保存", File.ReadAllText(files.Sentinel));
        Assert.Equal(new[] { files.Output }, Directory.GetFileSystemEntries(files.Root));
    }

    [Fact]
    public void CurrentDirectoryExecutableDirectoryAndRootAreProtected()
    {
        foreach (var path in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory, Path.GetPathRoot(Path.GetTempPath())! })
            Assert.Throws<CommandLineException>(() => new OutputWorkspace(path, true));
    }

    [Fact(Skip = "macOS のファイル名の Unicode 正規化を確認する。", SkipUnless = nameof(IsMacOS))]
    public void MacUnicodeNormalizationCannotBypassInputProtection()
    {
        using var files = new WorkspaceFiles();
        var output = Path.Combine(files.Root, "結果が入る");
        Directory.CreateDirectory(output);
        var input = Path.Combine(output, "入力.txt");
        File.WriteAllText(input, "保持");
        var decomposedInput = input.Normalize(NormalizationForm.FormD);
        Assert.True(File.Exists(decomposedInput));
        Assert.Throws<CommandLineException>(() => new OutputWorkspace(output, true, decomposedInput));
        Assert.Equal("保持", File.ReadAllText(input));
    }

    [Theory(Skip = WindowsLinks, SkipWhen = nameof(IsWindows))]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputLinkIsRejectedEvenWhenDangling(bool dangling)
    {
        using var files = new WorkspaceFiles();
        var link = Path.Combine(files.Root, "出力リンク");
        Directory.CreateSymbolicLink(link, dangling ? Path.Combine(files.Root, "不在") : files.Output);
        Assert.Throws<CommandLineException>(() => new OutputWorkspace(link, true));
        Assert.Equal("旧結果", File.ReadAllText(files.Sentinel));
    }

    [Fact(Skip = WindowsLinks, SkipWhen = nameof(IsWindows))]
    public void InputLinkAndLinkedParentCannotBypassContainmentCheck()
    {
        using var files = new WorkspaceFiles();
        var inputLink = Path.Combine(files.Root, "入力リンク");
        File.CreateSymbolicLink(inputLink, files.Sentinel);
        Assert.Throws<CommandLineException>(() => new OutputWorkspace(files.Output, true, inputLink));
        var parentLink = Path.Combine(files.Root, "親リンク");
        Directory.CreateSymbolicLink(parentLink, files.Root);
        Assert.Throws<CommandLineException>(() => new OutputWorkspace(Path.Combine(parentLink, "結果"), true, files.Sentinel));
        Assert.Equal("旧結果", File.ReadAllText(files.Sentinel));
    }

    [Fact(Skip = WindowsLinks, SkipWhen = nameof(IsWindows))]
    public void ReplacementRemovesLinksWithoutDeletingTheirTargets()
    {
        using var files = new WorkspaceFiles();
        var external = Path.Combine(files.Root, "外部");
        Directory.CreateDirectory(external);
        var protectedFile = Path.Combine(external, "保持.txt");
        File.WriteAllText(protectedFile, "保持");
        Directory.CreateSymbolicLink(Path.Combine(files.Output, "リンク"), external);
        using (var workspace = new OutputWorkspace(files.Output, true, protectedFile))
        {
            Directory.CreateDirectory(workspace.StagingPath);
            File.WriteAllText(Path.Combine(workspace.StagingPath, "result.json"), "新結果");
            workspace.Commit();
        }
        Assert.Equal("保持", File.ReadAllText(protectedFile));
        Assert.Equal("新結果", File.ReadAllText(Path.Combine(files.Output, "result.json")));
        Assert.False(File.Exists(files.Sentinel));
        Assert.Empty(Directory.EnumerateDirectories(files.Root, ".reportdiff-*"));
    }

    [Fact(Skip = WindowsLinks, SkipWhen = nameof(IsWindows))]
    public void OrdinaryLinkedParentCanBeUsedForOutput()
    {
        using var files = new WorkspaceFiles();
        var parentLink = Path.Combine(files.Root, "親リンク");
        Directory.CreateSymbolicLink(parentLink, files.Root);
        using (var workspace = new OutputWorkspace(Path.Combine(parentLink, "新結果"), false, files.Sentinel))
        {
            Directory.CreateDirectory(workspace.StagingPath);
            File.WriteAllText(Path.Combine(workspace.StagingPath, "result.json"), "新結果");
            workspace.Commit();
        }
        Assert.Equal("旧結果", File.ReadAllText(files.Sentinel));
        Assert.True(File.Exists(Path.Combine(files.Root, "新結果/result.json")));
    }

    private sealed class WorkspaceFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "reportdiff-workspace-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Root, "結果");
        public string Sentinel => Path.Combine(Output, "旧結果.txt");
        public WorkspaceFiles() { Directory.CreateDirectory(Output); File.WriteAllText(Sentinel, "旧結果"); }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
