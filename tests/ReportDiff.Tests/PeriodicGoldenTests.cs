using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class PeriodicGoldenTests
{
    [Theory]
    [InlineData("I10", 2, 0, 1736)]
    [InlineData("I11", 0, 2, 1736)]
    [InlineData("I12", 4, 0, 7370)]
    public void SecondStageAbsorbsEvenWhenOppositeShiftsBothHaveZeroResidual(string id, int dx, int dy, int initialPixels)
    {
        var parameters = GoldenData.Parameters(GoldenData.Cases().Single(c => c.GetProperty("id").GetString() == id));
        using var a = SyntheticScene.Original(id);
        using var b = SyntheticScene.Changed(id);
        using var initial = TolerantDifference.Calculate(a, b, parameters with
        {
            Diff = parameters.Diff with { MaxShiftMm = 0 }
        });
        Assert.Equal(initialPixels, Cv2.CountNonZero(initial.RawMask));
        using var featuresA = ComparisonFeatures.Create(a, parameters, false);
        using var featuresB = ComparisonFeatures.Create(b, parameters, false);
        Assert.Equal(initialPixels, Residual(featuresA, featuresB, a.Width, a.Height, parameters.Diff, 0, 0));
        // 元の特徴量をずらし、初期候補だけでなくページ全画素を検証する。
        Assert.Equal(0, Residual(featuresA, featuresB, a.Width, a.Height, parameters.Diff, dx, dy));
        Assert.Equal(0, Residual(featuresA, featuresB, a.Width, a.Height, parameters.Diff, -dx, -dy));
        using var result = PageComparer.Compare(a, b, parameters);
        Assert.Equal("same", result.Status);
        Assert.Equal(0, result.RawPixels);
        Assert.Empty(result.Clusters);
        Assert.Equal(1, result.AbsorbedGroups);
        Assert.Equal(Math.Max(dx, dy), result.MaxShiftPx);
    }

    [Theory]
    [InlineData("D20", 60)]
    [InlineData("D21", 96)]
    public void MissingStripeCannotBeExplainedByAnyAllowedShift(string id, int rawPixels)
    {
        var parameters = GoldenData.Parameters(GoldenData.Cases().Single(c => c.GetProperty("id").GetString() == id));
        using var a = SyntheticScene.Original(id);
        using var b = SyntheticScene.Changed(id);
        using var featuresA = ComparisonFeatures.Create(a, parameters, false);
        using var featuresB = ComparisonFeatures.Create(b, parameters, false);
        var radius = Units.RoundPixels(parameters.Diff.MaxShiftMm, parameters.Dpi);
        var minimum = int.MaxValue;
        for (var dx = -radius; dx <= radius; dx++)
        for (var dy = -radius; dy <= radius; dy++)
            minimum = Math.Min(minimum, Residual(featuresA, featuresB, a.Width, a.Height, parameters.Diff, dx, dy));
        Assert.Equal(rawPixels, minimum);
        using var result = PageComparer.Compare(a, b, parameters);
        Assert.Equal("different", result.Status);
        Assert.Single(result.Clusters);
        Assert.Equal(rawPixels, result.RawPixels);
        Assert.Equal(0, result.AbsorbedGroups);
    }

    private static int Residual(ComparisonFeatures ownedA, ComparisonFeatures ownedB,
        int width, int height, DiffOptions options, int dx, int dy)
    {
        var a = ownedA.Read(); var b = ownedB.Read();
        var count = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var target = (y * width + x) * 3;
            var source = (Math.Clamp(y - dy, 0, height - 1) * width + Math.Clamp(x - dx, 0, width - 1)) * 3;
            for (var channel = 0; channel < 3; channel++)
            {
                var limit = (float)options.ColorThreshold + (float)options.EdgeTolerance
                    * MathF.Max(a.Contrast[target + channel], b.Contrast[source + channel]);
                if (MathF.Abs(a.Values[target + channel] - b.Values[source + channel]) <= limit) continue;
                count++;
                break;
            }
        }
        return count;
    }
}
