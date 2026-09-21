using System.Runtime.Versioning;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class PageMapTextTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PdfAnnotationUsesSelectedSideAndExcludesInCanvasCoordinates(bool exclude)
    {
        using var file = new TextFile(PdfFixture.CreateTextPage([new("日本語", 20, 200)]));
        using var reader = new PdfTextReader(file.Path);
        var map = new PageMap(new(240, 300), new(240, 300), new(260, 320),
            [new(0, 80, 0, 0), new(80, 20, null, 80), new(100, 220, 80, 100)]);
        DifferenceCluster[] clusters = [new(1, new(20, 110, 40, 20), 1), new(2, new(20, 90, 40, 20), 1),
            new(3, new(240, 90, 20, 40), 1)];
        RectMm[] exclusions = exclude ? [new(Units.PixelsToMm(22, 72), Units.PixelsToMm(113, 72), 1, 1)] : [];
        var a = reader.AnnotateMapped(1, map, PageSpace.A, 72, clusters, exclusions);
        var b = reader.AnnotateMapped(1, map, PageSpace.B, 72, clusters, exclusions);
        Assert.Empty(a.Warnings); Assert.Empty(b.Warnings);
        Assert.Equal(exclude ? "" : "日本語", a.TextByCluster[1]);
        Assert.Equal("", a.TextByCluster[2]);
        Assert.Equal("", b.TextByCluster[1]); Assert.Equal("日本語", b.TextByCluster[2]);
        Assert.Equal("", a.TextByCluster[3]); Assert.Equal("", b.TextByCluster[3]);
    }

    [Fact]
    public void LegacyAnnotationKeepsEmptyPageFastPathAndInvalidSizeWarning()
    {
        using var file = new TextFile(PdfFixture.CreateTextPage([new("日本語", 20, 200)]));
        using var reader = new PdfTextReader(file.Path);
        Assert.Empty(reader.Annotate(1, new(0, 0), 72, [], []).Warnings);
        DifferenceCluster[] clusters = [new(1, new(0, 0, 240, 300), 1)];
        Assert.Equal("TEXT_ANNOTATION_SKIPPED", Assert.Single(reader.Annotate(1, new(0, 0), 72, clusters, []).Warnings).Code);
        var movedOutside = reader.Annotate(1, new(240, 300), 72, clusters, [], new(int.MinValue, int.MinValue));
        Assert.Empty(movedOutside.Warnings); Assert.Equal("", movedOutside.TextByCluster[1]);
    }
}
