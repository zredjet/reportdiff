using System.Text;

namespace ReportDiff.Cli;

/// <summary>結果が揃うまでは旧出力を保持し、同じ親ディレクトリ内で置き換える。</summary>
internal sealed class OutputWorkspace : IDisposable
{
    private readonly string destination;
    private readonly bool force;
    public string StagingPath { get; }

    public OutputWorkspace(string output, bool force, params string[] protectedFiles)
    {
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
        this.force = force;
        ValidateDestination();
        var resolved = ResolvePath(destination);
        // 大文字小文字を区別しない Windows / macOS のボリュームでも安全側に判定する。
        foreach (var path in protectedFiles.Append(Directory.GetCurrentDirectory()).Append(AppContext.BaseDirectory))
        {
            var full = Path.GetFullPath(path);
            if (ContainsPath(destination, full) || ContainsPath(resolved, ResolvePath(full)))
                throw new CommandLineException("出力先に入力・設定ファイル、作業ディレクトリ、実行ファイルの保存先を含めることはできません。");
        }
        var parent = Path.GetDirectoryName(destination);
        if (parent is null) throw new CommandLineException("出力先にファイルシステムのルートは指定できません。");
        Directory.CreateDirectory(parent);
        StagingPath = Path.Combine(parent, ".reportdiff-stage-" + Guid.NewGuid().ToString("N"));
    }

    public void Commit()
    {
        ValidateDestination();
        string? backup = null;
        if (Directory.Exists(destination))
        {
            backup = Path.Combine(Path.GetDirectoryName(destination)!, ".reportdiff-backup-" + Guid.NewGuid().ToString("N"));
            Directory.Move(destination, backup);
        }
        try { Directory.Move(StagingPath, destination); }
        catch
        {
            if (backup is not null)
            {
                try { Directory.Move(backup, destination); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new CommandLineException($"出力先の置き換えと復元に失敗しました。旧出力は次に残っています: {backup}");
                }
            }
            throw;
        }
        if (backup is not null)
        {
            try { Directory.Delete(backup, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CommandLineException($"新しい結果は保存しましたが、旧出力の削除に失敗しました。次の保存先を確認してください: {backup}");
            }
        }
    }

    private void ValidateDestination()
    {
        var info = new DirectoryInfo(destination);
        if (info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new CommandLineException("出力先そのものにシンボリックリンクやジャンクションは指定できません。");
        if (File.Exists(destination)) throw new CommandLineException("出力先はファイルではなくディレクトリを指定してください。");
        if (info.Exists && !force && info.EnumerateFileSystemInfos().Any())
            throw new CommandLineException("出力先が空ではありません。別の出力先を指定するか、--force で上書きしてください。");
    }

    private static bool ContainsPath(string directory, string path)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        directory = Path.TrimEndingDirectorySeparator(directory);
        path = Path.TrimEndingDirectorySeparator(path);
        // macOS は「が」と「か + 結合濁点」を同じ名前として扱うため、包含判定もそろえる。
        if (OperatingSystem.IsMacOS())
        {
            directory = directory.Normalize(NormalizationForm.FormC);
            path = path.Normalize(NormalizationForm.FormC);
        }
        return string.Equals(directory, path, comparison)
            || path.StartsWith(Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar, comparison);
    }

    // 親にリンクがある入力も、実際の保存先をたどって出力先との包含関係を調べる。
    private static string ResolvePath(string path, int depth = 0)
    {
        if (depth > 40) throw new CommandLineException("パスのリンクが多すぎます。実際の保存先を指定してください。");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var resolved = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            FileSystemInfo info = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved);
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new CommandLineException("パスのリンク先を確認できません。");
                resolved = ResolvePath(target.FullName, depth + 1);
            }
        }
        return Path.TrimEndingDirectorySeparator(resolved);
    }

    public void Dispose()
    {
        if (Directory.Exists(StagingPath)) Directory.Delete(StagingPath, recursive: true);
    }
}
