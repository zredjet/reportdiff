using System.Text;
using ReportDiff.Report;

namespace ReportDiff.Cli;

internal sealed record DirectoryEntry(string RelativePath, bool IsDirectory);
internal sealed record DirectoryPair(string Id, string RelativePath, string? A, string? B);
internal sealed record DirectoryPlan(string RootA, string RootB, IReadOnlyList<DirectoryPair> Pairs,
    IReadOnlyList<DirectoryIgnoredFile> Ignored)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    public static DirectoryPlan Create(string a, string b, string output,
        Func<string, IEnumerable<DirectoryEntry>>? enumerate = null)
    {
        a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        ValidateRoot(a); ValidateRoot(b);
        ValidateBoundaries(a, b, Path.GetFullPath(output));
        // 列挙を完了してから比較を開始する。アクセス失敗を空のフォルダと見なさない。
        var scan = enumerate ?? Enumerate;
        return Pair(a, b, scan(a), scan(b));
    }

    internal static DirectoryPlan Pair(string a, string b, IEnumerable<DirectoryEntry> entriesA, IEnumerable<DirectoryEntry> entriesB)
    {
        static Dictionary<string, DirectoryEntry> Index(IEnumerable<DirectoryEntry> entries, string side)
        {
            var result = new Dictionary<string, DirectoryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                if (!result.TryAdd(Key(entry.RelativePath), entry))
                    throw new CommandLineException($"入力 {side} の相対パスが正規化後に重複しています: {entry.RelativePath}");
            return result;
        }
        var left = Index(entriesA, "A"); var right = Index(entriesB, "B");
        foreach (var (key, entry) in left)
            if (right.TryGetValue(key, out var other) && entry.IsDirectory != other.IsDirectory)
                throw new CommandLineException($"ファイルとフォルダの名前が衝突しています: {entry.RelativePath} / {other.RelativePath}");
        var ignored = new List<DirectoryIgnoredFile>();
        foreach (var (side, entries) in new[] { ("A", left), ("B", right) })
            foreach (var entry in entries.Values.Where(x => !x.IsDirectory && !IsTarget(x)))
                ignored.Add(new(side, entry.RelativePath, "対応拡張子ではないため対象外（内容は未検査）"));
        var keys = left.Where(x => IsTarget(x.Value)).Select(x => x.Key)
            .Concat(right.Where(x => IsTarget(x.Value)).Select(x => x.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var pairs = new List<DirectoryPair>();
        foreach (var key in keys)
        {
            var pathA = left.GetValueOrDefault(key)?.RelativePath;
            var pathB = right.GetValueOrDefault(key)?.RelativePath;
            // 代表表記も A/B の順序に依存させない。
            var representative = new[] { pathA, pathB }.Where(x => x is not null).Select(x => Key(x!)).Order(StringComparer.Ordinal).First();
            pairs.Add(new($"f{pairs.Count + 1:D6}", representative, pathA, pathB));
        }
        return new(a, b, pairs, ignored.OrderBy(x => x.Side, StringComparer.Ordinal)
            .ThenBy(x => Key(x.RelativePath), StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool IsTarget(DirectoryEntry entry) => !entry.IsDirectory && Extensions.Contains(Path.GetExtension(entry.RelativePath));
    internal static string Key(string path) => path.Replace(Path.DirectorySeparatorChar, '/').Normalize(NormalizationForm.FormC);
    private static void ValidateRoot(string path)
    {
        RejectLink(new DirectoryInfo(path));
        if (!Directory.Exists(path)) throw new CommandLineException($"入力フォルダを確認してください: {path}");
    }
    private static void RejectLink(FileSystemInfo info)
    {
        if (info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new CommandLineException($"入力のリンク・ジャンクション・再解析ポイントは使用できません: {info.FullName}");
    }
    private static IEnumerable<DirectoryEntry> Enumerate(string root)
    {
        var pending = new Stack<DirectoryInfo>(); pending.Push(new(root));
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        while (pending.TryPop(out var directory))
            foreach (var info in directory.EnumerateFileSystemInfos("*", options))
            {
                RejectLink(info);
                var isDirectory = info.Attributes.HasFlag(FileAttributes.Directory);
                yield return new(KeySeparators(Path.GetRelativePath(root, info.FullName)), isDirectory);
                if (isDirectory) pending.Push(new(info.FullName));
            }
    }
    private static string KeySeparators(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
    private static void ValidateBoundaries(string a, string b, string output)
    {
        static bool Contains(string parent, string child) => OutputWorkspace.ContainsPath(parent, child);
        foreach (var (left, right, destination) in new[] { (a, b, output),
            (OutputWorkspace.ResolvePath(a), OutputWorkspace.ResolvePath(b), OutputWorkspace.ResolvePath(output)) })
        {
            var same = Contains(left, right) && Contains(right, left);
            if (!same && (Contains(left, right) || Contains(right, left)))
                throw new CommandLineException("入力 A/B の一方がもう一方を含むフォルダは指定できません。");
            if (Contains(left, destination) || Contains(destination, left) || Contains(right, destination) || Contains(destination, right))
                throw new CommandLineException("出力先と入力フォルダは、同一または一方が他方を含む場所には指定できません。");
        }
    }
}
