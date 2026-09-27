using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

public sealed class RowOutputTests
{
    [Fact]
    public void Split_yaml_does_not_cover_the_unrelated_deleted_band()
    {
        var cluster = new ReportCluster(1, new(42, 78, 2, 44), new(4.2, 7.8, 0.2, 4.4), 8, 8.0 / 88,
            "changed", null, null, null, new("crops/a.png", "crops/b.png", "crops/d.png"))
            { DisplayPartsPx = [new(42, 78, 2, 2), new(42, 120, 2, 2)] };
        var row = ReportRowAlignment.Skipped("applied", "pdf_text") with { Status = "applied", AlignedSizePx = new(80, 240),
            Segments = [new(0, 80, 0, 0, 0, "paired"), new(80, 40, 80, null, null, "structural"), new(120, 120, 120, 80, 40, "paired")] };
        var page = new ReportPage(1, "different", new(80, 240), false, 8, 0, 0, 0, new(null, null, null), [cluster]) { RowAlignment = row };
        var yaml = ExclusionSnippet.Create(page, cluster, 254, 0);
        var regions = ConfigurationLoader.Load("dpi: 254\nexclude:\n" + string.Join('\n', yaml.Split('\n').Select(s => "  " + s))).Exclude;
        Assert.Equal(2, regions.Count);
        Assert.Equal([7.5, 12], regions.Select(r => r.Y)); Assert.Equal([0.5, 0.5], regions.Select(r => r.H));
        Assert.DoesNotContain(regions, r => r.Y < 10 && r.Y + r.H > 10);
    }

    [Fact]
    public void Split_cluster_text_excludes_words_in_the_gap_but_structural_text_keeps_them()
    {
        var map = new PageMap(new(80, 240), new(80, 200), new(80, 240),
            [new(0, 80, 0, 0), new(80, 40, 80, null), new(120, 120, 120, 80)]);
        var text = new RowTextResult("available", null, [Word("HEAD", 78), Word("UNRELATED", 90), Word("TAIL", 120)]);
        var cluster = new DifferenceCluster(1, new(42, 78, 2, 44), 8) { Row = new(1, new(42, 78, 2, 4), 1,
            [new(new(42, 78, 2, 2), new(42, 78, 2, 2), null, null, 4), new(new(42, 80, 2, 2), new(42, 120, 2, 2), null, null, 4)], null, null) };
        var result = RowTextAnnotations.Create(text, map, PageSpace.A, 254, [cluster], [], new());
        Assert.Equal("HEAD\nTAIL", result.TextByCluster[1]);
        var structure = RowTextAnnotations.Create(text, map, PageSpace.A, 254, [new(1, new(0, 80, 80, 40), 0)], [], new());
        Assert.Equal("UNRELATED", structure.TextByCluster[1]);
        var excluded = RowTextAnnotations.Create(text, map, PageSpace.A, 254, [cluster], [new(4.1, 12.1, 0.05, 0.05)], new());
        Assert.Equal("HEAD", excluded.TextByCluster[1]); // 連続座標で語との正面積交差を除外
        static RowWord Word(string value, int y) => new(value, new(41, y, 45, y + 2), [y + 1]);
    }
}
