namespace ReportDiff.Core;

/// <summary>既存の区間索引を分担する境界だけを保持する。画像・区間自体は複製しない。</summary>
internal sealed class GroupCandidateSchedule
{
    private readonly int[] boundaries;
    private GroupCandidateSchedule(int[] boundaries, int degree)
    { this.boundaries = boundaries; Degree = degree; }
    public int Degree { get; }
    public int BlockCount => boundaries.Length - 1;
    public long MemoryBytes => boundaries.LongLength * sizeof(int);
    public (int Start, int End) Block(int block) => (boundaries[block], boundaries[block + 1]);

    public static GroupCandidateSchedule? TryCreate(GroupRunIndex index, int group, int shiftCount,
        GroupSearchExecution execution, long availableBytes, int? processorCount = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(execution.MaxDegreeOfParallelism, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(execution.MinimumCandidatePixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(execution.CandidateBlockPixels, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(execution.CandidateMemoryBudget);
        var processors = processorCount ?? Environment.ProcessorCount;
        ArgumentOutOfRangeException.ThrowIfLessThan(processors, 1);
        var degree = Math.Min(4, Math.Min(processors, execution.MaxDegreeOfParallelism));
        if (degree < 2 || shiftCount < 2 || index.InitialCount(group) == 0
            || index.PixelCount(group) < execution.MinimumCandidatePixels || availableBytes < 3 * sizeof(int)) return null;

        var runs = index.Runs(group);
        var blocks = 0; var pixels = 0;
        foreach (var run in runs)
        {
            pixels += run.Right - run.Left;
            if (pixels < execution.CandidateBlockPixels) continue;
            blocks++; pixels = 0;
        }
        if (pixels != 0) blocks++;
        var bytes = ((long)blocks + 1) * sizeof(int);
        if (blocks < 2 || bytes > Math.Min(availableBytes, execution.CandidateMemoryBudget)) return null;

        // 二度数えることで正確な長さを一度だけ確保。Listの拡張用領域を作らない。
        var boundaries = new int[blocks + 1];
        var position = 1; pixels = 0;
        for (var r = 0; r < runs.Length; r++)
        {
            pixels += runs[r].Right - runs[r].Left;
            if (pixels < execution.CandidateBlockPixels) continue;
            boundaries[position++] = r + 1; pixels = 0;
        }
        if (pixels != 0) boundaries[position] = runs.Length;
        return new(boundaries, Math.Min(degree, blocks));
    }
}
