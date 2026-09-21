using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class GroupParallelSearchTests
{
    public static IEnumerable<object[]> Cases => TolerantDifferenceTests.Cases;

    [Theory]
    [MemberData(nameof(Cases))]
    public void SerialParallelAndFallbackPreserveEveryGoldenCase(string id)
    {
        var p = GoldenData.Parameters(GoldenData.Cases().Single(c => c.GetProperty("id").GetString() == id));
        using var a = GoldenData.Image(id, "a"); using var b = GoldenData.Image(id, "b");
        using var reference = TolerantDifference.Calculate(a, b, p, false);
        foreach (var degree in new[] { 1, 2, 4, 8, 0 })
        {
            using var actual = TolerantDifference.Calculate(a, b, p, true, null, null, new()
            {
                MaxDegreeOfParallelism = Math.Max(1, degree), MinimumParallelWork = 0,
                RunMemoryBudget = degree == 0 ? 0 : GroupRunIndex.DefaultMemoryBudget
            });
            Assert.Equal(reference.AbsorbedGroups, actual.AbsorbedGroups);
            Assert.Equal(reference.MaxShiftPx, actual.MaxShiftPx);
            Assert.Equal(0, Cv2.Norm(reference.RawMask, actual.RawMask, NormTypes.INF));
        }
    }

    [Theory]
    [InlineData(300, 0, 0.15)]
    [InlineData(300, 0.3, 0.15)]
    [InlineData(400, 0.6, 0.3)]
    public void ManyGroupsAreDeterministicAcrossWorkersAndNonContinuousInputs(int dpi, double edge, double shift)
    {
        using var parent = new Mat(307, 409, MatType.CV_8UC3, Scalar.All(190));
        using var a = new Mat(parent, new Rect(3, 3, 400, 300));
        a.SetTo(Scalar.All(255));
        for (var y = 0; y < 300; y += 40)
            for (var x = 0; x < 400; x += 40)
                Cv2.Rectangle(a, new Rect(x, y, 8, 12), Scalar.All(0), -1);
        using var parentB = parent.Clone();
        using var b = new Mat(parentB, new Rect(3, 3, 400, 300));
        // 大きいエッジ許容でも初期候補が複数残る移動量にする。
        using var transform = Mat.FromArray(new double[,] { { 1, 0, edge >= 0.6 ? 3 : 1 }, { 0, 1, 0 } });
        using var shifted = new Mat();
        Cv2.WarpAffine(a, shifted, transform, a.Size(), InterpolationFlags.Nearest, BorderTypes.Replicate);
        shifted.CopyTo(b);
        Cv2.Rectangle(b, new Rect(201, 201, 8, 12), new Scalar(0, 0, 190), -1);
        var beforeA = MatBuffers.Bytes(parent); var beforeB = MatBuffers.Bytes(parentB);
        var p = new ComparisonParameters { Dpi = dpi, Diff = new() { EdgeTolerance = edge, MaxShiftMm = shift }, Move = new() { SearchMm = 0 } };
        using var reference = TolerantDifference.Calculate(a, b, p, false);
        for (var repeat = 0; repeat < 3; repeat++)
        foreach (var degree in new[] { 1, 2, 4, 8, 0 })
        {
            var timings = new ComparisonTimings();
            using var actual = TolerantDifference.Calculate(a, b, p, true, timings, null, new()
            {
                MaxDegreeOfParallelism = Math.Max(1, degree), MinimumParallelWork = 0,
                RunMemoryBudget = degree == 0 ? 0 : GroupRunIndex.DefaultMemoryBudget
            });
            Assert.Equal(degree == 0, timings.UsedRectangleSearch);
            if (degree > 0) Assert.True(timings.SearchGroups > 2);
            if (degree > 1) Assert.Equal(Math.Min(degree, Environment.ProcessorCount), timings.SearchWorkers);
            Assert.Equal(reference.AbsorbedGroups, actual.AbsorbedGroups);
            Assert.Equal(reference.MaxShiftPx, actual.MaxShiftPx);
            Assert.Equal(0, Cv2.Norm(reference.RawMask, actual.RawMask, NormTypes.INF));
        }
        Assert.Equal(beforeA, MatBuffers.Bytes(parent)); Assert.Equal(beforeB, MatBuffers.Bytes(parentB));
    }

    [Fact]
    public void SchedulingKeepsSmallAndSingleGroupWorkSerial()
    {
        var labels = Enumerable.Range(0, 1000).Select(i => i < 900 ? 1 : 2).ToArray();
        var candidates = Enumerable.Repeat((byte)255, 1000).ToArray();
        var index = GroupRunIndex.TryCreate(labels, candidates, 100, 10, 3)!;
        Assert.Equal(1, GroupSearchSchedule.Create(index, 25, new()).Degree);
        Assert.Equal(1, GroupSearchSchedule.Create(index, 25, new() { MaxDegreeOfParallelism = 1, MinimumParallelWork = 0 }).Degree);
        var parallel = GroupSearchSchedule.Create(index, 25, new() { MinimumParallelWork = 0 });
        Assert.Equal(Math.Min(2, Environment.ProcessorCount), parallel.Degree);
        Array.Fill(labels, 1);
        index = GroupRunIndex.TryCreate(labels, candidates, 100, 10, 2)!;
        Assert.Equal(1, GroupSearchSchedule.Create(index, 25, new() { MinimumParallelWork = 0 }).Degree);
    }

    [Fact]
    public void ScheduleProcessesEachGroupOnceAndWaitsForWorkersOnFailure()
    {
        var visited = new int[101];
        var schedule = new GroupSearchSchedule(Enumerable.Range(1, 100).ToArray(), 4);
        schedule.Run(group => Interlocked.Increment(ref visited[group]));
        Assert.All(visited.Skip(1), value => Assert.Equal(1, value));
        var running = 0; var completed = 0;
        Assert.Throws<AggregateException>(() => schedule.Run(group =>
        {
            Interlocked.Increment(ref running);
            try
            {
                if (group == 5) throw new InvalidOperationException("試験用の失敗");
                Thread.SpinWait(100_000);
            }
            finally { Interlocked.Increment(ref completed); Interlocked.Decrement(ref running); }
        }));
        Assert.Equal(0, Volatile.Read(ref running));
        Assert.InRange(Volatile.Read(ref completed), 5, 100);
    }
}
