using ReportDiff.Cli;
using Xunit;

namespace ReportDiff.Tests;

public sealed class DirectoryPlanTests
{
    [Fact]
    public void PairingNormalizesCaseAndUnicodeButKeepsOriginalPathsAndStableIds()
    {
        DirectoryEntry[] a = [new("z.PNG", false), new("帳票/が.pdf", false), new("readme.txt", false), new("空", true)];
        DirectoryEntry[] b = [new("帳票/か\u3099.PDF", false), new("a.jpg", false), new("Z.png", false), new("metadata", false)];
        var plan = DirectoryPlan.Pair("a", "b", a, b);
        Assert.Equal(new[] { "f000001", "f000002", "f000003" }, plan.Pairs.Select(x => x.Id));
        Assert.Equal("a.jpg", plan.Pairs[0].B); Assert.Null(plan.Pairs[0].A);
        Assert.Equal("帳票/が.pdf", plan.Pairs[2].A); Assert.Equal("帳票/か\u3099.PDF", plan.Pairs[2].B);
        Assert.Equal(2, plan.Ignored.Count);
        var reverse = DirectoryPlan.Pair("b", "a", b.Reverse(), a.Reverse());
        Assert.Equal(plan.Pairs.Select(x => (x.Id, x.RelativePath)), reverse.Pairs.Select(x => (x.Id, x.RelativePath)));
    }

    [Theory]
    [InlineData("FILE.txt", "file.txt", false)]
    [InlineData("が", "か\u3099", true)]
    [InlineData("帳票.PDF", "帳票.pdf", false)]
    public void SameSideCollisionsIncludeIgnoredFilesAndDirectories(string x, string y, bool directory)
    {
        Assert.Throws<CommandLineException>(() => DirectoryPlan.Pair("a", "b", [new(x, directory), new(y, directory)], []));
        Assert.Throws<CommandLineException>(() => DirectoryPlan.Pair("a", "b", [], [new(x, directory), new(y, directory)]));
    }

    [Theory]
    [InlineData("document.pdf")]
    [InlineData("ignored.txt")]
    public void FileDirectoryCollisionAcrossSidesIsPreflightFailure(string path) =>
        Assert.Throws<CommandLineException>(() => DirectoryPlan.Pair("a", "b", [new(path, false)], [new(path.ToUpperInvariant(), true)]));

    [Fact]
    public void AllSupportedExtensionsAndHiddenDirectoriesAreIncluded()
    {
        using var files = new DirectoryTestFiles();
        foreach (var ext in new[] { "PDF", "png", "JPG", "jpeg", "bmp", "tif", "TIFF" }) files.Text($"A/.hidden/帳票.{ext}", "未検査");
        files.Text("A/.隠し", "ignored"); files.Text("B/data.csv", "ignored");
        var plan = DirectoryPlan.Create(files.A, files.B, files.Output);
        Assert.Equal(7, plan.Pairs.Count); Assert.Equal(2, plan.Ignored.Count);
        Assert.All(plan.Pairs, x => { Assert.StartsWith(".hidden/", x.A); Assert.Null(x.B); });
    }

    [Theory]
    [InlineData("same")]
    [InlineData("descendant")]
    [InlineData("ancestor")]
    public void OutputCannotOverlapEitherInput(string kind)
    {
        using var files = new DirectoryTestFiles();
        var output = kind switch { "same" => files.A, "descendant" => Path.Combine(files.B, "結果"), _ => files.Root };
        Assert.Throws<CommandLineException>(() => DirectoryPlan.Create(files.A, files.B, output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingRootOrFileRootReportsDirectoryError(bool file)
    {
        using var files = new DirectoryTestFiles();
        var input = file ? files.Text("file.txt", "input") : Path.Combine(files.Root, "missing");
        Assert.Contains("入力フォルダ", Assert.Throws<CommandLineException>(() => DirectoryPlan.Create(input, files.B, files.Output)).Message);
    }

    [Fact]
    public void EqualInputsAreAllowedButNestedInputsAreRejected()
    {
        using var files = new DirectoryTestFiles();
        Assert.Empty(DirectoryPlan.Create(files.A, files.A, files.Output).Pairs);
        Directory.CreateDirectory(Path.Combine(files.A, "child"));
        Assert.Throws<CommandLineException>(() => DirectoryPlan.Create(files.A, Path.Combine(files.A, "child"), files.Output));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("dangling")]
    [InlineData("root")]
    public void LinksAreRejectedEvenWhenIgnoredOrDangling(string kind)
    {
        // Windows のリンク作成権限・ジャンクションは実機確認として別管理。
        if (OperatingSystem.IsWindows()) return;
        using var files = new DirectoryTestFiles();
        var path = Path.Combine(files.A, "ignored.txt");
        if (kind == "directory") Directory.CreateSymbolicLink(path, files.B);
        else if (kind == "root") Directory.CreateSymbolicLink(Path.Combine(files.Root, "alias"), files.A);
        else File.CreateSymbolicLink(path, files.Text("target.txt", "x") + (kind == "dangling" ? "missing" : ""));
        Assert.Throws<CommandLineException>(() => DirectoryPlan.Create(kind == "root" ? Path.Combine(files.Root, "alias") : files.A, files.B, files.Output));
    }

    [Fact]
    public void AncestorAliasesAreResolvedForBoundaryChecks()
    {
        if (OperatingSystem.IsWindows()) return;
        using var files = new DirectoryTestFiles();
        var alias = Path.Combine(files.Root, "alias"); Directory.CreateSymbolicLink(alias, files.A);
        Directory.CreateDirectory(Path.Combine(files.A, "child"));
        Assert.Throws<CommandLineException>(() => DirectoryPlan.Create(files.A, files.B, Path.Combine(alias, "result")));
        Assert.Empty(DirectoryPlan.Create(Path.Combine(alias, "child"), files.B, files.Output).Pairs);
    }
}
