namespace ReportDiff.Report;

/// <summary>レポートの色指定。HTML に渡せる RGB 6 桁の表記に限定する。</summary>
public static class ReportColor
{
    public const string DefaultCommonColor = "#CCCCCC";

    public static string Normalize(string color)
    {
        if (color is not { Length: 7 } || color[0] != '#' || !color.AsSpan(1).ContainsOnlyHex())
            throw new ArgumentException("色は引用符で囲んだ #RRGGBB 形式の 6 桁の十六進数で指定してください。", nameof(color));
        return color.ToUpperInvariant();
    }

    private static bool ContainsOnlyHex(this ReadOnlySpan<char> text)
    {
        foreach (var c in text) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
