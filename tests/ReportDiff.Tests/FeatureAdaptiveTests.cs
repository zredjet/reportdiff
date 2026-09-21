using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class FeatureAdaptiveTests
{
    [Theory]
    [InlineData(4960, 3508, 18, true, true, 64, 4)]
    [InlineData(6614, 4677, 24, true, true, 64, 4)]
    [InlineData(4960, 3508, 18, false, true, 128, 4)]
    [InlineData(6614, 4677, 24, false, true, 64, 4)]
    [InlineData(4960, 3508, 2, true, false, 128, 4)]
    [InlineData(4960, 3508, 236, true, true, 128, 1)]
    [InlineData(999, 1000, 18, true, true, 128, 1)]
    [InlineData(1000, 1000, 18, true, true, 128, 4)]
    [InlineData(73, 2049, 18, true, true, 128, 1)]
    public void DefaultSelectionUsesFourWorkersOnlyWhenItImprovesTheCurrentSchedule(
        int width, int height, int margin, bool tolerant, bool ink, int rows, int degree)
    {
        var schedule = FeatureStripeSchedule.Create(width, height, margin, tolerant, ink, new() { MaxDegreeOfParallelism = 4 }, 16);
        Assert.Equal(rows, schedule.StripeRows);
        Assert.Equal(rows == 64, schedule.ReuseLab);
        Assert.Equal(degree, schedule.Degree);
        if (degree > 1) Assert.True(schedule.WorkerTemporaryBytes * degree <= 96L * 1024 * 1024);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LimitedCpusAndExecutionCapsKeepTheCurrent128RowPath(int limit)
    {
        var byCpu = FeatureStripeSchedule.Create(4960, 3508, 18, true, true, new() { MaxDegreeOfParallelism = 4 }, limit);
        var byOption = FeatureStripeSchedule.Create(4960, 3508, 18, true, true,
            new() { MaxDegreeOfParallelism = limit }, 16);
        foreach (var schedule in new[] { byCpu, byOption })
        {
            Assert.Equal(128, schedule.StripeRows);
            Assert.Equal(limit, schedule.Degree);
            Assert.Equal(4960L * (12 * 128 + 25 * 164), schedule.WorkerTemporaryBytes);
        }
    }

    [Fact]
    public void BudgetSelects64AtFourWorkersAndReturnsTo128WhenThatNoLongerFits()
    {
        const long candidateBytes = 4960L * (12 * 64 + 24 * 100);
        const long currentBytes = 4960L * (12 * 128 + 25 * 164);
        var execution = new FeatureExecution { MaxDegreeOfParallelism = 4, TemporaryMemoryBudget = candidateBytes * 4 };
        var exact = FeatureStripeSchedule.Create(4960, 3508, 18, true, true, execution, 16);
        Assert.Equal(64, exact.StripeRows); Assert.Equal(4, exact.Degree);
        Assert.Equal(candidateBytes, exact.WorkerTemporaryBytes);
        var below = FeatureStripeSchedule.Create(4960, 3508, 18, true, true,
            execution with { TemporaryMemoryBudget = execution.TemporaryMemoryBudget - 1 }, 16);
        Assert.Equal(128, below.StripeRows); Assert.Equal(2, below.Degree);
        var alreadyFour = FeatureStripeSchedule.Create(4960, 3508, 18, true, true,
            execution with { TemporaryMemoryBudget = currentBytes * 4 }, 16);
        Assert.Equal(128, alreadyFour.StripeRows); Assert.Equal(4, alreadyFour.Degree);
        var belowCurrentFour = FeatureStripeSchedule.Create(4960, 3508, 18, true, true,
            execution with { TemporaryMemoryBudget = currentBytes * 4 - 1 }, 16);
        Assert.Equal(64, belowCurrentFour.StripeRows); Assert.Equal(4, belowCurrentFour.Degree);
        var noBudget = FeatureStripeSchedule.Create(4960, 3508, 18, true, true,
            execution with { TemporaryMemoryBudget = 0 }, 16);
        Assert.Equal(128, noBudget.StripeRows); Assert.Equal(1, noBudget.Degree);
    }

    [Theory]
    [InlineData(448, 128, 2)]
    [InlineData(449, 64, 4)]
    [InlineData(511, 64, 4)]
    [InlineData(512, 64, 4)]
    [InlineData(513, 64, 4)]
    [InlineData(896, 64, 4)]
    [InlineData(897, 128, 4)]
    public void StripeCountBoundaryPreservesAtLeastTwoStripesPerWorker(int height, int rows, int degree)
    {
        var schedule = FeatureStripeSchedule.Create(73, height, 18, true, true, new()
            { MaxDegreeOfParallelism = 4, MinimumParallelPixels = 0, TemporaryMemoryBudget = long.MaxValue }, 16);
        Assert.Equal(rows, schedule.StripeRows); Assert.Equal(degree, schedule.Degree);
        var visits = new int[height];
        schedule.Run((first, last) =>
        {
            Assert.True(last - first >= 2);
            for (var y = first * schedule.StripeRows; y < Math.Min(height, last * schedule.StripeRows); y++)
                Interlocked.Increment(ref visits[y]);
        });
        Assert.All(visits, value => Assert.Equal(1, value));
    }

    [Theory]
    [InlineData(1, 449, 300, 0.3, 1.5, true)]
    [InlineData(37, 511, 300, 0.3, 1.5, true)]
    [InlineData(37, 512, 400, 0.3, 1.5, true)]
    [InlineData(37, 513, 400, 0.6, 1.5, true)]
    [InlineData(73, 2049, 300, 0.3, 1.5, true)]
    [InlineData(73, 2049, 400, 0.3, 1.5, true)]
    [InlineData(73, 2049, 400, 0.0, 1.5, true)]
    [InlineData(73, 2049, 300, 0.0, 1.5, false)]
    [InlineData(73, 2049, 400, 0.3, 1.5, false)]
    [InlineData(73, 2049, 400, 0.3, 20.0, true)]
    [InlineData(73, 2049, 300, 0.3, 0.001, true)]
    public void AdaptiveFeaturesMatchFullPageBytesAndKeepRoiParentUnchanged(
        int width, int height, int dpi, double edge, double radius, bool ink)
    {
        using var parent = new Mat(height + 8, width + 8, MatType.CV_8UC3);
        var random = new Random(261921);
        var pixels = parent.AsSpan<Vec3b>();
        for (var i = 0; i < pixels.Length; i++) pixels[i] = new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        using var image = new Mat(parent, new Rect(3, 3, width, height));
        var before = MatBuffers.Bytes(parent);
        var p = new ComparisonParameters { Dpi = dpi, Diff = new() { EdgeTolerance = edge }, Ink = new() { BackgroundRadiusMm = radius } };
        using var lab = ImageInk.ToLab(image);
        using var blur = new Mat(); using var maximum = new Mat(); using var minimum = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        if (edge != 0)
        {
            Cv2.Blur(lab, blur, new Size(3, 3)); Cv2.Dilate(lab, maximum, kernel); Cv2.Erode(lab, minimum, kernel);
            Cv2.Subtract(maximum, minimum, maximum);
        }
        using var expectedInk = ink ? ImageInk.FromLab(lab, dpi, p.Ink) : new Mat();
        var margin = Math.Max(edge == 0 ? 0 : 2, ink ? Math.Max(1, Units.RoundPixels(radius, dpi)) : 0);
        // 候補4workerだけが入る予算で、狭い合成画像にも製品の選択規則を適用する。
        var execution = new FeatureExecution { MaxDegreeOfParallelism = 4, MinimumParallelPixels = 0,
            TemporaryMemoryBudget = 4L * width * ((edge == 0 ? 0 : 12 * Math.Min(height, 64)) + 24L * Math.Min(height, 64 + 2 * margin)) };
        for (var repeat = 0; repeat < 2; repeat++)
        {
            using var actual = ComparisonFeatures.Create(image, p, ink, execution);
            Assert.Equal(Environment.ProcessorCount >= 4 ? 64 : 128, actual.StripeRows);
            var data = actual.Read();
            Assert.Equal(MemoryMarshal.AsBytes((edge == 0 ? lab : blur).AsSpan<Vec3f>()).ToArray(), MemoryMarshal.AsBytes(data.Values).ToArray());
            Assert.Equal(MemoryMarshal.AsBytes(maximum.AsSpan<Vec3f>()).ToArray(), MemoryMarshal.AsBytes(data.Contrast).ToArray());
            Assert.Equal(expectedInk.AsSpan<byte>().ToArray(), actual.Ink.AsSpan<byte>().ToArray());
        }
        Assert.Equal(before, MatBuffers.Bytes(parent));
    }

    [Fact]
    public void BorrowedLabHeaderDoesNotExtendTheLastStripeOrOwnTheBuffer()
    {
        using var image = new Mat(81, 3, MatType.CV_8UC3, Scalar.All(0));
        using (var bright = new Mat(image, new Rect(0, 70, 3, 11))) bright.SetTo(Scalar.All(255));
        using var cache = new LabStripeCache(3, 80);
        using (var first = cache.Get(image, 0, 80)) Assert.Equal(80, first.Rows);
        using (var last = cache.Get(image, 70, 81))
        {
            last.LocateROI(out var whole, out var offset);
            Assert.Equal(new Size(3, 11), whole); Assert.Equal(new Point(0, 0), offset);
            using var source = new Mat(image, new Rect(0, 70, 3, 11));
            using var expected = ImageInk.ToLab(source);
            Assert.Equal(MemoryMarshal.AsBytes(expected.AsSpan<Vec3f>()).ToArray(), MemoryMarshal.AsBytes(last.AsSpan<Vec3f>()).ToArray());
        }
        cache.Dispose(); cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cache.Get(image, 70, 81));
        Assert.False(image.IsDisposed);
    }

    [Fact]
    public void InjectedStripeFailureWaitsBeforeDisposingAllHeadersAndPartialFeatures()
    {
        if (Environment.ProcessorCount < 4) return;
        using var image = new Mat(1025, 73, MatType.CV_8UC3, Scalar.All(255));
        var execution = new FeatureExecution { MaxDegreeOfParallelism = 4, MinimumParallelPixels = 0,
            TemporaryMemoryBudget = 4L * 73 * (12 * 64 + 24 * 100) };
        using var started = new Barrier(4);
        var headers = new ConcurrentBag<Mat>();
        var owners = new ConcurrentBag<ComparisonFeatures>();
        var completed = 0;
        var error = Assert.Throws<AggregateException>(() => ComparisonFeatures.Create(image, new(), true, execution,
            (features, stripe, lab) =>
            {
                headers.Add(lab); owners.Add(features);
                if (stripe is not (0 or 4 or 8 or 12)) return;
                Assert.True(started.SignalAndWait(TimeSpan.FromSeconds(10)));
                if (stripe == 0) throw new InvalidOperationException("帯生成中の試験用失敗");
                Thread.Sleep(30);
                Assert.False(features.Ink.IsDisposed); Assert.False(lab.IsDisposed);
                Assert.False(image.IsDisposed);
                Interlocked.Increment(ref completed);
            }));
        Assert.Contains(error.InnerExceptions, ex => ex is InvalidOperationException { Message: "帯生成中の試験用失敗" });
        Assert.Equal(3, completed);
        Assert.All(headers, header => Assert.True(header.IsDisposed));
        Assert.All(owners, owner =>
        {
            Assert.True(owner.Ink.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => { owner.Read(); });
        });
        using var retry = ComparisonFeatures.Create(image, new(), true, execution);
        Assert.Equal(64, retry.StripeRows); Assert.Equal(0, Cv2.CountNonZero(retry.Ink));
        Assert.False(image.IsDisposed);
    }

    [Fact]
    public void FailedColorConversionDisposesTheCacheAndLeavesInputUsable()
    {
        using var image = new Mat(1025, 73, MatType.CV_8UC1, Scalar.All(255));
        var cache = new LabStripeCache(73, 100);
        Assert.ThrowsAny<Exception>(() =>
        {
            using (cache) using (var lab = cache.Get(image, 0, 82)) { }
        });
        Assert.Throws<ObjectDisposedException>(() => cache.Get(image, 0, 82));
        Assert.False(image.IsDisposed); Assert.Equal(1025 * 73, Cv2.CountNonZero(image));
    }
}
