using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class GroupShiftSearchTests
{
    [Fact]
    public void IndexIncludesNonCandidatePixelsAndKeepsRowOrder()
    {
        var labels = new int[100]; var candidates = new byte[100];
        foreach (var pixel in new[] { 0, 1, 4, 9, 10, 11, 12, 19, 99 }) labels[pixel] = 1;
        labels[50] = 2; // 初期候補のないグループは探索しない。
        candidates[4] = candidates[99] = 255;
        var index = GroupRunIndex.TryCreate(labels, candidates, 10, 10, 3)!;
        Assert.NotNull(index);
        Assert.Equal(2, index.InitialCount(1)); Assert.Equal(9, index.PixelCount(1));
        Assert.Equal(new GroupRun[] { new(0, 0, 2), new(0, 4, 5), new(0, 9, 10),
            new(1, 0, 3), new(1, 9, 10), new(9, 9, 10) }, index.Runs(1).ToArray());
        Assert.Empty(index.Runs(0).ToArray()); Assert.Empty(index.Runs(2).ToArray());
    }

    [Fact]
    public void LocalIndexDoesNotLoseIntervalsWhenBackgroundIsSkipped()
    {
        var labels = new int[400]; var candidates = new byte[400];
        foreach (var pixel in new[] { 65, 66, 67, 85, 86, 87, 108 }) labels[pixel] = 1;
        candidates[66] = 255;
        var index = GroupRunIndex.TryCreate(labels, candidates, 20, 20, 2)!;
        Assert.Equal(new GroupRun[] { new(3, 5, 8), new(4, 5, 8), new(5, 8, 9) }, index.Runs(1).ToArray());
        Assert.Equal(7, index.PixelCount(1));
        Array.Clear(labels); Array.Clear(candidates);
        index = GroupRunIndex.TryCreate(labels, candidates, 20, 20, 1)!;
        Assert.Equal(0, index.RunCount);
    }

    [Fact]
    public void IndexBudgetIncludesConstructionBuffersAndHasALabelSizeLimit()
    {
        var labels = Enumerable.Repeat(1, 100).ToArray();
        var candidates = Enumerable.Repeat((byte)255, 100).ToArray();
        // 4本のint配列と終端offsetで36 bytes、10区間で120 bytes。
        Assert.Null(GroupRunIndex.TryCreate(labels, candidates, 10, 10, 2, 155));
        Assert.NotNull(GroupRunIndex.TryCreate(labels, candidates, 10, 10, 2, 156));
        Assert.Null(GroupRunIndex.TryCreate(labels, candidates, 10, 10, 2, 0));
        for (var i = 0; i < labels.Length; i++) labels[i] = i % 2;
        Assert.Null(GroupRunIndex.TryCreate(labels, candidates, 10, 10, 2, long.MaxValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.3)]
    [InlineData(0.6)]
    public void AllShiftsMatchIndependentExhaustiveEvaluation(double tolerance)
    {
        const int width = 17, height = 13;
        var random = new Random(702);
        var av = Enumerable.Range(0, width * height * 3).Select(_ => (float)random.Next(7)).ToArray();
        var bv = Enumerable.Range(0, av.Length).Select(_ => (float)random.Next(7)).ToArray();
        var ac = Enumerable.Range(0, av.Length).Select(_ => (float)random.Next(5)).ToArray();
        var bc = Enumerable.Range(0, av.Length).Select(_ => (float)random.Next(5)).ToArray();
        var labels = Enumerable.Range(0, width * height).Select(i => i % width < 8 ? 1 : 2).ToArray();
        var options = new DiffOptions { EdgeTolerance = tolerance, ColorThreshold = 2 };
        var shifts = (from dx in Enumerable.Range(-2, 5) from dy in Enumerable.Range(-2, 5)
            orderby Math.Abs(dx) + Math.Abs(dy), dx, dy select (dx, dy)).ToArray();
        bool Candidate(int pixel, int dx, int dy)
        {
            var source = Math.Clamp(pixel / width - dy, 0, height - 1) * width + Math.Clamp(pixel % width - dx, 0, width - 1);
            return Enumerable.Range(0, 3).Any(c => MathF.Abs(av[pixel * 3 + c] - bv[source * 3 + c])
                > 2f + (float)tolerance * MathF.Max(ac[pixel * 3 + c], bc[source * 3 + c]));
        }
        var candidates = Enumerable.Range(0, labels.Length).Select(i => Candidate(i, 0, 0) ? (byte)255 : (byte)0).ToArray();
        var index = GroupRunIndex.TryCreate(labels, candidates, width, height, 3)!;
        Assert.NotNull(index);
        var actual = new byte[labels.Length]; var expected = new byte[labels.Length];
        for (var group = 1; group <= 2; group++)
        {
            var pixels = Enumerable.Range(0, labels.Length).Where(i => labels[i] == group).ToArray();
            var best = Enumerable.Range(0, shifts.Length)
                .OrderBy(s => pixels.Count(i => Candidate(i, shifts[s].dx, shifts[s].dy))).First();
            var remaining = pixels.Count(i => Candidate(i, shifts[best].dx, shifts[best].dy));
            foreach (var pixel in pixels)
                if (Candidate(pixel, shifts[best].dx, shifts[best].dy)) expected[pixel] = 255;
            var result = GroupShiftSearch.Evaluate(new(av, ac), new(bv, bc), width, height,
                options, index.Runs(group), index.InitialCount(group), shifts, actual);
            Assert.Equal(new(remaining, best), result);
        }
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EqualScoresKeepTheFirstShiftAndZeroStopsTheSearch()
    {
        // 対象は中央画素。左右へのずれは両方一致し、先に来る -1 を採用する。
        float[] av = [0, 0, 0, 0, 0, 0, 0, 0, 0];
        float[] bv = [0, 0, 0, 10, 10, 10, 0, 0, 0];
        var raw = new byte[3];
        var result = GroupShiftSearch.Evaluate(new(av, []), new(bv, []), 3, 1,
            new() { EdgeTolerance = 0 }, [new(0, 1, 2)], 1, [(0, 0), (-1, 0), (1, 0)], raw);
        Assert.Equal(new(0, 1), result); Assert.Equal(new byte[3], raw);
        // 全候補が同数なら原位置を選ぶ。
        Array.Fill(bv, 10);
        result = GroupShiftSearch.Evaluate(new(av, []), new(bv, []), 3, 1,
            new() { EdgeTolerance = 0 }, [new(0, 1, 2)], 1, [(0, 0), (-1, 0), (1, 0)], raw);
        Assert.Equal(new(1, 0), result); Assert.Equal(new byte[] { 0, 255, 0 }, raw);
    }

    [Fact]
    public void PixelsThatWereInitiallyEqualStillParticipateAfterShifting()
    {
        // 原位置は左だけが差分。-2に動かすと左は一致するが右が新たに差分になる。
        // Bの参照先はグループ外でもよい。探索するA側の領域だけを固定する。
        float[] av = [10, 10, 10, 0, 0, 0, 0, 0, 0];
        float[] bv = [0, 0, 0, 0, 0, 0, 10, 10, 10];
        var raw = new byte[3];
        var result = GroupShiftSearch.Evaluate(new(av, []), new(bv, []), 3, 1,
            new() { EdgeTolerance = 0 }, [new(0, 0, 2)], 1, [(0, 0), (-1, 0), (-2, 0)], raw);
        Assert.Equal(new(1, 0), result);
        Assert.Equal(new byte[] { 255, 0, 0 }, raw);
    }
}
