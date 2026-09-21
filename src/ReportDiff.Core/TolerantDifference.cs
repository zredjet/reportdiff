using OpenCvSharp;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ReportDiff.Core;

/// <summary>RawMask の所有権を持つ。呼び出し側で破棄する。</summary>
public sealed class RawDifference(Mat rawMask, int absorbedGroups, int maxShiftPx) : IDisposable
{
    public Mat RawMask { get; } = rawMask;
    public int AbsorbedGroups { get; } = absorbedGroups;
    public int MaxShiftPx { get; } = maxShiftPx;
    public void Dispose() => RawMask.Dispose();
}

public static class TolerantDifference
{
    public static RawDifference Calculate(Mat a, Mat b, ComparisonParameters parameters) =>
        Calculate(a, b, parameters, true);

    internal static RawDifference Calculate(Mat a, Mat b, ComparisonParameters parameters,
        bool useGroupBounds, ComparisonTimings? timings = null, ComparisonInk? classificationInk = null) =>
        Calculate(a, b, parameters, useGroupBounds, timings, classificationInk, new());

    internal static RawDifference Calculate(Mat a, Mat b, ComparisonParameters parameters,
        bool useGroupBounds, ComparisonTimings? timings, ComparisonInk? classificationInk, GroupSearchExecution execution)
    {
        var started = Stopwatch.GetTimestamp();
        ArgumentNullException.ThrowIfNull(parameters);
        if (a.Empty() || b.Empty() || a.Type() != MatType.CV_8UC3 || b.Type() != MatType.CV_8UC3 || a.Size() != b.Size())
            throw new ArgumentException("比較画像は同じ大きさの空でない BGR 8bit 画像にしてください。");
        var width = a.Cols;
        var height = a.Rows;
        using (var difference = new Mat())
        {
            Cv2.Absdiff(a, b, difference);
            using var channels = difference.Reshape(1);
            if (Cv2.CountNonZero(channels) == 0)
            {
                if (timings is not null) timings.PreparationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return new(new Mat(height, width, MatType.CV_8UC1, Scalar.All(0)), 0, 0);
            }
        }

        var shift = Units.RoundPixels(parameters.Diff.MaxShiftMm, parameters.Dpi);
        using var ownedA = ComparisonFeatures.Create(a, parameters, shift > 0);
        using var ownedB = ComparisonFeatures.Create(b, parameters, shift > 0);
        if (timings is not null)
        {
            timings.FeatureWorkers = Math.Max(ownedA.WorkerCount, ownedB.WorkerCount);
            timings.FeatureWorkerTemporaryBytes = Math.Max(ownedA.WorkerTemporaryBytes, ownedB.WorkerTemporaryBytes);
        }
        var featuresA = ownedA.Read();
        var featuresB = ownedB.Read();
        if (timings is not null) timings.PreparationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        var candidates = Candidates(featuresA, featuresB, width, height, parameters.Diff, 0, 0);
        if (timings is not null) timings.CandidatesMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (shift <= 0 || !candidates.Contains((byte)255))
            return new(MatBuffers.Mask(candidates, width, height), 0, 0);

        started = Stopwatch.GetTimestamp();
        var labels = Groups(ownedA.Ink, ownedB.Ink, candidates, shift, out var count);
        classificationInk?.Capture(a, b, parameters, ownedA.Ink, ownedB.Ink);
        if (timings is not null) timings.GroupingMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        var shifts = (from dx in Enumerable.Range(-shift, checked(2 * shift + 1))
                      from dy in Enumerable.Range(-shift, checked(2 * shift + 1))
                      orderby Math.Abs(dx) + Math.Abs(dy), dx, dy
                      select (dx, dy)).ToArray();
        var result = useGroupBounds
            ? EvaluateBounds(ownedA, ownedB, width, height, parameters.Diff, candidates, labels, count, shifts, shift, execution, timings)
            : EvaluateFullPage(featuresA, featuresB, width, height, parameters.Diff, candidates, labels, count, shifts);
        if (timings is not null) timings.ShiftsMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return result;
    }

    private static RawDifference EvaluateFullPage(ComparisonFeatureData featuresA, ComparisonFeatureData featuresB, int width, int height,
        DiffOptions options, byte[] candidates, int[] labels, int count, (int dx, int dy)[] shifts)
    {
        var masks = new List<byte[]>();
        var bestCounts = Enumerable.Repeat(int.MaxValue, count).ToArray();
        var initialCounts = new int[count];
        var best = new int[count];
        // 最適化前の方式を、回帰比較と計測用の内部経路として保持する。
        for (var i = 0; i < shifts.Length; i++)
        {
            var (dx, dy) = shifts[i];
            var mask = i == 0 ? candidates : Candidates(featuresA, featuresB, width, height, options, dx, dy);
            masks.Add(mask);
            var counts = new int[count];
            for (var pixel = 0; pixel < mask.Length; pixel++)
                if (mask[pixel] != 0) counts[labels[pixel]]++;
            if (i == 0) initialCounts = counts;
            for (var group = 1; group < count; group++)
                if (counts[group] < bestCounts[group])
                {
                    bestCounts[group] = counts[group];
                    best[group] = i;
                }
        }
        var absorbed = 0;
        var maxShift = 0;
        for (var group = 1; group < count; group++)
        {
            if (initialCounts[group] == 0 || bestCounts[group] != 0) continue;
            absorbed++;
            var (dx, dy) = shifts[best[group]];
            maxShift = Math.Max(maxShift, Math.Max(Math.Abs(dx), Math.Abs(dy)));
        }
        var raw = new byte[candidates.Length];
        for (var pixel = 0; pixel < raw.Length; pixel++)
        {
            var group = labels[pixel];
            if (group > 0 && initialCounts[group] > 0 && bestCounts[group] > 0)
                raw[pixel] = masks[best[group]][pixel];
        }
        return new(MatBuffers.Mask(raw, width, height), absorbed, maxShift);
    }

    private static RawDifference EvaluateBounds(ComparisonFeatures ownedA, ComparisonFeatures ownedB, int width, int height, DiffOptions options,
        byte[] candidates, int[] labels, int count, (int dx, int dy)[] shifts, int shift,
        GroupSearchExecution execution, ComparisonTimings? timings)
    {
        var started = Stopwatch.GetTimestamp();
        var index = GroupRunIndex.TryCreate(labels, candidates, width, height, count, execution.RunMemoryBudget);
        if (timings is not null)
        {
            timings.GroupIndexMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            timings.SearchRuns = index?.RunCount ?? 0;
            timings.UsedRectangleSearch = index is null;
            timings.SearchWorkers = 1;
        }
        if (index is null) return EvaluateRectangles(ownedA.Read(), ownedB.Read(), width, height, options, candidates, labels, count, shifts, shift);
        var schedule = GroupSearchSchedule.Create(index, shifts.Length, execution);
        if (timings is not null) { timings.SearchGroups = schedule.Count; timings.SearchWorkers = schedule.Degree; }
        var raw = new byte[candidates.Length];
        var results = new GroupShiftResult[count];
        schedule.Run(group =>
        {
            // ref structのビューをlambdaへ捕捉せず、その実行スレッド内で取得する。
            results[group] = GroupShiftSearch.Evaluate(ownedA.Read(), ownedB.Read(), width, height,
                options, index.Runs(group), index.InitialCount(group), shifts, raw);
        });
        var absorbed = 0; var maxShift = 0;
        for (var group = 1; group < count; group++)
        {
            if (index.InitialCount(group) == 0 || results[group].Remaining != 0) continue;
            absorbed++;
            var (dx, dy) = shifts[results[group].ShiftIndex];
            maxShift = Math.Max(maxShift, Math.Max(Math.Abs(dx), Math.Abs(dy)));
        }
        return new(MatBuffers.Mask(raw, width, height), absorbed, maxShift);
    }

    private static RawDifference EvaluateRectangles(ComparisonFeatureData a, ComparisonFeatureData b, int width, int height, DiffOptions options,
        byte[] candidates, int[] labels, int count, (int dx, int dy)[] shifts, int shift)
    {
        var left = Enumerable.Repeat(width, count).ToArray();
        var top = Enumerable.Repeat(height, count).ToArray();
        var right = new int[count]; var bottom = new int[count]; var initialCounts = new int[count];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var pixel = y * width + x; var group = labels[pixel];
            if (group == 0) continue;
            left[group] = Math.Min(left[group], x); top[group] = Math.Min(top[group], y);
            right[group] = Math.Max(right[group], x); bottom[group] = Math.Max(bottom[group], y);
            if (candidates[pixel] != 0) initialCounts[group]++;
        }
        var raw = new byte[candidates.Length];
        var absorbed = 0; var maxShift = 0;
        var tolerance = (float)options.EdgeTolerance; var threshold = (float)options.ColorThreshold;
        for (var group = 1; group < count; group++)
        {
            if (initialCounts[group] == 0) continue;
            // グループの外接矩形に余白を付けた範囲だけを評価する。
            // 特徴量はページ全体で先に作り、元座標から参照する。ROI の端に偽の境界を作らない。
            var x0 = Math.Max(0, left[group] - shift - 3); var y0 = Math.Max(0, top[group] - shift - 3);
            var x1 = Math.Min(width - 1, right[group] + shift + 3); var y1 = Math.Min(height - 1, bottom[group] + shift + 3);
            var bestCount = initialCounts[group]; var bestDx = 0; var bestDy = 0;
            foreach (var (dx, dy) in shifts.AsSpan(1))
            {
                var candidateCount = 0;
                for (var y = y0; y <= y1 && candidateCount < bestCount; y++)
                {
                    var sourceRow = Math.Clamp(y - dy, 0, height - 1) * width;
                    for (var x = x0; x <= x1 && candidateCount < bestCount; x++)
                    {
                        var pixel = y * width + x;
                        if (labels[pixel] != group) continue;
                        var source = sourceRow + Math.Clamp(x - dx, 0, width - 1);
                        if (IsCandidate(a, b, pixel, source, threshold, tolerance)) candidateCount++;
                    }
                }
                if (candidateCount >= bestCount) continue;
                bestCount = candidateCount; bestDx = dx; bestDy = dy;
                // ずれは優先順に走査済み。ゼロよりよい候補はない。
                if (bestCount == 0) break;
            }
            if (bestCount == 0)
            {
                absorbed++;
                maxShift = Math.Max(maxShift, Math.Max(Math.Abs(bestDx), Math.Abs(bestDy)));
                continue;
            }
            for (var y = y0; y <= y1; y++)
            {
                var sourceRow = Math.Clamp(y - bestDy, 0, height - 1) * width;
                for (var x = x0; x <= x1; x++)
                {
                    var pixel = y * width + x;
                    if (labels[pixel] == group && IsCandidate(a, b, pixel,
                        sourceRow + Math.Clamp(x - bestDx, 0, width - 1), threshold, tolerance)) raw[pixel] = 255;
                }
            }
        }
        return new(MatBuffers.Mask(raw, width, height), absorbed, maxShift);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCandidate(ComparisonFeatureData a, ComparisonFeatureData b, int pixel, int source, float threshold, float tolerance)
    {
        for (var channel = 0; channel < 3; channel++)
        {
            var ai = pixel * 3 + channel; var bi = source * 3 + channel;
            var limit = tolerance == 0 ? threshold : threshold + tolerance * MathF.Max(a.Contrast[ai], b.Contrast[bi]);
            if (MathF.Abs(a.Values[ai] - b.Values[bi]) > limit) return true;
        }
        return false;
    }

    private static byte[] Candidates(ComparisonFeatureData a, ComparisonFeatureData b, int width, int height, DiffOptions options, int dx, int dy)
    {
        var mask = new byte[width * height];
        var threshold = (float)options.ColorThreshold;
        var tolerance = (float)options.EdgeTolerance;
        for (var y = 0; y < height; y++)
        {
            var sourceRow = Math.Clamp(y - dy, 0, height - 1) * width;
            for (var x = 0; x < width; x++)
            {
                var pixel = y * width + x;
                var source = sourceRow + Math.Clamp(x - dx, 0, width - 1);
                for (var channel = 0; channel < 3; channel++)
                {
                    var ai = pixel * 3 + channel;
                    var bi = source * 3 + channel;
                    var limit = tolerance == 0 ? threshold : threshold + tolerance * MathF.Max(a.Contrast[ai], b.Contrast[bi]);
                    if (MathF.Abs(a.Values[ai] - b.Values[bi]) > limit)
                    {
                        mask[pixel] = 255;
                        break;
                    }
                }
            }
        }
        return mask;
    }

    private static int[] Groups(Mat inkA, Mat inkB, byte[] candidates, int shift, out int count)
    {
        // 次のラベル画像を確保する前に、前段のネイティブ画像を解放する。
        // 配列の構成は維持し、連続ページで GC の回収時期が変わる影響を抑える。
        using var dilated = new Mat();
        {
            using var united = GroupRegion(inkA, inkB, candidates);
            using var expansion = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(checked(2 * shift + 1), checked(2 * shift + 1)));
            Cv2.Dilate(united, dilated, expansion);
        }
        using var groupLabels = new Mat();
        count = Cv2.ConnectedComponents(dilated, groupLabels, PixelConnectivity.Connectivity8);
        return MatBuffers.Integers(groupLabels);
    }

    private static Mat GroupRegion(Mat inkA, Mat inkB, byte[] candidates)
    {
        // この段階で必要なデータだけを残し、Mat の寿命を次段へ持ち越さない。
        int[] inkData;
        int inkCount;
        {
            using var ink = new Mat();
            Cv2.BitwiseOr(inkA, inkB, ink);
            using var inkLabels = new Mat();
            inkCount = Cv2.ConnectedComponents(ink, inkLabels, PixelConnectivity.Connectivity8);
            inkData = MatBuffers.Integers(inkLabels);
        }
        var touched = new bool[inkCount];
        for (var i = 0; i < candidates.Length; i++)
            if (candidates[i] != 0) touched[inkData[i]] = true;
        touched[0] = false;
        byte[] regionData;
        {
            using var original = MatBuffers.Mask(candidates, inkA.Cols, inkA.Rows);
            using var region = new Mat();
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            Cv2.Dilate(original, region, kernel);
            regionData = MatBuffers.Bytes(region);
        }
        for (var i = 0; i < regionData.Length; i++)
            if (touched[inkData[i]]) regionData[i] = 255;
        return MatBuffers.Mask(regionData, inkA.Cols, inkA.Rows);
    }
}
