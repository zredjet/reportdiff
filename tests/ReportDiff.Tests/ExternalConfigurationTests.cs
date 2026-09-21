using System.Text.Json;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace ReportDiff.Tests;

public sealed class ExternalConfigurationTests
{
    internal const string CustomYaml = """
        # 承認済みの全追加項目を既定以外にする。
        ink: {background_radius_mm: 2, contrast_threshold: 30}
        cluster: {reading_band_mm: 7}
        move: {template_margin_mm: 0.5}
        align:
          coarse_max_side_samples: 512
          refine_radius_samples: 4
          min_support_cells: 4
          min_support_rows: 3
          min_support_columns: 3
          min_ink_area_mm2: 2.5
        text:
          max_letters_per_page: 50000
          max_words_per_page: 10000
          max_runes_per_cluster: 4 # 本文を短くする
          min_line_overlap: 0.75
        """;

    [Theory]
    [InlineData(null)] [InlineData("normal")] [InlineData("strict")] [InlineData("loose")]
    public void PartialCommentedYamlReachesConsumersAndOnlyExplicitOverridesApply(string? profile)
    {
        var p = ConfigurationLoader.Load(CustomYaml, profile, 400);
        Assert.Equal(new InkOptions { BackgroundRadiusMm = 2, ContrastThreshold = 30 }, p.Ink);
        Assert.Equal(7, p.Cluster.ReadingBandMm); Assert.Equal(0.5, p.Move.TemplateMarginMm);
        Assert.Equal(new AlignOptions { CoarseMaxSideSamples = 512, RefineRadiusSamples = 4, MinSupportCells = 4,
            MinSupportRows = 3, MinSupportColumns = 3, MinInkAreaMm2 = 2.5 }, p.Align);
        Assert.Equal(new TextOptions { MaxLettersPerPage = 50000, MaxWordsPerPage = 10000,
            MaxRunesPerCluster = 4, MinLineOverlap = 0.75 }, p.Text);
        Assert.Equal(400, p.Dpi); Assert.Equal(400, p.ImageDpi);
        Assert.Equal(p.Ink, p.ForPage(1, 400).Ink);
        Assert.Equal(p.Cluster, p.ForPage(1, 400).Cluster); Assert.Equal(p.Move, p.ForPage(1, 400).Move);
        var report = p.ToReportConfiguration();
        Assert.Equal(p.Ink, report.Ink); Assert.Equal(p.Text, report.Text); Assert.Equal(p.Align, report.Align);
        Assert.Equal(new ClusterOptions().MergeXMm, p.Cluster.MergeXMm);
    }

    [Theory]
    [InlineData("ink", "background_radius_mm", "0", "20.01", "0.01", "20")]
    [InlineData("ink", "contrast_threshold", "-1", "100.01", "0", "100")]
    [InlineData("cluster", "reading_band_mm", "0", "1000.01", "0.01", "1000")]
    [InlineData("move", "template_margin_mm", "-1", "20.01", "0", "20")]
    [InlineData("text", "max_letters_per_page", "0", "1000001", "1", "1000000")]
    [InlineData("text", "max_words_per_page", "0", "200001", "1", "200000")]
    [InlineData("text", "max_runes_per_cluster", "0", "100001", "1", "100000")]
    [InlineData("text", "min_line_overlap", "0", "1.01", "0.01", "1")]
    [InlineData("align", "coarse_max_side_samples", "63", "4097", "64", "4096")]
    [InlineData("align", "refine_radius_samples", "0", "9", "1", "8")]
    [InlineData("align", "min_support_cells", "0", "10", "1", "9")]
    [InlineData("align", "min_support_rows", "0", "4", "1", "3")]
    [InlineData("align", "min_support_columns", "0", "4", "1", "3")]
    [InlineData("align", "min_ink_area_mm2", "0", "10000.01", "0.01", "10000")]
    public void NewFieldsValidateBothBoundsAndTypesWithKeyedErrors(string group, string key,
        string tooLow, string tooHigh, string minimum, string maximum)
    {
        foreach (var value in new[] { minimum, maximum }) ConfigurationLoader.Load($"{group}: {{{key}: {value}}}");
        foreach (var value in new[] { tooLow, tooHigh, "NaN", ".inf", "true", "[]", "{}", "null" })
        {
            var error = Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load($"{group}: {{{key}: {value}}}"));
            Assert.Contains(group + "." + key, error.Message); Assert.Contains("設定", error.Message);
        }
    }

    [Theory]
    [InlineData("ink: {background_radius_mm: 2, background_radius_mm: 3}", "ink.background_radius_mm")]
    [InlineData("text: {max_runes_per_cluster: 3, max_runes_per_cluster: 4}", "text.max_runes_per_cluster")]
    [InlineData("dpi: 300\ndpi: 400", "dpi")]
    [InlineData("exclude: [{page: all, x: 0, x: 1, y: 0, w: 1, h: 1}]", "exclude[0].x")]
    [InlineData("ink: {radius: 2}", "ink.radius")]
    [InlineData("text: {max_words: 2}", "text.max_words")]
    [InlineData("align: {refine_radius_samples: 2.5}", "align.refine_radius_samples")]
    public void DuplicateUnknownAndNonintegerKeysAreIdentified(string yaml, string key) =>
        Assert.Contains(key, Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load(yaml)).Message);

    [Theory]
    [InlineData("diff: {max_shift_mm: 1e100}", "diff.max_shift_mm")]
    [InlineData("cluster: {merge_x_mm: 1e100}", "cluster.merge_x_mm")]
    [InlineData("cluster: {merge_y_mm: 1e100}", "cluster.merge_y_mm")]
    [InlineData("exclude: [{page: all, x: 1e308, y: 0, w: 1e308, h: 1}]", "exclude")]
    public void UnrepresentableDerivedValuesFailBeforeComparison(string yaml, string key) =>
        Assert.Contains(key, Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load(yaml)).Message);

    [Fact]
    public void ConversionChecksUseFinalDpiAndDisabledFeaturesStillValidate()
    {
        ConfigurationLoader.Load("dpi: 72\ndiff: {max_shift_mm: 1000}");
        Assert.Contains("diff.max_shift_mm", Assert.Throws<ConfigurationException>(() =>
            ConfigurationLoader.Load("dpi: 72\ndiff: {max_shift_mm: 1000}", dpi: 1200)).Message);
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load("align: {enabled: false, min_support_cells: 10}"));
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load("diff: {edge_tolerance: 2}", profile: "strict"));
        // 行列数は区画数より強い条件として働く。矛盾として排除しない。
        ConfigurationLoader.Load("align: {min_support_cells: 1, min_support_rows: 3, min_support_columns: 3}");
        Assert.Equal(100, Units.SquareMmToPixels(1, 254), 10);
    }

    [Fact]
    public void CompleteExampleCoversEveryOptionAndExactlyMatchesDefaults()
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "settings.yaml"));
        var expected = JsonSerializer.Serialize(ConfigurationLoader.Load().ToReportConfiguration(), ReportJson.Options);
        Assert.Equal(expected, JsonSerializer.Serialize(ConfigurationLoader.Load(yaml).ToReportConfiguration(), ReportJson.Options));
        var stream = new YamlStream(); stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        using var defaults = JsonDocument.Parse(expected);
        foreach (var group in new[] { "diff", "ink", "cluster", "move", "align", "text", "report" })
        {
            var mapping = (YamlMappingNode)root.Children[new YamlScalarNode(group)];
            Assert.Equal(defaults.RootElement.GetProperty(group).EnumerateObject().Select(p => p.Name).Order(),
                mapping.Children.Keys.Cast<YamlScalarNode>().Select(k => k.Value).Order());
        }
        var json = JsonSerializer.Serialize(ConfigurationLoader.Load("diff: {}\nink: {}\ncluster: {}\nmove: {}\nalign: {}\ntext: {}\nreport: {}").ToReportConfiguration(), ReportJson.Options);
        Assert.Equal(expected, json);
    }

    [Theory]
    [InlineData("minimal.yaml", 0.15, 3, 0.3, false)]
    [InlineData("strict.yaml", 0, 3, 0, false)]
    [InlineData("scan.yaml", 0.15, 10, 0.3, false)]
    [InlineData("align.yaml", 0.15, 3, 0.3, true)]
    public void UseCaseExamplesHaveDocumentedEffectiveValues(string name, double shift, double color, double edge, bool align)
    {
        var p = ConfigurationLoader.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", name)));
        Assert.Equal(shift, p.Diff.MaxShiftMm); Assert.Equal(color, p.Diff.ColorThreshold);
        Assert.Equal(edge, p.Diff.EdgeTolerance); Assert.Equal(align, p.Align.Enabled);
    }

    [Fact]
    public void LegacyYamlAndJsonSupplyNewDefaults()
    {
        var p = ConfigurationLoader.Load("""
            dpi: 300
            diff: {max_shift_mm: 0.15, color_threshold: 3, edge_tolerance: 0.3}
            cluster: {merge_x_mm: 3, merge_y_mm: 1, min_pixels: 4, max_clusters_per_page: 500, max_diff_ratio: 0.30}
            move: {search_mm: 5, min_score: 0.98, min_score_gap: 0.02}
            align: {enabled: false, max_shift_mm: 5, min_score: 0.98, min_score_gap: 0.02, min_improvement: 0.05}
            exclude: []
            report: {crop_margin_mm: 2}
            """);
        var legacy = JsonSerializer.Deserialize<ReportConfiguration>("""
            {"dpi":300,"image_dpi":300,"diff":{},"cluster":{},"move":{},"align":{},"exclude":[],"report":{"crop_margin_mm":2}}
            """, ReportJson.Options)!;
        var defaults = JsonSerializer.Serialize(new AppSettings().ToReportConfiguration(), ReportJson.Options);
        Assert.Equal(defaults, JsonSerializer.Serialize(p.ToReportConfiguration(), ReportJson.Options));
        Assert.Equal(defaults, JsonSerializer.Serialize(legacy, ReportJson.Options));
    }
}
