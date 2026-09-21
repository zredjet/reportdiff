namespace ReportDiff.Core;

// 初期候補の実行方法だけを変える内部設定。比較条件・公開設定には含めない。
internal sealed record CandidateExecution
{
    public int MaxDegreeOfParallelism { get; init; } = Math.Min(4, Environment.ProcessorCount);
    public long MinimumParallelPixels { get; init; } = 1_000_000;
}

internal sealed class CandidateRowSchedule(int height, int degree)
{
    public int Degree => degree;

    public static CandidateRowSchedule Create(int width, int height, CandidateExecution execution)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(execution.MaxDegreeOfParallelism, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(execution.MinimumParallelPixels);
        var degree = (long)width * height < execution.MinimumParallelPixels ? 1
            : Math.Min(height, Math.Min(execution.MaxDegreeOfParallelism, Environment.ProcessorCount));
        return new(height, degree);
    }

    public void Run(Action<int, int> fill)
    {
        if (degree == 1) { fill(0, height); return; }
        // 行区間は重ならない。例外時も開始済みworkerの終了を待つため、
        // 呼び出し元は戻った後で読み取り専用の特徴量Matを解放できる。
        Parallel.For(0, degree, new ParallelOptions { MaxDegreeOfParallelism = degree }, worker =>
            fill((int)((long)height * worker / degree), (int)((long)height * (worker + 1) / degree)));
    }
}
