using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class ComparisonFeatureTests
{
    public static IEnumerable<object[]> Cases => TolerantDifferenceTests.Cases;

    [Theory]
    [MemberData(nameof(Cases))]
    public void FeaturesMatchFullPageForEveryGoldenInput(string id)
    {
        var test = GoldenData.Cases().Single(c => c.GetProperty("id").GetString() == id);
        var parameters = GoldenData.Parameters(test);
        using var a = GoldenData.Image(id, "a");
        using var b = GoldenData.Image(id, "b");
        AssertFullPageFeatures(a, parameters, true);
        AssertFullPageFeatures(b, parameters, true);
    }

    [Theory]
    [InlineData(1, 1, 300, 0.3, 1.5, true)]
    [InlineData(19, 1, 400, 0.3, 1.5, true)]
    [InlineData(1, 257, 300, 0.3, 1.5, true)]
    [InlineData(37, 127, 300, 0.3, 1.5, true)]
    [InlineData(37, 128, 400, 0.3, 1.5, true)]
    [InlineData(37, 129, 300, 0.3, 1.5, true)]
    [InlineData(37, 257, 400, 0.3, 1.5, true)]
    [InlineData(37, 385, 300, 0.3, 12.7, true)]
    [InlineData(37, 385, 400, 0.3, 20, true)]
    [InlineData(37, 257, 300, 0, 1.5, true)]
    [InlineData(37, 257, 400, 0, 1.5, false)]
    [InlineData(37, 257, 300, 0.3, 0.001, true)]
    [InlineData(37, 257, 400, 0.6, 1.5, false)]
    public void StripeEdgesAndNonContinuousInputPreserveExactValues(
        int width, int height, int dpi, double tolerance, double radius, bool includeInk)
    {
        using var parent = new Mat(height + 8, width + 8, MatType.CV_8UC3);
        var rows = parent.AsRows<Vec3b>();
        var random = new Random(8731);
        for (var y = 0; y < height + 8; y++)
        {
            var row = rows[y];
            for (var x = 0; x < row.Length; x++)
                row[x] = new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        }
        using var image = new Mat(parent, new Rect(3, 3, width, height));
        if (height > 1) Assert.False(image.IsContinuous());
        var original = MatBuffers.Bytes(parent);
        AssertFullPageFeatures(image, new()
        {
            Dpi = dpi,
            Diff = new() { EdgeTolerance = tolerance },
            Ink = new() { BackgroundRadiusMm = radius }
        }, includeInk);
        Assert.Equal(original, MatBuffers.Bytes(parent));
    }

    [Fact]
    public void DisposingFeaturesReleasesOwnedMatsAndRejectsNewViews()
    {
        using var image = new Mat(10, 10, MatType.CV_8UC3, Scalar.All(255));
        using var features = ComparisonFeatures.Create(image, new(), true);
        var ink = features.Ink;
        features.Dispose();
        features.Dispose();
        Assert.True(ink.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => { features.Read(); });
        Assert.False(image.IsDisposed);
    }

    private static void AssertFullPageFeatures(Mat image, ComparisonParameters parameters, bool includeInk)
    {
        // 変更前と同じ全ページ演算を独立した基準にし、帯の余白や型の違いを許容しない。
        using var lab = ImageInk.ToLab(image);
        using var blur = new Mat();
        using var maximum = new Mat();
        using var minimum = new Mat();
        using var contrast = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        var expectedValues = lab;
        if (parameters.Diff.EdgeTolerance != 0)
        {
            Cv2.Blur(lab, blur, new Size(3, 3));
            Cv2.Dilate(lab, maximum, kernel);
            Cv2.Erode(lab, minimum, kernel);
            Cv2.Subtract(maximum, minimum, contrast);
            expectedValues = blur;
        }
        using var features = ComparisonFeatures.Create(image, parameters, includeInk);
        var data = features.Read();
        Assert.Equal(MatBuffers.Floats(expectedValues), data.Values.ToArray());
        Assert.Equal(parameters.Diff.EdgeTolerance == 0 ? [] : MatBuffers.Floats(contrast), data.Contrast.ToArray());
        if (includeInk)
        {
            using var expectedInk = ImageInk.FromLab(lab, parameters.Dpi, parameters.Ink);
            Assert.Equal(0, Cv2.Norm(expectedInk, features.Ink, NormTypes.INF));
        }
        else
            Assert.True(features.Ink.Empty());
    }
}
