namespace ReportDiff.Pdf;

/// <summary>入力の元のページ番号を保った対応。片側だけの場合は比較コアへ渡さない。</summary>
public sealed class PagePair
{
    internal PagePair(int pageNumber, bool hasA, bool hasB)
    {
        PageNumber = pageNumber;
        HasA = hasA;
        HasB = hasB;
    }

    public int PageNumber { get; }
    public bool HasA { get; }
    public bool HasB { get; }
    public bool CanCompare => HasA && HasB;
    public string? UnpairedStatus => CanCompare ? null : HasA ? "only_in_a" : "only_in_b";
}

public sealed class PagePairingPlan
{
    internal PagePairingPlan(int pageCountA, int pageCountB, IReadOnlyList<PagePair> pages)
    {
        PageCountA = pageCountA;
        PageCountB = pageCountB;
        Pages = pages;
        Warnings = Array.AsReadOnly<string>(pageCountA == pageCountB ? [] : ["PAGE_COUNT_MISMATCH"]);
    }

    public int PageCountA { get; }
    public int PageCountB { get; }
    public IReadOnlyList<PagePair> Pages { get; }
    public IReadOnlyList<string> Warnings { get; }
}

public static class PagePairing
{
    public static PagePairingPlan Create(int pageCountA, int pageCountB, string? pages = null)
    {
        if (pageCountA < 1 || pageCountB < 1)
            throw new PageSelectionException("入力 A・B のページ数はそれぞれ 1 以上である必要があります。");
        // 片方だけにあるページも選択・報告できるよう、多い方を上限にする。
        var selected = PageSelection.Parse(pages, Math.Max(pageCountA, pageCountB));
        var pairs = selected.Select(page => new PagePair(page, page <= pageCountA, page <= pageCountB)).ToArray();
        return new PagePairingPlan(pageCountA, pageCountB, Array.AsReadOnly(pairs));
    }
}
