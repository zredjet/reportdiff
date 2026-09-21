namespace ReportDiff.Core;

internal static class InitialCandidates
{
    public static byte[] Calculate(ComparisonFeatures a, ComparisonFeatures b, int width, int height,
        DiffOptions options, CandidateExecution execution, out int workers)
    {
        var schedule = CandidateRowSchedule.Create(width, height, execution);
        workers = schedule.Degree;
        // マスクは全体で1枚だけ確保する。workerごとのマスクや特徴量の複製は作らない。
        var mask = new byte[checked(width * height)];
        schedule.Run((first, last) =>
            Fill(a.Read(), b.Read(), options, first * width, mask.AsSpan(first * width, (last - first) * width)));
        return mask;
    }

    internal static void Fill(ComparisonFeatureData a, ComparisonFeatureData b, DiffOptions options,
        int firstPixel, Span<byte> output)
    {
        // outputは一度だけゼロ初期化したマスクの担当区間。候補がある画素だけを立てる。
        var threshold = (float)options.ColorThreshold;
        var tolerance = (float)options.EdgeTolerance;
        // 初期候補はずれゼロなので両画像の同じ座標を参照する。
        // float32の演算順、チャネル順、厳密な > と早期終了を参照実装と揃える。
        for (var pixel = 0; pixel < output.Length; pixel++)
        {
            var offset = (firstPixel + pixel) * 3;
            for (var channel = 0; channel < 3; channel++)
            {
                var index = offset + channel;
                var limit = tolerance == 0 ? threshold
                    : threshold + tolerance * MathF.Max(a.Contrast[index], b.Contrast[index]);
                if (MathF.Abs(a.Values[index] - b.Values[index]) > limit)
                {
                    output[pixel] = 255;
                    break;
                }
            }
        }
    }
}
