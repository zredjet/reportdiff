namespace ReportDiff.Core;

// 実行方法だけを変える内部設定。比較条件・公開設定・結果JSONには含めない。
internal sealed record GroupSearchExecution
{
    public int MaxDegreeOfParallelism { get; init; } = Math.Min(4, Environment.ProcessorCount);
    public long MinimumParallelWork { get; init; } = 250_000;
    public long RunMemoryBudget { get; init; } = GroupRunIndex.DefaultMemoryBudget;
    public long MinimumCandidatePixels { get; init; } = 1_000_000;
    public int CandidateBlockPixels { get; init; } = 16_384;
    public long CandidateMemoryBudget { get; init; } = 1024 * 1024;
    // 失敗時の所有関係の試験用。製品の呼び出しでは指定しない。
    public Action<ComparisonFeatures, ComparisonFeatures, int, int, int>? BeforeCandidateWorker { get; init; }
}

internal sealed class GroupSearchSchedule(int[] groups, int degree)
{
    public int Count => groups.Length;
    public int Degree => degree;

    public static GroupSearchSchedule Create(GroupRunIndex index, int shiftCount, GroupSearchExecution execution,
        bool[]? completed = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(execution.MaxDegreeOfParallelism, 1);
        var groups = Enumerable.Range(1, index.GroupCount - 1)
            .Where(g => index.InitialCount(g) != 0 && completed?[g] != true).ToArray();
        long Work(int group) => ((long)index.PixelCount(group) + 4L * index.Runs(group).Length) * (shiftCount - 1);
        long total = 0, largest = 0;
        foreach (var group in groups)
        {
            var work = Work(group);
            total += work; largest = Math.Max(largest, work);
        }
        // 最大グループ以外にも分担する仕事がある場合だけ開始する。
        var degree = groups.Length < 2 || total - largest < execution.MinimumParallelWork
            ? 1 : Math.Min(groups.Length, Math.Min(execution.MaxDegreeOfParallelism, Environment.ProcessorCount));
        if (degree > 1)
            Array.Sort(groups, (a, b) => { var order = Work(b).CompareTo(Work(a)); return order == 0 ? a.CompareTo(b) : order; });
        return new(groups, degree);
    }

    public void Run(Action<int> evaluate)
    {
        if (degree == 1)
        {
            foreach (var group in groups) evaluate(group);
            return;
        }
        var next = -1;
        // グループごとに仕事を取得する。画素ループにはロックも共有カウンターも置かない。
        // Parallel.Forは例外時も実行中のworkerを待つ。呼び出し側のMatはその後に破棄する。
        Parallel.For(0, degree, new ParallelOptions { MaxDegreeOfParallelism = degree }, (_, loop) =>
        {
            int position;
            while (!loop.ShouldExitCurrentIteration && (position = Interlocked.Increment(ref next)) < groups.Length)
                evaluate(groups[position]);
        });
    }
}
