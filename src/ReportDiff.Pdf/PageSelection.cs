using System.Globalization;

namespace ReportDiff.Pdf;

public static class PageSelection
{
    /// <summary>1 始まりのページ指定を昇順・重複なしにする。null は全ページ。</summary>
    public static IReadOnlyList<int> Parse(string? expression, int pageCount)
    {
        if (pageCount < 1) throw new PageSelectionException("ページ数は 1 以上である必要があります。");
        if (expression is null) return Array.AsReadOnly(Enumerable.Range(1, pageCount).ToArray());
        var selected = new SortedSet<int>();
        foreach (var item in expression.Split(','))
        {
            var endpoints = item.Split('-');
            if (endpoints.Length is < 1 or > 2) throw InvalidSyntax();
            var first = ParseNumber(endpoints[0]);
            var last = endpoints.Length == 2 ? ParseNumber(endpoints[1]) : first;
            if (first > last) throw new PageSelectionException("--pages の範囲は小さいページ番号から指定してください。");
            // 範囲を展開する前に上限を調べ、巨大な不正指定でも早く失敗させる。
            if (last > pageCount)
                throw new PageSelectionException($"--pages のページ番号は 1〜{pageCount} を指定してください: {last}");
            for (var page = first; ; page++)
            {
                selected.Add(page);
                if (page == last) break; // int.MaxValue の単一指定でもオーバーフローさせない。
            }
        }
        return Array.AsReadOnly(selected.ToArray());
    }

    private static int ParseNumber(string text)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
            throw InvalidSyntax();
        return value;
    }

    private static PageSelectionException InvalidSyntax() =>
        new("--pages は 1 始まりの整数または範囲をカンマで区切って指定してください（例: 1-2,5）。");
}

public sealed class PageSelectionException(string message) : Exception(message);
