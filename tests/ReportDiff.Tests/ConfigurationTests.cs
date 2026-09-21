using ReportDiff.Cli;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void DefaultsMatchSpecification()
    {
        var p = ConfigurationLoader.Load();
        Assert.Equal(300, p.Dpi); Assert.Equal(300, p.ImageDpi);
        Assert.Equal(0.15, p.Diff.MaxShiftMm); Assert.Equal(3, p.Diff.ColorThreshold);
        Assert.Equal(0.3, p.Diff.EdgeTolerance);
        Assert.Equal(3, p.Cluster.MergeXMm); Assert.Equal(1, p.Cluster.MergeYMm);
        Assert.Equal(4, p.Cluster.MinPixels); Assert.Equal(500, p.Cluster.MaxClustersPerPage);
        Assert.Equal(0.30, p.Cluster.MaxDiffRatio);
        Assert.Equal(new MoveOptions(), p.Move);
        Assert.Equal(new AlignOptions(), p.Align);
        Assert.Equal(2, p.Report.CropMarginMm); Assert.Empty(p.Exclude);
    }

    [Theory]
    [InlineData("strict", 0, 0)]
    [InlineData("normal", 0.15, 0.3)]
    [InlineData("loose", 0.30, 0.3)]
    public void ExplicitProfileOverridesOnlyItsFields(string profile, double shift, double edge)
    {
        var p = ConfigurationLoader.Load("dpi: 200\ndiff: {max_shift_mm: 0.2, edge_tolerance: 0.1, color_threshold: 9}", profile, 400);
        Assert.Equal(400, p.Dpi); Assert.Equal(400, p.ImageDpi);
        Assert.Equal(shift, p.Diff.MaxShiftMm); Assert.Equal(edge, p.Diff.EdgeTolerance);
        Assert.Equal(9, p.Diff.ColorThreshold);
    }

    [Fact]
    public void FileValuesRemainWithoutExplicitProfileAndImageDpiIsIndependent()
    {
        var p = ConfigurationLoader.Load("dpi: 200\nimage_dpi: 150\ndiff: {max_shift_mm: 0.2}", dpi: 400);
        Assert.Equal(400, p.Dpi); Assert.Equal(150, p.ImageDpi);
        Assert.Equal(0.2, p.Diff.MaxShiftMm);
        Assert.Equal(200, ConfigurationLoader.Load("dpi: 200").ImageDpi);
    }

    [Theory]
    [InlineData("dppi: 300", "dppi")]
    [InlineData("diff: {threshold: 3}", "diff.threshold")]
    [InlineData("dpi: 71", "dpi")]
    [InlineData("dpi: 1201", "dpi")]
    [InlineData("dpi: 300.5", "dpi")]
    [InlineData("image_dpi: 0", "image_dpi")]
    [InlineData("diff: {edge_tolerance: 1}", "diff.edge_tolerance")]
    [InlineData("diff: {max_shift_mm: -1}", "diff.max_shift_mm")]
    [InlineData("diff: {color_threshold: NaN}", "diff.color_threshold")]
    [InlineData("cluster: {min_pixels: 0}", "cluster.min_pixels")]
    [InlineData("cluster: {max_clusters_per_page: 0}", "cluster.max_clusters_per_page")]
    [InlineData("cluster: {max_diff_ratio: 1.1}", "cluster.max_diff_ratio")]
    [InlineData("report: {crop_margin_mm: -1}", "report.crop_margin_mm")]
    [InlineData("exclude: [{page: 0, x: 0, y: 0, w: 1, h: 1}]", "page")]
    [InlineData("exclude: [{page: all, x: 0, y: 0, w: 1}]", "h")]
    [InlineData("exclude: [{page: all, x: -1, y: 0, w: 1, h: 1}]", "exclude.x")]
    public void InvalidSettingsAreJapaneseErrors(string yaml, string field)
    {
        var error = Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load(yaml));
        Assert.Contains("設定", error.Message); Assert.Contains(field, error.Message);
    }

    [Theory]
    [InlineData("move: {search_mm: -1}", "move.search_mm")]
    [InlineData("move: {search_mm: 20.01}", "move.search_mm")]
    [InlineData("move: {search_mm: NaN}", "move.search_mm")]
    [InlineData("move: {min_score: 0}", "move.min_score")]
    [InlineData("move: {min_score: 1.01}", "move.min_score")]
    [InlineData("move: {min_score: .inf}", "move.min_score")]
    [InlineData("move: {min_score_gap: 0}", "move.min_score_gap")]
    [InlineData("move: {min_score_gap: -0.1}", "move.min_score_gap")]
    [InlineData("move: {min_score_gap: 1.01}", "move.min_score_gap")]
    [InlineData("move: {min_score_gap: NaN}", "move.min_score_gap")]
    [InlineData("move: {search_px: 10}", "move.search_px")]
    public void InvalidMovementSettingsAreRejected(string yaml, string field)
    {
        Assert.Contains(field, Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load(yaml)).Message);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("strict")]
    [InlineData("loose")]
    public void MovementSettingsReachCoreAndReportWithoutProfileOverride(string profile)
    {
        var settings = ConfigurationLoader.Load("move: {search_mm: 20, min_score: 1, min_score_gap: 1}", profile, 400);
        Assert.Equal(new MoveOptions { SearchMm = 20, MinScore = 1, MinScoreGap = 1 }, settings.Move);
        Assert.Equal(settings.Move, settings.ForPage(1, 400).Move);
        Assert.Equal(settings.Move, settings.ToReportConfiguration().Move);
        Assert.Equal(0, ConfigurationLoader.Load("move: {search_mm: 0}", profile).Move.SearchMm);
    }

    [Theory]
    [InlineData("dpi: [")]
    [InlineData("dpi: 300\ndpi: 400")]
    [InlineData("dpi: 300\n---\ndpi: 400")]
    public void InvalidYamlIsRejected(string yaml) => Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load(yaml));

    [Fact]
    public void PageExclusionsAndUnitsAreCorrect()
    {
        var p = ConfigurationLoader.Load("exclude:\n- {page: all, x: 1, y: 2, w: 3, h: 4, note: 出力日時}\n- {page: 2, x: 5, y: 6, w: 7, h: 8}");
        Assert.Single(p.ForPage(1, 300).Exclude); Assert.Equal(2, p.ForPage(2, 300).Exclude.Count);
        Assert.Equal("出力日時", p.Exclude[0].Note);
        Assert.Equal(300, Units.MmToPixels(25.4, 300), 10);
        Assert.Equal(25.4, Units.PixelsToMm(300, 300), 10);
        Assert.Equal(2, Units.RoundPixels(0.15, 300));
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load(profile: "unknown"));
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load(dpi: 0));
    }

    [Theory]
    [InlineData("enabled: yes", "enabled")]
    [InlineData("max_shift_mm: -1", "max_shift_mm")]
    [InlineData("max_shift_mm: 20.1", "max_shift_mm")]
    [InlineData("max_shift_mm: NaN", "max_shift_mm")]
    [InlineData("min_score: 0", "min_score")]
    [InlineData("min_score: 1.1", "min_score")]
    [InlineData("min_score_gap: 0", "min_score_gap")]
    [InlineData("min_score_gap: .inf", "min_score_gap")]
    [InlineData("min_improvement: -1", "min_improvement")]
    [InlineData("min_improvement: 1.1", "min_improvement")]
    [InlineData("search_px: 10", "search_px")]
    public void InvalidAlignmentSettingsNameTheKey(string value, string key) =>
        Assert.Contains("align." + key, Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load("align: {" + value + "}")).Message);

    [Theory]
    [InlineData("strict")] [InlineData("normal")] [InlineData("loose")]
    public void CommentedAlignmentYamlOverridesDefaultsWithoutProfileOrDpiInterference(string profile)
    {
        var settings = ConfigurationLoader.Load("# 全体補正\nalign:\n  enabled: true # 明示的に有効化\n  max_shift_mm: 2.5\n  min_score: 0.99\n  min_score_gap: 0.04\n  min_improvement: 0.1\n", profile, 400);
        Assert.Equal(new AlignOptions { Enabled = true, MaxShiftMm = 2.5, MinScore = .99, MinScoreGap = .04, MinImprovement = .1 }, settings.Align);
        Assert.Equal(settings.Align, settings.ToReportConfiguration().Align);
        Assert.Equal(400, settings.Dpi); Assert.Equal(400, settings.ImageDpi);
        Assert.False(ConfigurationLoader.Load("align: {max_shift_mm: 0}").Align.Enabled);
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load("align: {enabled: true, enabled: false}"));
    }
}
