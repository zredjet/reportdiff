namespace ReportDiff.Core;

/// <summary>CLIが渡す入力識別子と実効設定。ファイルはCoreで開かず、設定の可変コレクションも保持しない。</summary>
public sealed class AnchoredContentSettings
{
    public ComparisonParameters Comparison { get; }
    public RowOptions Rows { get; }
    public string InputSha256A { get; }
    public string InputSha256B { get; }
    public string TextSettingsSha256 { get; }
    public double MinimumLineOverlap { get; }
    public string? ScopeReason { get; }

    public AnchoredContentSettings(ComparisonParameters firstPage, ComparisonParameters secondPage, RowOptions rows,
        string inputSha256A, string inputSha256B, string textSettingsSha256, double minimumLineOverlap = .5,
        bool selectionLimited = false, bool alignmentEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(firstPage); ArgumentNullException.ThrowIfNull(secondPage); ArgumentNullException.ThrowIfNull(rows);
        rows.Validated(firstPage.Dpi);
        if (!double.IsFinite(minimumLineOverlap) || minimumLineOverlap is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumLineOverlap));
        InputSha256A = Digest(inputSha256A); InputSha256B = Digest(inputSha256B); TextSettingsSha256 = Digest(textSettingsSha256);
        MinimumLineOverlap = minimumLineOverlap; Rows = rows with { };
        Comparison = firstPage with { Exclude = [], Regions = [] };
        ScopeReason = !rows.Enabled || !rows.CarryEnabled ? "rows_disabled"
            : selectionLimited ? "selection_limited" : alignmentEnabled ? "global_alignment_enabled"
            : firstPage.Exclude.Count != 0 || secondPage.Exclude.Count != 0 ? "exclusions_not_supported"
            : firstPage.Regions.Count != 0 || secondPage.Regions.Count != 0 ? "regions_not_supported"
            : !EqualComparison(firstPage, secondPage) ? "page_parameters_differ"
            : !EqualComparison(firstPage, new()) ? "comparison_profile_not_supported" : null;
    }

    private static bool EqualComparison(ComparisonParameters a, ComparisonParameters b) =>
        a.Dpi == b.Dpi && a.Diff == b.Diff && a.Ink == b.Ink && a.Cluster == b.Cluster && a.Move == b.Move;
    private static string Digest(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("入力・文字設定のSHA-256を64桁の16進数で指定してください。");
        return value.ToLowerInvariant();
    }
}
