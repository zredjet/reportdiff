namespace ReportDiff.Core;

// 実行方法だけを変える内部設定。比較条件・公開設定・結果JSONには含めない。
internal sealed record FeatureExecution
{
    public int MaxDegreeOfParallelism { get; init; } = Math.Min(4, Environment.ProcessorCount);
    public long MinimumParallelPixels { get; init; } = 1_000_000;
    public long TemporaryMemoryBudget { get; init; } = 96L * 1024 * 1024;
}

internal sealed class FeatureStripeSchedule(int stripeCount, int degree, long workerTemporaryBytes)
{
    public const int StripeRows = 128;
    public int StripeCount => stripeCount;
    public int Degree => degree;
    public long WorkerTemporaryBytes => workerTemporaryBytes;

    public static FeatureStripeSchedule Create(int width, int height, int margin, bool tolerant, bool includeInk,
        FeatureExecution execution)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(margin);
        ArgumentOutOfRangeException.ThrowIfLessThan(execution.MaxDegreeOfParallelism, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(execution.MinimumParallelPixels);
        ArgumentOutOfRangeException.ThrowIfNegative(execution.TemporaryMemoryBudget);
        var count = (int)(((long)height + StripeRows - 1) / StripeRows);
        // 再利用する1枚のfloat3フィルター画像と、Lab変換／インク生成の最大保持量。
        // 入出力の全ページMat、カーネル、OpenCV内部の作業領域・アロケータの保持分は含めない。
        var featureRows = Math.Min((long)height, (long)StripeRows);
        var labRows = Math.Min((long)height, StripeRows + 2L * margin);
        var bytes = checked((long)width * ((tolerant ? 12L * featureRows : 0) + (includeInk ? 25L : 24L) * labRows));
        var degree = 1;
        if ((long)width * height >= execution.MinimumParallelPixels && count >= 4)
        {
            // 各workerに最低2帯を割り当て、一時Matを再利用する。予算不足なら逐次へ戻す。
            var limit = Math.Min(Math.Min(execution.MaxDegreeOfParallelism, Environment.ProcessorCount), count / 2);
            degree = (int)Math.Max(1, Math.Min(limit, execution.TemporaryMemoryBudget / bytes));
        }
        return new(count, degree, bytes);
    }

    public void Run(Action<int, int> fill)
    {
        if (degree == 1) { fill(0, stripeCount); return; }
        // 出力行は重ならない。例外時も開始済みworkerが終了するまで戻らず、
        // 呼び出し元はその後で共有する入力・出力Matを解放できる。
        Parallel.For(0, degree, new ParallelOptions { MaxDegreeOfParallelism = degree }, worker =>
            fill((int)((long)stripeCount * worker / degree), (int)((long)stripeCount * (worker + 1) / degree)));
    }
}
