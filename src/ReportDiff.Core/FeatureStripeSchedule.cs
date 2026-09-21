namespace ReportDiff.Core;

// 実行方法だけを変える内部設定。比較条件・公開設定・結果JSONには含めない。
internal sealed record FeatureExecution
{
    public int MaxDegreeOfParallelism { get; init; } = Math.Min(4, Environment.ProcessorCount);
    public long MinimumParallelPixels { get; init; } = 1_000_000;
    public long TemporaryMemoryBudget { get; init; } = 96L * 1024 * 1024;
}

internal sealed class FeatureStripeSchedule
{
    public const int DefaultStripeRows = 128;
    private const int ReusedStripeRows = 64;
    private FeatureStripeSchedule(int stripeRows, int stripeCount, int degree, long workerTemporaryBytes)
    {
        StripeRows = stripeRows;
        StripeCount = stripeCount;
        Degree = degree;
        WorkerTemporaryBytes = workerTemporaryBytes;
    }
    public int StripeRows { get; }
    public int StripeCount { get; }
    public int Degree { get; }
    public long WorkerTemporaryBytes { get; }
    public bool ReuseLab => StripeRows == ReusedStripeRows;

    public static FeatureStripeSchedule Create(int width, int height, int margin, bool tolerant, bool includeInk,
        FeatureExecution execution, int? processorCount = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(margin);
        ArgumentOutOfRangeException.ThrowIfLessThan(execution.MaxDegreeOfParallelism, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(execution.MinimumParallelPixels);
        ArgumentOutOfRangeException.ThrowIfNegative(execution.TemporaryMemoryBudget);
        var processors = processorCount ?? Environment.ProcessorCount;
        ArgumentOutOfRangeException.ThrowIfLessThan(processors, 1);
        var current = CreateForRows(DefaultStripeRows, width, height, margin, tolerant, includeInk, execution, processors);
        // 逐次や既に4workerの条件へ適用を広げない。帯数だけでなく一時Mat予算も比較する。
        if (current.Degree >= 4 || Math.Min(execution.MaxDegreeOfParallelism, processors) < 4)
            return current;
        var candidate = CreateForRows(ReusedStripeRows, width, height, margin, tolerant, includeInk,
            execution with { MaxDegreeOfParallelism = 4 }, processors);
        return candidate.Degree == 4 ? candidate : current;
    }

    private static FeatureStripeSchedule CreateForRows(int stripeRows, int width, int height, int margin,
        bool tolerant, bool includeInk, FeatureExecution execution, int processors)
    {
        var count = (int)(((long)height + stripeRows - 1) / stripeRows);
        // 再利用する1枚のfloat3フィルター画像と、Lab変換／インク生成の最大保持量。
        // 入出力の全ページMat、カーネル、OpenCV内部の作業領域・アロケータの保持分は含めない。
        // 64行ではL・背景を帯末尾で解放し、次のLab変換まで持ち越さない。
        // 変換時の24Lが最大。インク計算時のLab+L+中央背景は16L+4S <= 24L。
        var featureRows = Math.Min((long)height, stripeRows);
        var labRows = Math.Min((long)height, stripeRows + 2L * margin);
        var labBytes = includeInk && stripeRows == DefaultStripeRows ? 25L : 24L;
        var bytes = checked((long)width * ((tolerant ? 12L * featureRows : 0) + labBytes * labRows));
        var degree = 1;
        if ((long)width * height >= execution.MinimumParallelPixels && count >= 4)
        {
            // 各workerに最低2帯を割り当て、一時Matを再利用する。予算不足なら逐次へ戻す。
            var limit = Math.Min(Math.Min(execution.MaxDegreeOfParallelism, processors), count / 2);
            degree = (int)Math.Max(1, Math.Min(limit, execution.TemporaryMemoryBudget / bytes));
        }
        return new(stripeRows, count, degree, bytes);
    }

    public void Run(Action<int, int> fill)
    {
        if (Degree == 1) { fill(0, StripeCount); return; }
        // 出力行は重ならない。例外時も開始済みworkerが終了するまで戻らず、
        // 呼び出し元はその後で共有する入力・出力Matを解放できる。
        Parallel.For(0, Degree, new ParallelOptions { MaxDegreeOfParallelism = Degree }, worker =>
            fill((int)((long)StripeCount * worker / Degree), (int)((long)StripeCount * (worker + 1) / Degree)));
    }
}
