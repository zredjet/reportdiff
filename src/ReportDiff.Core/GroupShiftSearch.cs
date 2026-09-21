using System.Runtime.CompilerServices;

namespace ReportDiff.Core;

internal readonly record struct GroupShiftResult(int Remaining, int ShiftIndex);

/// <summary>1グループの探索。入力は読み取り専用、出力は担当グループの画素だけ。</summary>
internal static class GroupShiftSearch
{
    public static GroupShiftResult Evaluate(ComparisonFeatureData a, ComparisonFeatureData b, int width, int height,
        DiffOptions options, ReadOnlySpan<GroupRun> runs, int initialCount, (int dx, int dy)[] shifts, byte[] raw)
    {
        var threshold = (float)options.ColorThreshold;
        var tolerance = (float)options.EdgeTolerance;
        var bestCount = initialCount;
        var best = 0;
        for (var index = 1; index < shifts.Length && bestCount != 0; index++)
        {
            var (dx, dy) = shifts[index];
            var candidateCount = 0;
            foreach (var run in runs)
            {
                if (candidateCount >= bestCount) break;
                var row = run.Y * width;
                var sourceRow = Math.Clamp(run.Y - dy, 0, height - 1) * width;
                for (var x = run.Left; x < run.Right && candidateCount < bestCount; x++)
                    if (IsCandidate(a, b, row + x, sourceRow + Math.Clamp(x - dx, 0, width - 1), threshold, tolerance))
                        candidateCount++;
            }
            // 同点は先に評価したずれを維持する。
            if (candidateCount >= bestCount) continue;
            bestCount = candidateCount;
            best = index;
        }
        if (bestCount != 0)
        {
            var (dx, dy) = shifts[best];
            foreach (var run in runs)
            {
                var row = run.Y * width;
                var sourceRow = Math.Clamp(run.Y - dy, 0, height - 1) * width;
                for (var x = run.Left; x < run.Right; x++)
                    if (IsCandidate(a, b, row + x, sourceRow + Math.Clamp(x - dx, 0, width - 1), threshold, tolerance))
                        raw[row + x] = 255;
            }
        }
        return new(bestCount, best);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsCandidate(ComparisonFeatureData a, ComparisonFeatureData b, int pixel, int source, float threshold, float tolerance)
    {
        for (var channel = 0; channel < 3; channel++)
        {
            var ai = pixel * 3 + channel; var bi = source * 3 + channel;
            var limit = tolerance == 0 ? threshold : threshold + tolerance * MathF.Max(a.Contrast[ai], b.Contrast[bi]);
            if (MathF.Abs(a.Values[ai] - b.Values[bi]) > limit) return true;
        }
        return false;
    }
}
