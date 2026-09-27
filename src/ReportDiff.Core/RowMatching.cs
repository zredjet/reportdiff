namespace ReportDiff.Core;

public sealed record RowMatch(int A, int B);
internal sealed record RowMatchingResult(IReadOnlyList<IReadOnlyList<RowMatch>> Paths, string? Detail, bool[]? Eligible = null, int Columns = 0);

/// <summary>文字を正規化せず、単語 LCS の対称な一致率から行 LCS の対応を列挙する。</summary>
internal static class RowMatching
{
    internal const int MaximumRows = 2000;
    internal const int MaximumRowCells = 4_000_000;
    internal const int MaximumWordCells = 8_000_000;
    // 同値性の判定前に探索を打ち切る場合も「一意」とはしない。完走できなければ resource_limit。
    internal const int MaximumTraceSteps = 4_000_000;
    internal const int MaximumPaths = 4096;

    public static RowMatchingResult Find(IReadOnlyList<RowLine> a, IReadOnlyList<RowLine> b, RowOptions options, int dpi)
    {
        if (a.Count > MaximumRows || b.Count > MaximumRows || (long)(a.Count + 1) * (b.Count + 1) > MaximumRowCells)
            return new([], "row_dp_limit");
        if (a.Count == 0 || b.Count == 0) return new([], null);
        var width = b.Count + 1;
        var dp = new ushort[checked((a.Count + 1) * width)];
        var matches = new bool[checked(a.Count * b.Count)];
        long wordCells = 0;
        var shift = Units.RoundPixels(options.MaxShiftMm, dpi) + Units.RoundPixels(options.RefineMm, dpi);
        for (var i = a.Count - 1; i >= 0; i--)
        for (var j = b.Count - 1; j >= 0; j--)
        {
            var eligible = false;
            if (Math.Abs(a[i].Baseline - b[j].Baseline) <= shift)
            {
                wordCells += (long)a[i].Words.Count * b[j].Words.Count;
                if (wordCells > MaximumWordCells) return new([], "word_dp_limit");
                eligible = WordMatch(a[i].Words, b[j].Words) >= options.MinWordMatch;
            }
            matches[i * b.Count + j] = eligible;
            dp[i * width + j] = (ushort)Math.Max(eligible ? 1 + dp[(i + 1) * width + j + 1] : 0,
                Math.Max(dp[(i + 1) * width + j], dp[i * width + j + 1]));
        }
        if (dp[0] == 0) return new([], null);
        var result = new List<IReadOnlyList<RowMatch>>();
        var pending = new Stack<Trace>(); pending.Push(new(0, 0, dp[0], null));
        var steps = 0;
        while (pending.TryPop(out var state))
        {
            if (state.Remaining == 0)
            {
                if (result.Count == MaximumPaths) return new([], "hypothesis_trace_limit");
                var path = new RowMatch[dp[0]]; var node = state.Previous;
                for (var n = path.Length - 1; n >= 0; n--) { path[n] = node!.Match; node = node.Previous; }
                result.Add(path); continue;
            }
            // 次の対応対を直接列挙する。同じ組合せに至る縦横スキップの順列は生成しない。
            for (var i = state.A; i < a.Count && dp[i * width + state.B] >= state.Remaining; i++)
            for (var j = state.B; j < b.Count && dp[i * width + j] >= state.Remaining; j++)
            {
                if (++steps > MaximumTraceSteps || pending.Count >= MaximumPaths) return new([], "hypothesis_trace_limit");
                if (matches[i * b.Count + j] && dp[(i + 1) * width + j + 1] == state.Remaining - 1)
                    pending.Push(new(i + 1, j + 1, state.Remaining - 1, new(new(i, j), state.Previous)));
            }
        }
        return new(result, null, matches, b.Count);
    }

    internal static double WordMatch(IReadOnlyList<RowWord> a, IReadOnlyList<RowWord> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var cells = new int[b.Count + 1];
        for (var i = 0; i < a.Count; i++)
        {
            var diagonal = 0;
            for (var j = 0; j < b.Count; j++)
            {
                var previous = cells[j + 1];
                cells[j + 1] = a[i].Text == b[j].Text ? diagonal + 1 : Math.Max(cells[j], previous);
                diagonal = previous;
            }
        }
        return 2.0 * cells[^1] / (a.Count + b.Count);
    }
    private sealed record Node(RowMatch Match, Node? Previous);
    private sealed record Trace(int A, int B, int Remaining, Node? Previous);
}
