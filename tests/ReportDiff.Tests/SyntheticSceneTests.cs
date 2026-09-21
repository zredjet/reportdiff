using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class SyntheticSceneTests
{
    public static IEnumerable<object[]> Cases => TolerantDifferenceTests.Cases.Where(c => !((string)c[0]).StartsWith('J'));

    [Theory]
    [MemberData(nameof(Cases))]
    public void GeneratedScenePreservesInputsAndExpectedDetection(string id)
    {
        using var a = SyntheticScene.Original(id);
        using var b = SyntheticScene.Changed(id);
        using var goldenA = GoldenData.Image(id, "a");
        using var goldenB = GoldenData.Image(id, "b");
        Assert.Equal(0, Cv2.Norm(a, goldenA, NormTypes.INF));
        Assert.Equal(0, Cv2.Norm(b, goldenB, NormTypes.INF));
        var expected = GoldenData.Cases().Single(c => c.GetProperty("id").GetString() == id);
        var parameters = GoldenData.Parameters(expected);
        using var result = PageComparer.Compare(a, b, parameters);
        Assert.Equal(expected.GetProperty("status").GetString(), result.Status);
        var boxes = expected.GetProperty("clusters").EnumerateArray().ToArray();
        Assert.Equal(boxes.Length, result.Clusters.Count);
        for (var i = 0; i < boxes.Length; i++)
        {
            var box = boxes[i]; var actual = result.Clusters[i].Bounds;
            Assert.InRange(actual.X, box.GetProperty("x").GetInt32() - 1, box.GetProperty("x").GetInt32() + 1);
            Assert.InRange(actual.Y, box.GetProperty("y").GetInt32() - 1, box.GetProperty("y").GetInt32() + 1);
            Assert.InRange(actual.Width, box.GetProperty("w").GetInt32() - 1, box.GetProperty("w").GetInt32() + 1);
            Assert.InRange(actual.Height, box.GetProperty("h").GetInt32() - 1, box.GetProperty("h").GetInt32() + 1);
        }
        if (id.StartsWith('I') || id == "S02") Assert.Equal("same", result.Status);
        else if (!id.StartsWith('L')) Assert.Equal("different", result.Status);
        if (id == "D11")
            Assert.Contains(result.Clusters, c => c.Bounds.X < Units.MmToPixels(49.5, 300)
                && c.Bounds.Right > Units.MmToPixels(46, 300)
                && c.Bounds.Y < Units.MmToPixels(14.5, 300) && c.Bounds.Bottom > Units.MmToPixels(10.5, 300)
                && c.Bounds.Width < Units.MmToPixels(12, 300) && c.Bounds.Height < Units.MmToPixels(8, 300));
    }
}
