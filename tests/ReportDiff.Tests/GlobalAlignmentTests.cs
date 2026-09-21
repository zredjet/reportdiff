using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class GlobalAlignmentTests
{
    private static readonly AlignOptions Enabled = new() { Enabled = true };

    [Theory]
    [InlineData(100, 9, 0)] [InlineData(100, -9, 0)]
    [InlineData(100, 0, 6)] [InlineData(100, 0, -6)]
    [InlineData(100, 9, 6)] [InlineData(100, -9, 6)]
    [InlineData(100, 9, -6)] [InlineData(100, -9, -6)]
    [InlineData(300, 29, -19)] [InlineData(300, -29, 19)]
    public void CorrectsAllDirectionsWithoutChangingSourceOrResampling(int dpi, int dx, int dy)
    {
        using var a = AlignmentFixture.Render(dpi); using var savedA = a.Clone();
        using var b = AlignmentFixture.Shift(a, dx, dy); using var savedB = b.Clone();
        var result = GlobalAligner.Estimate(a, b, new() { Dpi = dpi }, Enabled);
        Assert.Equal("applied", result.Status); Assert.Equal(new(-dx, -dy), result.EstimatedShiftPx);
        Assert.Equal(1, result.ScoreAfter); Assert.True(result.SupportCells >= 3);
        using var corrected = GlobalAligner.TranslateB(b, result.EstimatedShiftPx!);
        Assert.Equal(0, Cv2.Norm(a, corrected, NormTypes.INF));
        Assert.Equal(0, Cv2.Norm(a, savedA, NormTypes.INF)); Assert.Equal(0, Cv2.Norm(b, savedB, NormTypes.INF));
    }

    [Theory]
    [InlineData(72)] [InlineData(100)] [InlineData(300)] [InlineData(400)]
    public void SearchBoundaryIsFlooredAndOutOfRangeIsNotApplied(int dpi)
    {
        using var a = AlignmentFixture.Render(dpi);
        var radius = (int)Math.Floor(Units.MmToPixels(5, dpi));
        using var b = AlignmentFixture.Shift(a, radius, -radius);
        var result = GlobalAligner.Estimate(a, b, new() { Dpi = dpi }, Enabled);
        Assert.Equal("applied", result.Status); Assert.Equal(new(-radius, radius), result.EstimatedShiftPx);
        using var outside = AlignmentFixture.Shift(a, radius + 1, -radius);
        Assert.NotEqual("applied", GlobalAligner.Estimate(a, outside, new() { Dpi = dpi }, Enabled).Status);
    }

    [Theory]
    [InlineData(100, false)] [InlineData(300, false)] [InlineData(300, true)]
    public void ContentChangeMatchesUnshiftedReferenceIncludingClassification(int dpi, bool strict)
    {
        using var a = AlignmentFixture.Render(dpi); using var changed = AlignmentFixture.Render(dpi, true);
        using var b = AlignmentFixture.Shift(changed, 9, -6);
        var p = new ComparisonParameters { Dpi = dpi, Diff = strict ? new() { MaxShiftMm = 0, EdgeTolerance = 0 } : new() };
        var result = GlobalAligner.Estimate(a, b, p, Enabled);
        Assert.Equal("applied", result.Status);
        using var corrected = GlobalAligner.TranslateB(b, result.EstimatedShiftPx!);
        Assert.Equal(0, Cv2.Norm(changed, corrected, NormTypes.INF));
        using var reference = PageComparer.Compare(a, changed, p);
        using var comparison = PageComparer.Compare(a, corrected, p);
        Assert.Equal("different", comparison.Status); Assert.Single(comparison.Clusters);
        Assert.Equal(reference.RawPixels, comparison.RawPixels); Assert.Equal(reference.Clusters, comparison.Clusters);
        Assert.Equal(0, Cv2.Norm(reference.RawMask, comparison.RawMask, NormTypes.INF));
        Assert.Equal(0, Cv2.Norm(reference.LabelMask, comparison.LabelMask, NormTypes.INF));
    }

    [Fact]
    public void DisabledZeroRangeAndSizeMismatchDoNotEstimate()
    {
        using var a = AlignmentFixture.Render(100); using var b = AlignmentFixture.Shift(a, 9, 6);
        Assert.Equal(AlignmentResult.Disabled, GlobalAligner.Estimate(a, b, new() { Dpi = 100 }, new()));
        Assert.Equal(AlignmentResult.Skipped("zero_range"), GlobalAligner.Estimate(a, b, new(), Enabled with { MaxShiftMm = 0 }));
        Assert.Equal(AlignmentResult.Skipped("size_mismatch"), GlobalAligner.Estimate(a, b, new(), Enabled, true));
        using var small = new Mat(10, 10, MatType.CV_8UC3, Scalar.All(255));
        Assert.Equal("size_mismatch", GlobalAligner.Estimate(a, small, new(), Enabled).Reason);
        Assert.Equal("insufficient_area", GlobalAligner.Estimate(small, small, new(), Enabled).Reason);
    }

    [Fact]
    public void BlankAndSingleObjectDoNotProduceGlobalCorrection()
    {
        using var a = new Mat(600, 600, MatType.CV_8UC3, Scalar.All(255));
        Assert.Equal("insufficient_information", GlobalAligner.Estimate(a, a, new() { Dpi = 100 }, Enabled).Reason);
        Cv2.PutText(a, "12345", new(280, 290), HersheyFonts.HersheySimplex, 1, Scalar.All(0), 2);
        using var b = AlignmentFixture.Shift(a, 9, 6);
        Assert.Equal("insufficient_support", GlobalAligner.Estimate(a, b, new() { Dpi = 100 }, Enabled).Reason);
    }

    [Theory]
    [InlineData(-9, -6)] [InlineData(9, -6)] [InlineData(-9, 6)] [InlineData(9, 6)]
    public void DoesNotCropEvenOneNonwhitePixelAtAnyEdge(int dx, int dy)
    {
        using var a = AlignmentFixture.Render(100); using var b = AlignmentFixture.Shift(a, dx, dy);
        // 非白の 1 チャンネルが 1 だけ違う画素でも、除外を理由に捨てない。
        var x = dx > 0 ? 0 : b.Width - 1; var y = dy > 0 ? 0 : b.Height - 1;
        b.Set(y, x, new Vec3b(254, 255, 255));
        var p = new ComparisonParameters { Dpi = 100, Exclude = [new(Units.PixelsToMm(x, 100), Units.PixelsToMm(y, 100), 1, 1)] };
        Assert.Equal("edge_content", GlobalAligner.Estimate(a, b, p, Enabled).Reason);
    }

    [Fact]
    public void PeriodicGridAndFractionalShiftsAreNotForcedIntoAnIntegerCandidate()
    {
        using var a = new Mat(600, 600, MatType.CV_8UC3, Scalar.All(255));
        for (var pos = 0; pos < 600; pos += 9)
        {
            Cv2.Line(a, new(pos, 0), new(pos, 599), Scalar.All(0));
            Cv2.Line(a, new(0, pos), new(599, pos), Scalar.All(0));
        }
        using var b = AlignmentFixture.Shift(a, 4, 3);
        Assert.Equal("ambiguous", GlobalAligner.Estimate(a, b, new() { Dpi = 100 }, Enabled).Reason);
        using var source = AlignmentFixture.Render(100); using var fractional = AlignmentFixture.Shift(source, 9.5, 6.5);
        Assert.NotEqual("applied", GlobalAligner.Estimate(source, fractional, new() { Dpi = 100 }, Enabled).Status);
    }

    [Fact]
    public void ExcludedContentIsNotUsedForEstimationAndExclusionRemainsInCorrectedCoordinates()
    {
        using var a = AlignmentFixture.Render(100); using var b = AlignmentFixture.Shift(a, 9, 6);
        var area = new Rect(300, 400, 100, 100);
        Cv2.Rectangle(b, new Rect(area.X + 12, area.Y + 9, area.Width - 6, area.Height - 6), Scalar.All(0), -1);
        var p = new ComparisonParameters { Dpi = 100, Exclude = [new(Units.PixelsToMm(area.X, 100),
            Units.PixelsToMm(area.Y, 100), Units.PixelsToMm(area.Width, 100), Units.PixelsToMm(area.Height, 100))] };
        var result = GlobalAligner.Estimate(a, b, p, Enabled);
        Assert.Equal("applied", result.Status); Assert.Equal(new(-9, -6), result.EstimatedShiftPx);
        using var corrected = GlobalAligner.TranslateB(b, result.EstimatedShiftPx!);
        using var compared = PageComparer.Compare(a, corrected, p);
        Assert.Equal("same", compared.Status);
        Assert.Equal("insufficient_area", GlobalAligner.Estimate(a, b, p with { Exclude = [new(0, 0, 210, 297)] }, Enabled).Reason);
    }

    [Fact]
    public void NoncontinuousImagesMatchIndependentContinuousCopies()
    {
        using var original = AlignmentFixture.Render(100); using var shifted = AlignmentFixture.Shift(original, 9, 6);
        using var parentA = new Mat(original.Height + 10, original.Width + 10, MatType.CV_8UC3, Scalar.All(0));
        using var parentB = parentA.Clone();
        using var a = new Mat(parentA, new Rect(5, 5, original.Width, original.Height));
        using var b = new Mat(parentB, new Rect(5, 5, original.Width, original.Height));
        original.CopyTo(a); shifted.CopyTo(b); Assert.False(a.IsContinuous());
        var expected = GlobalAligner.Estimate(original, shifted, new() { Dpi = 100 }, Enabled);
        Assert.Equal(expected, GlobalAligner.Estimate(a, b, new() { Dpi = 100 }, Enabled));
        using var result = GlobalAligner.TranslateB(b, expected.EstimatedShiftPx!);
        Assert.Equal(0, Cv2.Norm(original, result, NormTypes.INF));
    }

    [Fact]
    public void EachThresholdCanConservativelyDeclineTheSameCandidate()
    {
        using var a = AlignmentFixture.Render(100); using var changed = AlignmentFixture.Render(100, true);
        using var b = AlignmentFixture.Shift(changed, 9, 6);
        var p = new ComparisonParameters { Dpi = 100 };
        Assert.Equal("low_score", GlobalAligner.Estimate(a, b, p, Enabled with { MinScore = 1 }).Reason);
        Assert.Equal("low_improvement", GlobalAligner.Estimate(a, b, p, Enabled with { MinImprovement = 1 }).Reason);
        Assert.Equal("ambiguous", GlobalAligner.Estimate(a, b, p, Enabled with { MinScoreGap = 1 }).Reason);
    }
}
