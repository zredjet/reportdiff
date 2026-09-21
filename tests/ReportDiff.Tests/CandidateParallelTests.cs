using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class CandidateParallelTests
{
    public static IEnumerable<object[]> Cases => TolerantDifferenceTests.Cases;

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryWorkerCountPreservesGoldenResults(string id)
    {
        var p = GoldenData.Parameters(GoldenData.Cases().Single(c => c.GetProperty("id").GetString() == id));
        using var a = GoldenData.Image(id, "a"); using var b = GoldenData.Image(id, "b");
        // 初期候補も探索も変更前の全ページ走査で求める。
        using var expected = TolerantDifference.Calculate(a, b, p, false);
        foreach (var degree in new[] { 1, 2, 4, 8 })
        {
            using var actual = TolerantDifference.Calculate(a, b, p, true, null, null, new(),
                new() { MaxDegreeOfParallelism = degree, MinimumParallelPixels = 0 });
            Assert.Equal(expected.AbsorbedGroups, actual.AbsorbedGroups);
            Assert.Equal(expected.MaxShiftPx, actual.MaxShiftPx);
            Assert.Equal(0, Cv2.Norm(expected.RawMask, actual.RawMask, NormTypes.INF));
        }
    }

    [Theory]
    [InlineData(73, 513, 300, 0.0, 3)]
    [InlineData(73, 513, 300, 0.3, 3)]
    [InlineData(73, 513, 400, 0.6, 3)]
    [InlineData(1, 513, 400, 0.3, 3)]
    [InlineData(73, 1, 300, 0.0, 3)]
    [InlineData(73, 513, 300, 0.3, 1000)]
    public void NonContinuousInputsAndRemainderRowsPreserveMasks(int width, int height, int dpi, double edge, double threshold)
    {
        using var parent = new Mat(height + 6, width + 8, MatType.CV_8UC3);
        var random = new Random(2709);
        var pixels = parent.AsSpan<Vec3b>();
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        using var parentB = parent.Clone();
        using var a = new Mat(parent, new Rect(3, 3, width, height));
        using var b = new Mat(parentB, new Rect(3, 3, width, height));
        for (var y = 0; y < height; y += 3)
            Cv2.Line(b, new Point(0, y), new Point(width - 1, y), new Scalar(y % 256, 0, 255));
        Cv2.Line(b, new Point(0, height - 1), new Point(width - 1, height - 1), Scalar.All(0));
        if (height > 1) Assert.False(a.IsContinuous());
        var beforeA = MatBuffers.Bytes(parent); var beforeB = MatBuffers.Bytes(parentB);
        var p = new ComparisonParameters { Dpi = dpi, Diff = new()
            { EdgeTolerance = edge, ColorThreshold = threshold, MaxShiftMm = 0 } };
        using var expected = TolerantDifference.Calculate(a, b, p, false);
        if (threshold == 1000) Assert.Equal(0, Cv2.CountNonZero(expected.RawMask));
        for (var repeat = 0; repeat < 3; repeat++)
        foreach (var degree in new[] { 1, 2, 4, 8 })
        {
            var timings = new ComparisonTimings();
            using var actual = TolerantDifference.Calculate(a, b, p, true, timings, null, new(),
                new() { MaxDegreeOfParallelism = degree, MinimumParallelPixels = 0 });
            Assert.Equal(Math.Min(height, Math.Min(degree, Environment.ProcessorCount)), timings.CandidateWorkers);
            Assert.Equal(0, Cv2.Norm(expected.RawMask, actual.RawMask, NormTypes.INF));
        }
        Assert.Equal(beforeA, MatBuffers.Bytes(parent)); Assert.Equal(beforeB, MatBuffers.Bytes(parentB));
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.0, 1)]
    [InlineData(0.0, 2)]
    [InlineData(0.3, 0)]
    [InlineData(0.3, 1)]
    [InlineData(0.3, 2)]
    [InlineData(0.6, 2)]
    public void Float32ThresholdBoundaryAndChannelOrderArePreserved(double edge, int channel)
    {
        var options = new DiffOptions { EdgeTolerance = edge, ColorThreshold = 3.1 };
        foreach (var sign in new[] { -1, 1 })
        foreach (var swapContrast in new[] { false, true })
        {
            var a = new float[15]; var b = new float[15];
            var ca = edge == 0 ? [] : Enumerable.Repeat(swapContrast ? 7.3f : 2.1f, 15).ToArray();
            var cb = edge == 0 ? [] : Enumerable.Repeat(swapContrast ? 2.1f : 7.3f, 15).ToArray();
            var limit = edge == 0 ? (float)options.ColorThreshold
                : (float)options.ColorThreshold + (float)edge * 7.3f;
            b[3 + channel] = sign * MathF.BitDecrement(limit);
            b[6 + channel] = sign * limit;
            b[9 + channel] = sign * MathF.BitIncrement(limit);
            var output = new byte[] { 17, 0, 0, 0, 19 };
            InitialCandidates.Fill(new(a, ca), new(b, cb), options, 1, output.AsSpan(1, 3));
            Assert.Equal(new byte[] { 17, 0, 0, 255, 19 }, output);
        }
    }

    [Fact]
    public void ScheduleBoundsWorkersBySizeCpuAndAvailableRows()
    {
        Assert.Equal(1, CandidateRowSchedule.Create(999, 1000, new()).Degree);
        Assert.Equal(Math.Min(4, Environment.ProcessorCount), CandidateRowSchedule.Create(1000, 1000, new()).Degree);
        Assert.Equal(1, CandidateRowSchedule.Create(1_000_000, 1, new()).Degree);
        Assert.Equal(Math.Min(2, Environment.ProcessorCount), CandidateRowSchedule.Create(500_000, 2, new()).Degree);
        Assert.Equal(1, CandidateRowSchedule.Create(4960, 3508, new() { MaxDegreeOfParallelism = 1 }).Degree);
        // サイズ判定・区間分割はintの乗算を溢れさせない。
        var large = CandidateRowSchedule.Create(int.MaxValue, int.MaxValue, new());
        var intervals = new System.Collections.Concurrent.ConcurrentBag<(int first, int last)>();
        large.Run((first, last) => intervals.Add((first, last)));
        var sorted = intervals.OrderBy(i => i.first).ToArray();
        Assert.Equal(0, sorted[0].first); Assert.Equal(int.MaxValue, sorted[^1].last);
        for (var i = 1; i < sorted.Length; i++) Assert.Equal(sorted[i - 1].last, sorted[i].first);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(513)]
    [InlineData(3508)]
    public void ScheduleCoversEveryRowExactlyOnce(int height)
    {
        foreach (var degree in new[] { 1, 2, 4, 8 })
        {
            var schedule = CandidateRowSchedule.Create(73, height,
                new() { MaxDegreeOfParallelism = degree, MinimumParallelPixels = 0 });
            var visits = new int[height];
            schedule.Run((first, last) =>
            {
                Assert.InRange(first, 0, height - 1); Assert.InRange(last, first + 1, height);
                for (var row = first; row < last; row++) Interlocked.Increment(ref visits[row]);
            });
            Assert.All(visits, count => Assert.Equal(1, count));
        }
    }

    [Fact]
    public void FailureWaitsForStartedWorkersBeforeReturning()
    {
        if (Environment.ProcessorCount < 2) return;
        var schedule = CandidateRowSchedule.Create(73, 513,
            new() { MaxDegreeOfParallelism = 2, MinimumParallelPixels = 0 });
        using var started = new Barrier(2);
        var running = 0; var completed = 0;
        Assert.Throws<AggregateException>(() => schedule.Run((first, last) =>
        {
            Interlocked.Increment(ref running);
            try
            {
                Assert.True(started.SignalAndWait(TimeSpan.FromSeconds(10)));
                if (first == 0) throw new InvalidOperationException("試験用の失敗");
                Thread.Sleep(30);
            }
            finally { Interlocked.Decrement(ref running); Interlocked.Increment(ref completed); }
        }));
        Assert.Equal(0, running); Assert.Equal(2, completed);
    }
}
