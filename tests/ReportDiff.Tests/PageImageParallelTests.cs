using System.Security.Cryptography;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

public sealed class PageImageParallelTests
{
    [Theory]
    [InlineData(999, 1000, 16, 2, 1)]
    [InlineData(1000, 1000, 16, 2, 2)]
    [InlineData(1001, 1000, 16, 8, 2)]
    [InlineData(1000, 1000, 1, 2, 1)]
    [InlineData(1000, 1000, 16, 1, 1)]
    [InlineData(1, 1_000_000, 2, 2, 2)]
    [InlineData(int.MaxValue, int.MaxValue, 16, 8, 2)]
    public void ScheduleBoundsWorkersByPixelsCpuAndTwoImages(int width, int height, int cpu, int limit, int expected)
    {
        Assert.Equal(expected, PageImageWriteSchedule.Create(width, height,
            new() { MaxDegreeOfParallelism = limit }, cpu).Degree);
    }

    [Fact]
    public void InvalidScheduleArgumentsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PageImageWriteSchedule.Create(0, 1, new()));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageImageWriteSchedule.Create(1, 0, new()));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageImageWriteSchedule.Create(1, 1, new(), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageImageWriteSchedule.Create(1, 1, new() { MaxDegreeOfParallelism = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageImageWriteSchedule.Create(1, 1, new() { MinimumParallelPixels = -1 }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void EachSideRunsExactlyOnceAndReturnsAfterCompletion(int limit)
    {
        var visits = new int[2]; var finished = 0;
        PageImageWriteSchedule.Create(1000, 1000, new() { MaxDegreeOfParallelism = limit }).Run(side =>
        {
            Interlocked.Increment(ref visits[side]);
            Interlocked.Increment(ref finished);
        });
        Assert.Equal(new[] { 1, 1 }, visits); Assert.Equal(2, finished);
    }

    [Fact]
    public void SerialFailurePreservesOrderAndDoesNotStartB()
    {
        var visits = new List<int>(); var failure = new IOException("Aの保存失敗");
        var schedule = PageImageWriteSchedule.Create(32, 32, new());
        Assert.Same(failure, Assert.Throws<IOException>(() => schedule.Run(side =>
        {
            visits.Add(side); throw failure;
        })));
        Assert.Equal(new[] { 0 }, visits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task FailureWaitsForOtherWorkerAndPreservesOriginalException(int failingSide)
    {
        if (Environment.ProcessorCount < 2) return;
        using var entered = new Barrier(2);
        using var failing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var completed = 0; var failure = new IOException("試験用の保存失敗");
        var schedule = PageImageWriteSchedule.Create(1000, 1000, new());
        var cancellation = TestContext.Current.CancellationToken;
        var run = Task.Run(() => schedule.Run(side =>
        {
            try
            {
                if (!entered.SignalAndWait(TimeSpan.FromSeconds(10), cancellation)) throw new TimeoutException();
                if (side == failingSide) { failing.Set(); throw failure; }
                if (!release.Wait(TimeSpan.FromSeconds(10), cancellation)) throw new TimeoutException();
            }
            finally { Interlocked.Increment(ref completed); }
        }), cancellation);
        Exception? actual = null;
        try
        {
            Assert.True(failing.Wait(TimeSpan.FromSeconds(10), cancellation));
            // 一方が失敗しても、もう一方が共有Matを使い終えるまで戻らない。
            await Task.Delay(30, cancellation);
            Assert.False(run.IsCompleted);
        }
        finally
        {
            release.Set();
            actual = await Record.ExceptionAsync(async () => await run);
        }
        Assert.Same(failure, actual);
        Assert.Equal(2, completed);
    }

    [Fact]
    public void BothFailuresChooseAAfterBothWorkersFinish()
    {
        var a = new UnauthorizedAccessException("A"); var b = new IOException("B");
        var finished = 0;
        var schedule = PageImageWriteSchedule.Create(1000, 1000, new(), 2);
        Assert.Same(a, Assert.Throws<UnauthorizedAccessException>(() => schedule.Run(side =>
        {
            Interlocked.Increment(ref finished);
            throw side == 0 ? a : b;
        })));
        Assert.Equal(2, finished);
    }

    [Theory]
    [InlineData("same", false)]
    [InlineData("same", true)]
    [InlineData("different", false)]
    [InlineData("too_different", false)]
    [InlineData("aligned_same", false)]
    [InlineData("aligned_different", false)]
    [InlineData("mismatch", true)]
    [InlineData("only_in_a", false)]
    [InlineData("only_in_b", false)]
    public void SerialAndParallelReportsAndAllImagesAreByteIdentical(string scenario, bool saveAll)
    {
        using var files = new ReportTestDirectory();
        using var parentA = new Mat(83, 109, MatType.CV_8UC3, Scalar.All(255));
        Cv2.Rectangle(parentA, new Rect(20, 30, 15, 8), new Scalar(10, 50, 80), -1);
        using var parentB = parentA.Clone();
        if (scenario is "different" or "aligned_different" or "mismatch")
            Cv2.Rectangle(parentB, new Rect(50, 40, 10, 6), Scalar.All(0), -1);
        if (scenario == "too_different") parentB.SetTo(Scalar.All(0));
        using var pair = new NormalizedPagePair(new Mat(parentA, new Rect(3, 2, 101, 77)),
            new Mat(parentB, new Rect(3, 2, 101, 77)), new(101, 77), new(scenario == "mismatch" ? 99 : 101, 77));
        Assert.False(pair.A.IsContinuous()); Assert.False(pair.B.IsContinuous());
        var beforeA = MatBuffers.Bytes(parentA); var beforeB = MatBuffers.Bytes(parentB);
        var aligned = scenario.StartsWith("aligned", StringComparison.Ordinal);
        // 補正済み画像をBとは別のMatとして渡し、保存対象の取り違えも検出する。
        using var corrected = aligned ? pair.A.Clone() : null;
        if (scenario == "aligned_different") Cv2.Rectangle(corrected!, new Rect(50, 40, 10, 6), Scalar.All(0), -1);
        if (aligned) Cv2.Rectangle(pair.B, new Rect(70, 40, 6, 6), Scalar.All(0), -1);
        beforeB = MatBuffers.Bytes(parentB);
        var settings = ConfigurationLoader.Load(profile: "strict") with { Move = new() { SearchMm = 0 } };
        using var comparison = PageComparer.Compare(pair.A, corrected ?? pair.B, settings.ForPage(1, 300));
        var correctedBefore = corrected is null ? null : MatBuffers.Bytes(corrected);
        var inputs = Inputs(scenario == "only_in_a" ? 2 : 1, scenario == "only_in_b" ? 2 : 1);
        var timestamp = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
        foreach (var degree in new[] { 1, 2 })
        {
            var output = Path.Combine(files.Root, $"保存 {degree}");
            var writer = new ReportWriter(output, inputs, settings.ToReportConfiguration(), saveAll, timestamp,
                new() { MaxDegreeOfParallelism = degree, MinimumParallelPixels = 0 });
            if (scenario.StartsWith("only_in", StringComparison.Ordinal)) writer.AddUnpairedPage(2, pair.A);
            else writer.AddComparedPage(1, pair, comparison, 300,
                alignment: aligned ? new("applied", "accepted", new(2, -1)) : null, correctedB: corrected);
            var report = writer.Complete(); HtmlReportWriter.Write(output, report);
            if (scenario == "same" && !saveAll) Assert.Null(report.Pages[0].Images.A);
            else Assert.True(Directory.GetFiles(output, "*.png", SearchOption.AllDirectories).Length >= 1);
            if (aligned) Assert.NotNull(report.Pages[0].Images.BOriginal);
        }
        Assert.Equal(Manifest(Path.Combine(files.Root, "保存 1")), Manifest(Path.Combine(files.Root, "保存 2")));
        Assert.Equal(beforeA, MatBuffers.Bytes(parentA)); Assert.Equal(beforeB, MatBuffers.Bytes(parentB));
        if (corrected is not null) Assert.Equal(correctedBefore, MatBuffers.Bytes(corrected));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    [InlineData("both")]
    public void ParallelFileFailureFaultsWriterAndWorkspaceKeepsOldResult(string blockedSide)
    {
        using var files = new ReportTestDirectory();
        Directory.CreateDirectory(files.Output);
        var sentinel = Path.Combine(files.Output, "旧結果.txt"); File.WriteAllText(sentinel, "保持");
        using var image = new Mat(1000, 1000, MatType.CV_8UC3, Scalar.All(255));
        using var pair = PageNormalizer.Normalize(image, image);
        using var comparison = PageComparer.Compare(pair.A, pair.B, new());
        string stage;
        using (var workspace = new OutputWorkspace(files.Output, true))
        {
            stage = workspace.StagingPath;
            // 公開コンストラクターの既定値で並列対象に入る。
            var writer = new ReportWriter(stage, Inputs(), new AppSettings().ToReportConfiguration(), true);
            Directory.CreateDirectory(Path.Combine(stage, "pages"));
            foreach (var side in new[] { "a", "b" })
                if (blockedSide == "both" || blockedSide == side)
                    Directory.CreateDirectory(Path.Combine(stage, $"pages/p001_{side}.png"));
            var error = Assert.Throws<ReportWriteException>(() => writer.AddComparedPage(1, pair, comparison, 300));
            Assert.Contains("書き込めません", error.Message);
            Assert.True(error.InnerException is IOException or UnauthorizedAccessException);
            Assert.Throws<ReportWriteException>(() => writer.Complete());
            Assert.Throws<ReportWriteException>(() => writer.AddComparedPage(1, pair, comparison, 300));
            Assert.False(File.Exists(Path.Combine(stage, "result.json")));
            Assert.False(File.Exists(Path.Combine(stage, "pages/p001_overlay.png")));
            if (blockedSide != "both" && Environment.ProcessorCount >= 2)
                Assert.True(File.Exists(Path.Combine(stage, $"pages/p001_{(blockedSide == "a" ? "b" : "a")}.png")));
            Assert.False(pair.A.IsDisposed); Assert.False(pair.B.IsDisposed);
        }
        Assert.False(Directory.Exists(stage));
        Assert.Equal("保持", File.ReadAllText(sentinel));
        Assert.Empty(Directory.EnumerateDirectories(files.Root, ".reportdiff-*"));
    }

    private static ReportInputs Inputs(int a = 1, int b = 1) =>
        new(new("旧 帳票.png", "png", a, new string('a', 64)), new("新 帳票.png", "png", b, new string('b', 64)));

    private static string[] Manifest(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .Order(StringComparer.Ordinal).ToArray();
}
