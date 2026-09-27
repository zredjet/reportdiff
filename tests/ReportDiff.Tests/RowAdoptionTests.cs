using System.Runtime.Versioning;
using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class RowAdoptionTests
{
    [Theory]
    [InlineData("R01", false)]
    [InlineData("R01", true)] // R03: R01の逆方向
    [InlineData("R02", false)]
    [InlineData("R02", true)]
    [InlineData("R04", false)]
    [InlineData("R04", true)]
    [InlineData("R05", false)]
    [InlineData("R05", true)]
    [InlineData("R06", false)]
    [InlineData("R06", true)]
    [InlineData("R07", false)]
    [InlineData("R07", true)]
    [InlineData("replacement", false)]
    [InlineData("replacement", true)]
    [InlineData("decimal", false)]
    [InlineData("decimal", true)]
    [InlineData("minus", false)]
    [InlineData("minus", true)]
    public void Adoption_uses_raw_improvement_and_preserves_known_content(string id, bool reverse)
    {
        using var input = new RowSelectionTests.Inputs(id);
        using var alignedB = ReferenceB(input.B, id);
        using var expected = PageComparer.Compare(reverse ? alignedB : input.A, reverse ? input.A : alignedB, new());
        using var result = RowComparer.Compare(reverse ? input.B : input.A, reverse ? input.A : input.B, new(), new() { Enabled = true },
            () => reverse ? (input.TextB, input.TextA) : (input.TextA, input.TextB));
        Assert.Equal("applied", result.Decision.Status);
        Assert.Equal(expected.RawPixels, result.Comparison.RawPixels);
        Assert.Equal(0, Cv2.Norm(expected.RawMask, result.Comparison.RawMask, NormTypes.INF));
        Assert.Equal(expected.Clusters, result.Comparison.Clusters);
        Assert.True(result.Decision.Improvement >= 0.05);
        Assert.Equal(result.Decision.CandidateRawPixels, result.Comparison.RawPixels);
        Assert.NotNull(result.ContentA); Assert.NotNull(result.ContentB);
        if (id == "R07") Assert.Equal(4, result.Comparison.Clusters.Count);
        else Assert.Equal(id switch { "replacement" or "minus" => 2, "R02" or "decimal" => 1, _ => 0 }, result.Comparison.Clusters.Count);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("identical")]
    [InlineData("no_text")]
    [InlineData("size_mismatch")]
    public void Ineligible_pages_skip_lazy_pdf_text_and_keep_baseline(string reason)
    {
        using var a = new Mat(100, 100, MatType.CV_8UC3, Scalar.All(255));
        using var b = a.Clone();
        if (reason != "identical") Cv2.Rectangle(b, new(30, 30, 10, 10), Scalar.All(0), -1);
        using var baseline = PageComparer.Compare(a, b, new());
        using var result = RowComparer.Compare(a, b, new(), new() { Enabled = reason != "disabled" },
            () => throw new InvalidOperationException("不要なPDF取得"), pdfPair: reason != "no_text", originalSizesEqual: reason != "size_mismatch");
        Assert.Equal(reason, result.Decision.Reason); Same(baseline, result);
    }

    [Theory]
    [InlineData("no_text")]
    [InlineData("column_conflict")]
    [InlineData("ambiguous")]
    [InlineData("low_improvement")]
    [InlineData("text_unavailable")]
    [InlineData("insufficient_support")]
    public void Rejected_alignment_returns_the_existing_comparison(string reason)
    {
        using var input = new RowSelectionTests.Inputs(reason == "column_conflict" ? "R09" : "R02");
        var options = new RowOptions { Enabled = true, MinImprovement = reason == "low_improvement" ? 1 : 0.05,
            MinScoreGap = reason == "ambiguous" ? 1 : 0.02, MinSupportBands = reason == "insufficient_support" ? 100 : 2 };
        using var expected = PageComparer.Compare(input.A, input.B, new());
        var textA = reason is "text_unavailable" or "no_text" ? new RowTextResult(reason, reason == "no_text" ? null : "parse_failed", []) : input.TextA;
        using var result = RowComparer.Compare(input.A, input.B, new(), options, () => (textA, input.TextB));
        Assert.Equal(reason, result.Decision.Reason); Same(expected, result);
        if (reason == "low_improvement") Assert.True(result.Decision.CandidateRawPixels > 0);
    }

    [Fact]
    public void Zero_baseline_is_not_an_alignment_improvement()
    {
        using var input = new RowSelectionTests.Inputs("R01");
        var parameters = new ComparisonParameters { Diff = new() { ColorThreshold = 1000, EdgeTolerance = 1, MaxShiftMm = 0 } };
        using var result = RowComparer.Compare(input.A, input.B, parameters, new() { Enabled = true }, () => (input.TextA, input.TextB));
        Assert.Equal("low_improvement", result.Decision.Reason); Assert.Equal("baseline_zero", result.Decision.Detail);
        Assert.Equal(0, result.Decision.BaselineRawPixels); Assert.Null(result.Decision.CandidateRawPixels); Assert.Null(result.Surface);
    }

    private static Mat ReferenceB(Mat b, string id)
    {
        // 合成PDFの既知の100px行境界から、推定器を使わず内容比較画像を作る。
        var result = new Mat(b.Size(), b.Type(), Scalar.All(255));
        Copy(0, 0, 400);
        if (id == "R04") { Copy(500, 400, 200); Copy(800, 600, b.Height - 800); }
        else if (id == "R05") { Copy(500, 400, 400); Copy(900, 900, b.Height - 900); }
        else Copy(500, 400, b.Height - 500);
        return result;
        void Copy(int source, int target, int length)
        {
            using var from = new Mat(b, new Rect(0, source, b.Width, length));
            using var to = new Mat(result, new Rect(0, target, b.Width, length)); from.CopyTo(to);
        }
    }

    private static void Same(PageComparison expected, RowComparison result)
    {
        Assert.Equal("skipped", result.Decision.Status == "disabled" ? "skipped" : result.Decision.Status);
        Assert.Equal(expected.Status, result.Comparison.Status); Assert.Equal(expected.Clusters, result.Comparison.Clusters);
        Assert.Equal(expected.RawPixels, result.Comparison.RawPixels); Assert.Equal(expected.NoiseDropped, result.Comparison.NoiseDropped);
        Assert.Equal(expected.AbsorbedGroups, result.Comparison.AbsorbedGroups); Assert.Equal(expected.MaxShiftPx, result.Comparison.MaxShiftPx);
        Assert.Equal(expected.Warnings, result.Comparison.Warnings);
        Assert.Equal(0, Cv2.Norm(expected.RawMask, result.Comparison.RawMask, NormTypes.INF));
        Assert.Equal(0, Cv2.Norm(expected.LabelMask, result.Comparison.LabelMask, NormTypes.INF));
        Assert.Null(result.Surface); Assert.Null(result.ContentA); Assert.Null(result.ContentB);
    }
}
