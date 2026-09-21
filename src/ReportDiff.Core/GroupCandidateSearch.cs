namespace ReportDiff.Core;

/// <summary>ずれ候補は優先順に逐次評価し、1候補内の画素だけを分担する。</summary>
internal static class GroupCandidateSearch
{
    public static GroupShiftResult Evaluate(ComparisonFeatures ownedA, ComparisonFeatures ownedB,
        int width, int height, DiffOptions options, GroupRunIndex index, int group,
        (int dx, int dy)[] shifts, byte[] raw, GroupCandidateSchedule schedule,
        Action<ComparisonFeatures, ComparisonFeatures, int, int, int>? beforeWorker = null)
    {
        var threshold = (float)options.ColorThreshold;
        var tolerance = (float)options.EdgeTolerance;
        var bestCount = index.InitialCount(group); var best = 0;
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = schedule.Degree };
        for (var shiftIndex = 1; shiftIndex < shifts.Length && bestCount != 0; shiftIndex++)
        {
            var (dx, dy) = shifts[shiftIndex];
            var cutoff = bestCount; var count = 0; var next = -1;
            Parallel.For(0, schedule.Degree, parallelOptions, (worker, loop) =>
            {
                // Spanはlambdaへ捕捉せず、所有Mat・区間索引からworker内で取得する。
                beforeWorker?.Invoke(ownedA, ownedB, group, shiftIndex, worker);
                var a = ownedA.Read(); var b = ownedB.Read(); var runs = index.Runs(group);
                while (!loop.ShouldExitCurrentIteration && Volatile.Read(ref count) < cutoff)
                {
                    var block = Interlocked.Increment(ref next);
                    if (block >= schedule.BlockCount) break;
                    var local = 0; var remaining = cutoff - Volatile.Read(ref count);
                    var (start, end) = schedule.Block(block);
                    for (var r = start; r < end && local < remaining; r++)
                    {
                        var run = runs[r]; var row = run.Y * width;
                        var sourceRow = Math.Clamp(run.Y - dy, 0, height - 1) * width;
                        for (var x = run.Left; x < run.Right && local < remaining; x++)
                            if (GroupShiftSearch.IsCandidate(a, b, row + x,
                                sourceRow + Math.Clamp(x - dx, 0, width - 1), threshold, tolerance)) local++;
                    }
                    // 同じ画素は一度しか数えない。共有値の操作はブロック境界だけ。
                    Interlocked.Add(ref count, local);
                }
            });
            // 上限未満なら全ブロックを走査済み。上限以上の途中結果は採用しない。
            if (count >= bestCount) continue;
            bestCount = count; best = shiftIndex;
        }
        if (bestCount != 0)
        {
            var a = ownedA.Read(); var b = ownedB.Read(); var (dx, dy) = shifts[best];
            foreach (var run in index.Runs(group))
            {
                var row = run.Y * width;
                var sourceRow = Math.Clamp(run.Y - dy, 0, height - 1) * width;
                for (var x = run.Left; x < run.Right; x++)
                    if (GroupShiftSearch.IsCandidate(a, b, row + x,
                        sourceRow + Math.Clamp(x - dx, 0, width - 1), threshold, tolerance)) raw[row + x] = 255;
            }
        }
        return new(bestCount, best);
    }
}
