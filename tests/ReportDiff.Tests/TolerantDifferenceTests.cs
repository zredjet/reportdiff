using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class TolerantDifferenceTests
{
    public static IEnumerable<object[]> Cases => GoldenData.Cases().Select(c => new object[] { c.GetProperty("id").GetString()! });

    [Theory]
    [MemberData(nameof(Cases))]
    public void RawDifferenceMatchesReference(string id)
    {
        var expected = GoldenData.Cases().Single(c => c.GetProperty("id").GetString() == id);
        var p = GoldenData.Parameters(expected);
        using var a = GoldenData.Image(id, "a");
        using var b = GoldenData.Image(id, "b");
        using var result = TolerantDifference.Calculate(a, b, p);
        Assert.Equal(expected.GetProperty("absorbed_groups").GetInt32(), result.AbsorbedGroups);
        Assert.Equal(expected.GetProperty("max_shift_px").GetInt32(), result.MaxShiftPx);
        // 除外は T1-3 の責務。I05 では除外前に差分が残ることを確認する。
        if (p.Exclude.Count > 0)
        {
            Assert.True(Cv2.CountNonZero(result.RawMask) > 0);
            return;
        }
        var raw = expected.GetProperty("raw_pixels").GetInt32();
        Assert.InRange(Cv2.CountNonZero(result.RawMask), raw * 0.98, raw * 1.02);
        Assert.Equal(expected.GetProperty("status").GetString() == "same", Cv2.CountNonZero(result.RawMask) == 0);
    }

    [Fact]
    public void RejectsDifferentSizesAndFormats()
    {
        using var a = new Mat(3, 3, MatType.CV_8UC3, Scalar.All(255));
        using var b = new Mat(4, 3, MatType.CV_8UC3, Scalar.All(255));
        using var gray = new Mat(3, 3, MatType.CV_8UC1, Scalar.All(255));
        Assert.Throws<ArgumentException>(() => TolerantDifference.Calculate(a, b, new()));
        Assert.Throws<ArgumentException>(() => TolerantDifference.Calculate(a, gray, new()));
    }
}
