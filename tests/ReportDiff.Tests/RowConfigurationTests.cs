using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Pdf;
using Xunit;

namespace ReportDiff.Tests;

public sealed class RowConfigurationTests
{
    [Theory]
    [InlineData("enabled: maybe", "enabled")]
    [InlineData("max_shift_mm: 0", "max_shift_mm")]
    [InlineData("max_shift_mm: 101", "max_shift_mm")]
    [InlineData("min_word_match: 0", "min_word_match")]
    [InlineData("refine_mm: -0.1", "refine_mm")]
    [InlineData("refine_mm: 2.1", "refine_mm")]
    [InlineData("min_improvement: 1.1", "min_improvement")]
    [InlineData("min_score_gap: .inf", "min_score_gap")]
    [InlineData("min_support_bands: 1", "min_support_bands")]
    [InlineData("min_support_bands: 2.5", "min_support_bands")]
    [InlineData("min_support_ink_mm2: 0", "min_support_ink_mm2")]
    [InlineData("max_segments: 65", "max_segments")]
    [InlineData("typo: 1", "typo")]
    [InlineData("enabled: true, enabled: false", "enabled")]
    public void Invalid_rows_settings_name_the_key(string value, string key) => Assert.Contains("rows." + key,
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load("rows: {" + value + "}")).Message);

    [Fact]
    public void Layering_profiles_and_no_regions_keep_the_rows_contract()
    {
        var defaults = ConfigurationLoader.Load(); Assert.Equal(new RowOptions(), defaults.Rows);
        var settings = ConfigurationLoader.LoadLayered("rows: {enabled: true, max_shift_mm: 30, min_support_bands: 3}",
            "rows: {max_shift_mm: 40, refine_mm: 0}", "strict", 400);
        Assert.True(settings.Rows.Enabled); Assert.Equal(40, settings.Rows.MaxShiftMm);
        Assert.Equal(3, settings.Rows.MinSupportBands); Assert.Equal(0, settings.Rows.RefineMm);
        Assert.Equal(settings.Rows, settings.ToReportConfiguration().Rows);
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.LoadLayered("rows: {max_shift_mm: -1}", "rows: {max_shift_mm: 20}"));
    }

    [Fact]
    public void Global_text_mapping_translates_bounds_and_baselines_once_and_refuses_clipping()
    {
        var text = new RowTextResult("available", null, [new("TOTAL", new(10.1, 20.2, 40.3, 50.4), [49.5])]);
        var map = PageMap.Global(new(100, 100), new(100, 100), new(100, 100), new(3, -4));
        var mapped = RowTextAnnotations.ToAligned(text, map, PageSpace.B);
        var word = Assert.Single(mapped.Words); Assert.Equal("available", mapped.Status);
        Assert.Equal(new PageBounds(13.1, 16.2, 43.3, 46.4), word.Bounds); Assert.Equal([45.5], word.Baselines);
        var clipped = RowTextAnnotations.ToAligned(text, PageMap.Global(new(100, 100), new(100, 100), new(100, 100), new(-20, 0)), PageSpace.B);
        Assert.Equal("text_unavailable", clipped.Status); Assert.Equal("global_word_clipped", clipped.Detail);
    }
}
