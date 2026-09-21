using System.Runtime.ExceptionServices;

namespace ReportDiff.Report;

// 画像保存の実行方法だけを変える内部設定。PNG設定・公開設定には含めない。
internal sealed record PageImageExecution
{
    public int MaxDegreeOfParallelism { get; init; } = 2;
    public long MinimumParallelPixels { get; init; } = 1_000_000;
}

internal sealed class PageImageWriteSchedule
{
    private PageImageWriteSchedule(int degree) => Degree = degree;
    public int Degree { get; }

    public static PageImageWriteSchedule Create(int width, int height, PageImageExecution execution,
        int? processorCount = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(execution.MaxDegreeOfParallelism, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(execution.MinimumParallelPixels);
        var processors = processorCount ?? Environment.ProcessorCount;
        ArgumentOutOfRangeException.ThrowIfLessThan(processors, 1);
        var degree = (long)width * height < execution.MinimumParallelPixels ? 1
            : Math.Min(2, Math.Min(execution.MaxDegreeOfParallelism, processors));
        return new(degree);
    }

    public void Run(Action<int> write)
    {
        if (Degree == 1) { write(0); write(1); return; }
        var failures = new Exception?[2];
        // A/BのMatを複製せず共有するため、失敗時も両方の処理終了を待つ。
        // 例外をworker内で保持し、既存のエラー変換に元の型を渡す。
        Parallel.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = Degree }, side =>
        {
            try { write(side); }
            catch (Exception ex) { failures[side] = ex; }
        });
        // 同時に失敗しても、従来の保存順であるAを優先する。
        foreach (var failure in failures)
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
