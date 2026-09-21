using System.Collections.Concurrent;
using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class GroupCandidateSearchTests
{
    private static GroupRunIndex DenseIndex(int width, int height, int initial = 1) => new(
        Enumerable.Range(0, height).Select(y => new GroupRun(y, 0, width)).ToArray(),
        [0, 0, height], [0, initial], [0, checked(width * height)]);

    [Fact]
    public void ScheduleRespectsSizeWorkersBlocksAndExactMemoryBoundary()
    {
        var index = DenseIndex(1000, 1000);
        var execution = new GroupSearchExecution { MaxDegreeOfParallelism = 4 };
        Assert.Null(GroupCandidateSchedule.TryCreate(DenseIndex(999, 1001), 1, 25, execution, long.MaxValue, 4));
        var schedule = GroupCandidateSchedule.TryCreate(index, 1, 25, execution, long.MaxValue, 4)!;
        Assert.Equal(4, schedule.Degree); Assert.Equal(59, schedule.BlockCount);
        Assert.Equal(240, schedule.MemoryBytes); Assert.Equal(12036, index.BudgetedBytes);
        Assert.NotNull(GroupCandidateSchedule.TryCreate(index, 1, 25, execution, 240, 4));
        Assert.Null(GroupCandidateSchedule.TryCreate(index, 1, 25, execution, 239, 4));
        Assert.Null(GroupCandidateSchedule.TryCreate(index, 1, 25, execution with { CandidateMemoryBudget = 239 }, long.MaxValue, 4));
        Assert.Null(GroupCandidateSchedule.TryCreate(index, 1, 25, execution, long.MaxValue, 1));
        Assert.Null(GroupCandidateSchedule.TryCreate(index, 1, 25, execution with { MaxDegreeOfParallelism = 1 }, long.MaxValue, 4));
        Assert.Equal(2, GroupCandidateSchedule.TryCreate(index, 1, 25, execution, long.MaxValue, 2)!.Degree);
        Assert.Equal(4, GroupCandidateSchedule.TryCreate(index, 1, 25, execution with { MaxDegreeOfParallelism = 8 }, long.MaxValue, 8)!.Degree);
        Assert.Null(GroupCandidateSchedule.TryCreate(index, 1, 1, execution, long.MaxValue, 4));
        Assert.Null(GroupCandidateSchedule.TryCreate(DenseIndex(1000, 1000, 0), 1, 25, execution, long.MaxValue, 4));
        // 100万画素でも分ける区間が1本しかなければ従来経路。
        Assert.Null(GroupCandidateSchedule.TryCreate(DenseIndex(1_000_000, 1), 1, 25, execution, long.MaxValue, 4));
        Assert.Null(GroupCandidateSchedule.TryCreate(index, 1, 25, execution with { CandidateBlockPixels = 1_000_000 }, long.MaxValue, 4));
        Assert.Equal(2, GroupCandidateSchedule.TryCreate(index, 1, 25,
            execution with { CandidateBlockPixels = 500_000 }, long.MaxValue, 4)!.Degree);
    }

    [Theory]
    [InlineData(1, 35, 1)]
    [InlineData(1, 35, 16)]
    [InlineData(17, 19, 33)]
    [InlineData(100_000, 3, 16_384)]
    public void BlockBoundariesCoverEveryRunOnceIncludingShortLastBlock(int width, int height, int blockPixels)
    {
        var index = DenseIndex(width, height);
        var schedule = GroupCandidateSchedule.TryCreate(index, 1, 2,
            new() { MaxDegreeOfParallelism = 4, MinimumCandidatePixels = 0, CandidateBlockPixels = blockPixels }, long.MaxValue, 4)!;
        var covered = new List<int>();
        for (var block = 0; block < schedule.BlockCount; block++)
        {
            var (start, end) = schedule.Block(block);
            Assert.InRange(start, 0, height - 1); Assert.InRange(end, start + 1, height);
            covered.AddRange(Enumerable.Range(start, end - start));
        }
        Assert.Equal(Enumerable.Range(0, height), covered);
    }

    public static IEnumerable<object[]> Cases => TolerantDifferenceTests.Cases;

    [Theory]
    [MemberData(nameof(Cases))]
    public void GoldenMasksAndStatisticsMatchWithForcedCandidateWorkers(string id)
    {
        var p = GoldenData.Parameters(GoldenData.Cases().Single(c => c.GetProperty("id").GetString() == id));
        using var a = GoldenData.Image(id, "a"); using var b = GoldenData.Image(id, "b");
        using var expected = TolerantDifference.Calculate(a, b, p, false);
        foreach (var degree in new[] { 1, 2, 4 })
        {
            using var actual = TolerantDifference.Calculate(a, b, p, true, null, null,
                new() { MaxDegreeOfParallelism = degree, MinimumCandidatePixels = 0, CandidateBlockPixels = 16 });
            Assert.Equal(expected.AbsorbedGroups, actual.AbsorbedGroups);
            Assert.Equal(expected.MaxShiftPx, actual.MaxShiftPx);
            Assert.Equal(0, Cv2.Norm(expected.RawMask, actual.RawMask, NormTypes.INF));
        }
    }

    [Theory]
    [InlineData(17, 13, 300, 0)]
    [InlineData(17, 13, 300, 0.3)]
    [InlineData(17, 13, 400, 0.6)]
    [InlineData(1, 73, 400, 0.3)]
    [InlineData(4097, 3, 300, 0)]
    [InlineData(4097, 3, 300, 0.3)]
    public void CandidateSearchMatchesExhaustiveCountsAtEdgesAndOnNonContinuousInputs(int width, int height, int dpi, double edge)
    {
        using var parentA = new Mat(height + 6, width + 8, MatType.CV_8UC3);
        var random = new Random(721);
        var data = parentA.AsSpan<Vec3b>();
        for (var i = 0; i < data.Length; i++) data[i] = new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        using var parentB = parentA.Clone();
        using var a = new Mat(parentA, new Rect(3, 2, width, height));
        using var b = new Mat(parentB, new Rect(3, 2, width, height));
        for (var y = 0; y < height; y += 3) Cv2.Line(b, new Point(0, y), new Point(width - 1, y), new Scalar(37, 170, 224));
        var beforeA = MatBuffers.Bytes(parentA); var beforeB = MatBuffers.Bytes(parentB);
        var p = new ComparisonParameters { Dpi = dpi, Diff = new() { EdgeTolerance = edge } };
        using var fa = ComparisonFeatures.Create(a, p, false); using var fb = ComparisonFeatures.Create(b, p, false);
        var av = fa.Read().Values.ToArray(); var bv = fb.Read().Values.ToArray();
        var ac = fa.Read().Contrast.ToArray(); var bc = fb.Read().Contrast.ToArray();
        bool Candidate(int pixel, (int dx, int dy) shift)
        {
            var source = Math.Clamp(pixel / width - shift.dy, 0, height - 1) * width + Math.Clamp(pixel % width - shift.dx, 0, width - 1);
            return Enumerable.Range(0, 3).Any(c => MathF.Abs(av[pixel * 3 + c] - bv[source * 3 + c])
                > (edge == 0 ? 3f : 3f + (float)edge * MathF.Max(ac[pixel * 3 + c], bc[source * 3 + c])));
        }
        var shifts = (from dx in Enumerable.Range(-2, 5) from dy in Enumerable.Range(-2, 5)
            orderby Math.Abs(dx) + Math.Abs(dy), dx, dy select (dx, dy)).ToArray();
        var counts = shifts.Select(s => Enumerable.Range(0, width * height).Count(pixel => Candidate(pixel, s))).ToArray();
        var best = Array.IndexOf(counts, counts.Min());
        var expected = Enumerable.Range(0, width * height).Select(i => Candidate(i, shifts[best]) ? (byte)255 : (byte)0).ToArray();
        var index = DenseIndex(width, height, counts[0]);
        for (var repeat = 0; repeat < 3; repeat++)
        foreach (var degree in new[] { 1, 2, 4 })
        {
            var raw = new byte[width * height];
            var schedule = GroupCandidateSchedule.TryCreate(index, 1, shifts.Length,
                new() { MaxDegreeOfParallelism = degree, MinimumCandidatePixels = 0, CandidateBlockPixels = 7 }, long.MaxValue);
            var result = schedule is null
                ? GroupShiftSearch.Evaluate(fa.Read(), fb.Read(), width, height, p.Diff, index.Runs(1), counts[0], shifts, raw)
                : GroupCandidateSearch.Evaluate(fa, fb, width, height, p.Diff, index, 1, shifts, raw, schedule);
            Assert.Equal(new(counts[best], best), result); Assert.Equal(expected, raw);
        }
        Assert.Equal(beforeA, MatBuffers.Bytes(parentA)); Assert.Equal(beforeB, MatBuffers.Bytes(parentB));
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(0.3, 0)] [InlineData(0.3, 1)] [InlineData(0.3, 2)]
    public void SharedPredicateKeepsFloat32EqualityAndStrictInequality(double edge, int channel)
    {
        foreach (var sign in new[] { -1, 1 })
        {
            var a = new float[3]; var b = new float[3];
            var ca = edge == 0 ? [] : new[] { 2.1f, 2.1f, 2.1f };
            var cb = edge == 0 ? [] : new[] { 7.3f, 7.3f, 7.3f };
            var limit = edge == 0 ? 3.1f : 3.1f + (float)edge * 7.3f;
            foreach (var (value, expected) in new[] { (MathF.BitDecrement(limit), false), (limit, false), (MathF.BitIncrement(limit), true) })
            {
                b[channel] = value * sign;
                Assert.Equal(expected, GroupShiftSearch.IsCandidate(new(a, ca), new(b, cb), 0, 0, 3.1f, (float)edge));
            }
        }
    }

    [Fact]
    public void TiesKeepZeroShiftAndFirstPerfectShiftStopsLaterCandidates()
    {
        using var a = new Mat(33, 3, MatType.CV_8UC3, Scalar.All(255));
        using var b = a.Clone();
        Cv2.Line(b, new Point(1, 0), new Point(1, 32), Scalar.All(0));
        var p = new ComparisonParameters { Diff = new() { EdgeTolerance = 0 } };
        using var fa = ComparisonFeatures.Create(a, p, false); using var fb = ComparisonFeatures.Create(b, p, false);
        var runs = Enumerable.Range(0, 33).Select(y => new GroupRun(y, 1, 2)).ToArray();
        var index = new GroupRunIndex(runs, [0, 0, 33], [0, 33], [0, 33]);
        var schedule = GroupCandidateSchedule.TryCreate(index, 1, 3,
            new() { MaxDegreeOfParallelism = 4, MinimumCandidatePixels = 0, CandidateBlockPixels = 4 }, long.MaxValue, 4)!;
        var visited = new ConcurrentBag<int>(); var raw = new byte[99];
        var result = GroupCandidateSearch.Evaluate(fa, fb, 3, 33, p.Diff, index, 1,
            [(0, 0), (-1, 0), (1, 0)], raw, schedule, (_, _, _, shift, _) => visited.Add(shift));
        Assert.Equal(new(0, 1), result); Assert.All(visited, shift => Assert.Equal(1, shift)); Assert.All(raw, x => Assert.Equal(0, x));
        b.SetTo(Scalar.All(0));
        using var different = ComparisonFeatures.Create(b, p, false);
        result = GroupCandidateSearch.Evaluate(fa, different, 3, 33, p.Diff, index, 1,
            [(0, 0), (-1, 0), (1, 0)], raw, schedule);
        Assert.Equal(new(33, 0), result); Assert.Equal(33, raw.Count(x => x == 255));
    }

    [Theory]
    [InlineData(0)] [InlineData(17)] [InlineData(32)]
    public void SmallCutoffDoesNotAdoptPartialCountsOrIgnoreInitiallyEqualPixels(int changedRow)
    {
        using var a = new Mat(33, 3, MatType.CV_8UC3, Scalar.All(255));
        using var b = a.Clone();
        Cv2.Line(a, new Point(0, changedRow), new Point(0, changedRow), Scalar.All(0));
        Cv2.Line(b, new Point(2, changedRow), new Point(2, changedRow), Scalar.All(0));
        var p = new ComparisonParameters { Diff = new() { EdgeTolerance = 0 } };
        using var fa = ComparisonFeatures.Create(a, p, false); using var fb = ComparisonFeatures.Create(b, p, false);
        var runs = Enumerable.Range(0, 33).Select(y => new GroupRun(y, 0, 2)).ToArray();
        var index = new GroupRunIndex(runs, [0, 0, 33], [0, 1], [0, 66]);
        var schedule = GroupCandidateSchedule.TryCreate(index, 1, 3,
            new() { MaxDegreeOfParallelism = 4, MinimumCandidatePixels = 0, CandidateBlockPixels = 7 }, long.MaxValue, 4)!;
        for (var repeat = 0; repeat < 5; repeat++)
        {
            var raw = new byte[99];
            var result = GroupCandidateSearch.Evaluate(fa, fb, 3, 33, p.Diff, index, 1,
                [(0, 0), (-1, 0), (-2, 0)], raw, schedule);
            Assert.Equal(new(1, 0), result);
            Assert.Equal(255, raw[changedRow * 3]); Assert.Equal(1, raw.Count(x => x != 0));
        }
    }

    [Fact]
    public void PlanBudgetIncludesGroupMarksAndRespectsRemainingIndexBudget()
    {
        using var a = new Mat(1000, 1000, MatType.CV_8UC3, Scalar.All(255));
        using var b = new Mat(a.Size(), a.Type(), Scalar.All(0));
        var p = new ComparisonParameters { Diff = new() { EdgeTolerance = 0 } };
        var expectedPlanBytes = 2 + 240; // 2グループ分の印と59ブロックの終端境界。
        foreach (var budget in new[] { 0, expectedPlanBytes - 1, expectedPlanBytes })
        {
            var timings = new ComparisonTimings();
            using var actual = TolerantDifference.Calculate(a, b, p, true, timings, null,
                new() { CandidateMemoryBudget = budget });
            Assert.Equal(1_000_000, Cv2.CountNonZero(actual.RawMask));
            Assert.Equal(budget == expectedPlanBytes && Environment.ProcessorCount > 1 ? 1 : 0, timings.CandidateSearchGroups);
            Assert.InRange(timings.CandidateSearchPlanBytes, 0, budget);
        }
        foreach (var extra in new[] { 0, expectedPlanBytes - 1, expectedPlanBytes })
        {
            var timings = new ComparisonTimings();
            using var actual = TolerantDifference.Calculate(a, b, p, true, timings, null,
                new() { RunMemoryBudget = 12036 + extra });
            Assert.False(timings.UsedRectangleSearch); Assert.Equal(12036, timings.SearchIndexBytes);
            Assert.InRange(timings.SearchIndexBytes + timings.CandidateSearchPlanBytes, 0, 12036 + extra);
            Assert.Equal(extra == expectedPlanBytes && Environment.ProcessorCount > 1 ? 1 : 0, timings.CandidateSearchGroups);
        }
        var fallback = new ComparisonTimings();
        using var rectangle = TolerantDifference.Calculate(a, b, p, true, fallback, null, new() { RunMemoryBudget = 0 });
        Assert.True(fallback.UsedRectangleSearch); Assert.Equal(0, fallback.CandidateSearchGroups);
        Assert.Equal(1_000_000, Cv2.CountNonZero(rectangle.RawMask));
    }

    [Fact]
    public void MultipleLargeGroupsAndRemainingSmallGroupsMatchReference()
    {
        using var a = new Mat(73, 330, MatType.CV_8UC3, Scalar.All(255));
        using var b = a.Clone();
        Cv2.Rectangle(b, new Rect(3, 3, 100, 50), Scalar.All(0), -1);
        Cv2.Rectangle(b, new Rect(123, 3, 100, 50), Scalar.All(0), -1);
        Cv2.Rectangle(b, new Rect(300, 3, 3, 3), Scalar.All(0), -1);
        var p = new ComparisonParameters { Diff = new() { EdgeTolerance = 0 } };
        using var expected = TolerantDifference.Calculate(a, b, p, false);
        var timings = new ComparisonTimings();
        using var actual = TolerantDifference.Calculate(a, b, p, true, timings, null,
            new() { MinimumCandidatePixels = 1000, CandidateBlockPixels = 128 });
        Assert.Equal(3, timings.SearchGroups);
        Assert.Equal(Environment.ProcessorCount > 1 ? 2 : 0, timings.CandidateSearchGroups);
        Assert.Equal(0, Cv2.Norm(expected.RawMask, actual.RawMask, NormTypes.INF));
        Assert.Equal(expected.AbsorbedGroups, actual.AbsorbedGroups); Assert.Equal(expected.MaxShiftPx, actual.MaxShiftPx);
    }

    [Fact]
    public async Task FailureWaitsForOtherWorkersBeforeFeaturesAreDisposedAndRetrySucceeds()
    {
        if (Environment.ProcessorCount < 2) return;
        var cancellation = TestContext.Current.CancellationToken;
        using var a = new Mat(96, 128, MatType.CV_8UC3, Scalar.All(255));
        using var b = new Mat(a.Size(), a.Type(), Scalar.All(0));
        using var started = new Barrier(2); using var release = new ManualResetEventSlim();
        using var failed = new ManualResetEventSlim();
        var owners = new ConcurrentBag<ComparisonFeatures>(); var completed = 0;
        var execution = new GroupSearchExecution { MaxDegreeOfParallelism = 2, MinimumCandidatePixels = 0,
            CandidateBlockPixels = 128, BeforeCandidateWorker = (fa, fb, _, shift, worker) =>
            {
                owners.Add(fa); owners.Add(fb); Assert.Equal(1, shift);
                Assert.True(started.SignalAndWait(TimeSpan.FromSeconds(10), cancellation));
                if (worker == 0) { failed.Set(); throw new InvalidOperationException("候補内workerの試験用失敗"); }
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), cancellation));
                Assert.NotEmpty(fa.Read().Values.ToArray()); Assert.NotEmpty(fb.Read().Values.ToArray());
                Assert.False(fa.Ink.IsDisposed); Interlocked.Increment(ref completed);
            } };
        var task = Task.Run(() =>
        {
            using var result = TolerantDifference.Calculate(a, b, new(), true, null, null, execution);
        }, cancellation);
        try
        {
            Assert.True(failed.Wait(TimeSpan.FromSeconds(10), cancellation));
            await Task.Delay(30, cancellation);
            Assert.False(task.IsCompleted); Assert.Equal(0, completed);
        }
        finally { release.Set(); }
        var error = await Assert.ThrowsAsync<AggregateException>(async () => await task);
        Assert.Contains(error.InnerExceptions, e => e is InvalidOperationException { Message: "候補内workerの試験用失敗" });
        Assert.Equal(1, completed);
        Assert.All(owners, owner =>
        {
            Assert.True(owner.Ink.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => { owner.Read(); });
        });
        using var retry = TolerantDifference.Calculate(a, b, new(), true, null, null, execution with { BeforeCandidateWorker = null });
        Assert.Equal(128 * 96, Cv2.CountNonZero(retry.RawMask));
        Assert.False(a.IsDisposed); Assert.False(b.IsDisposed);
    }
}
