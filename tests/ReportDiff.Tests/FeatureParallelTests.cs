using System.Runtime.InteropServices;
using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class FeatureParallelTests
{
    [Theory]
    [InlineData(73, 300, 0.0, 1.5, false)]
    [InlineData(73, 300, 0.0, 1.5, true)]
    [InlineData(73, 300, 0.3, 1.5, false)]
    [InlineData(73, 300, 0.3, 1.5, true)]
    [InlineData(73, 400, 0.6, 1.5, true)]
    [InlineData(73, 400, 0.3, 20.0, true)]
    [InlineData(73, 400, 0.3, 0.001, true)]
    [InlineData(1, 300, 0.0, 1.5, false)]
    [InlineData(1, 400, 0.6, 1.5, true)]
    public void EveryWorkerCountPreservesFullPageFeaturesAndInput(int width, int dpi, double edge, double radius, bool ink)
    {
        // 17帯で8workerにも仕事を割り当て、最後の1行・worker境界・非連続入力を含める。
        using var parent = new Mat(2057, width + 8, MatType.CV_8UC3);
        var random = new Random(2619);
        var pixels = parent.AsSpan<Vec3b>();
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        using var image = new Mat(parent, new Rect(3, 3, width, 2049));
        Assert.False(image.IsContinuous());
        var before = MatBuffers.Bytes(parent);
        var p = new ComparisonParameters { Dpi = dpi, Diff = new() { EdgeTolerance = edge },
            Ink = new() { BackgroundRadiusMm = radius } };
        using var lab = ImageInk.ToLab(image);
        using var blur = new Mat(); using var maximum = new Mat(); using var minimum = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        if (edge != 0)
        {
            Cv2.Blur(lab, blur, new Size(3, 3));
            Cv2.Dilate(lab, maximum, kernel); Cv2.Erode(lab, minimum, kernel);
            Cv2.Subtract(maximum, minimum, maximum);
        }
        using var expectedInk = ink ? ImageInk.FromLab(lab, dpi, p.Ink) : new Mat();
        var expectedValues = MemoryMarshal.AsBytes((edge == 0 ? lab : blur).AsSpan<Vec3f>()).ToArray();
        var expectedContrast = MemoryMarshal.AsBytes(maximum.AsSpan<Vec3f>()).ToArray();
        foreach (var degree in new[] { 1, 2, 4, 8 })
        for (var repeat = 0; repeat < 2; repeat++)
        {
            using var features = ComparisonFeatures.Create(image, p, ink, new()
            {
                MaxDegreeOfParallelism = degree, MinimumParallelPixels = 0, TemporaryMemoryBudget = long.MaxValue
            });
            Assert.Equal(Math.Min(degree, Environment.ProcessorCount), features.WorkerCount);
            var data = features.Read();
            Assert.Equal(expectedValues, MemoryMarshal.AsBytes(data.Values).ToArray());
            Assert.Equal(expectedContrast, MemoryMarshal.AsBytes(data.Contrast).ToArray());
            if (ink) Assert.Equal(0, Cv2.Norm(expectedInk, features.Ink, NormTypes.INF));
            else Assert.True(features.Ink.Empty());
        }
        Assert.Equal(before, MatBuffers.Bytes(parent));
    }

    [Fact]
    public void ScheduleBoundsWorkersByImageSizeStripeCountAndMemory()
    {
        var unlimited = new FeatureExecution { MaxDegreeOfParallelism = 8, MinimumParallelPixels = 0,
            TemporaryMemoryBudget = long.MaxValue };
        foreach (var height in new[] { 1, 127, 128, 129, 256, 257, 384 })
            Assert.Equal(1, FeatureStripeSchedule.Create(8000, height, 18, true, true, unlimited).Degree);
        Assert.Equal(1, FeatureStripeSchedule.Create(100, 2049, 18, true, true, new()).Degree);
        Assert.Equal(1, FeatureStripeSchedule.Create(999, 1000, 18, true, true, new()).Degree);
        Assert.Equal(Math.Min(4, Environment.ProcessorCount), FeatureStripeSchedule.Create(1000, 1000, 18, true, true, new()).Degree);
        var normal = FeatureStripeSchedule.Create(4960, 3508, 18, true, true, unlimited);
        Assert.Equal(Math.Min(8, Environment.ProcessorCount), normal.Degree);
        // erode用float3画像1枚、Lab+インク生成用4枚の同時保持。
        Assert.Equal(4960L * (12 * 128 + 25 * 164), normal.WorkerTemporaryBytes);
        var exactlyTwo = unlimited with { TemporaryMemoryBudget = normal.WorkerTemporaryBytes * 2 };
        Assert.Equal(Math.Min(2, Environment.ProcessorCount), FeatureStripeSchedule.Create(4960, 3508, 18, true, true, exactlyTwo).Degree);
        Assert.Equal(1, FeatureStripeSchedule.Create(4960, 3508, 18, true, true,
            exactlyTwo with { TemporaryMemoryBudget = exactlyTwo.TemporaryMemoryBudget - 1 }).Degree);
        Assert.Equal(1, FeatureStripeSchedule.Create(4960, 3508, 1000, true, true, new()).Degree);
        Assert.Equal(1, FeatureStripeSchedule.Create(4960, 3508, 18, true, true,
            unlimited with { TemporaryMemoryBudget = 0 }).Degree);
        Assert.Equal(4960L * 24 * 128, FeatureStripeSchedule.Create(4960, 3508, 0, false, false, unlimited).WorkerTemporaryBytes);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(1024)]
    [InlineData(2049)]
    public void ScheduleAssignsEveryStripeExactlyOnce(int height)
    {
        foreach (var degree in new[] { 1, 2, 4, 8 })
        {
            var schedule = FeatureStripeSchedule.Create(73, height, 18, true, true, new()
            { MaxDegreeOfParallelism = degree, MinimumParallelPixels = 0, TemporaryMemoryBudget = long.MaxValue });
            var visits = new int[schedule.StripeCount];
            schedule.Run((first, last) =>
            {
                Assert.InRange(first, 0, schedule.StripeCount - 1);
                Assert.InRange(last, first + 1, schedule.StripeCount);
                for (var stripe = first; stripe < last; stripe++) Interlocked.Increment(ref visits[stripe]);
            });
            Assert.All(visits, count => Assert.Equal(1, count));
        }
    }

    [Fact]
    public void FailureWaitsForStartedWorkersBeforeReturning()
    {
        if (Environment.ProcessorCount < 2) return;
        var schedule = FeatureStripeSchedule.Create(73, 2049, 18, true, true, new()
        { MaxDegreeOfParallelism = 2, MinimumParallelPixels = 0, TemporaryMemoryBudget = long.MaxValue });
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

    [Fact]
    public void FailedParallelCreationDoesNotDisposeInputAndCanBeRetried()
    {
        var execution = new FeatureExecution { MaxDegreeOfParallelism = 4, MinimumParallelPixels = 0,
            TemporaryMemoryBudget = long.MaxValue };
        using var invalid = new Mat(2049, 73, MatType.CV_8UC1, Scalar.All(255));
        Assert.ThrowsAny<Exception>(() => ComparisonFeatures.Create(invalid, new(), true, execution));
        Assert.False(invalid.IsDisposed); Assert.Equal(73 * 2049, Cv2.CountNonZero(invalid));
        using var valid = new Mat(2049, 73, MatType.CV_8UC3, Scalar.All(255));
        using var result = ComparisonFeatures.Create(valid, new(), true, execution);
        Assert.Equal(0, Cv2.CountNonZero(result.Ink));
        result.Dispose(); result.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { result.Read(); });
        Assert.False(valid.IsDisposed);
    }
}
