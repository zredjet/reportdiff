using ReportDiff.Cli;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class RegionConfigurationTests
{
    internal const string One = "regions: [{name: 明細, page: all, x: 0, y: 0, w: 10, h: 10, profile: strict}]";

    [Theory]
    [InlineData("normal", 0.15, 0.3)] [InlineData("strict", 0, 0)] [InlineData("loose", 0.3, 0.3)]
    public void PageThenRegionProfileThenExplicitFieldsAndNestedRegionsInheritPage(string profile, double shift, double edge)
    {
        var settings = ConfigurationLoader.Load("""
            diff: {color_threshold: 9}
            regions:
              - {name: 外, page: all, x: 0, y: 0, w: 20, h: 20, profile: strict}
              - {name: 内, page: 1, x: 2, y: 2, w: 2, h: 2, diff: {color_threshold: 1}}
              - {name: 余白, page: 2, x: 30, y: 0, w: 10, h: 10, profile: loose, diff: {max_shift_mm: 0.1}}
            """, profile, 400);
        var one = settings.ForPage(1, 400);
        Assert.Equal(new DiffOptions { MaxShiftMm = 0, ColorThreshold = 9, EdgeTolerance = 0 }, one.Regions[0].Diff);
        Assert.Equal(new DiffOptions { MaxShiftMm = shift, ColorThreshold = 1, EdgeTolerance = edge }, one.Regions[1].Diff);
        Assert.Equal(0.1, settings.ForPage(2, 400).Regions[1].Diff.MaxShiftMm);
        Assert.Equal(400, settings.Dpi);
    }

    [Theory]
    [InlineData("name", "''")] [InlineData("name", "[]")]
    [InlineData("page", "0")] [InlineData("page", "-1")] [InlineData("page", "1.5")]
    [InlineData("x", "-1")] [InlineData("y", ".inf")]
    [InlineData("w", "0")] [InlineData("h", "-1")] [InlineData("w", "1e308")]
    [InlineData("mode", "anchor")] [InlineData("profile", "unknown")]
    [InlineData("diff", "{max_shift_mm: -1}")] [InlineData("diff", "{max_shift_mm: 1e100}")]
    [InlineData("diff", "{color_threshold: -1}")] [InlineData("diff", "{edge_tolerance: 1}")]
    [InlineData("diff", "{edge_tolerance: .nan}")] [InlineData("diff", "{unknown: 0}")]
    [InlineData("anchor", "{}")] [InlineData("float_mm", "1")] [InlineData("ink", "{}")]
    public void InvalidFieldsHaveIndexedKeys(string key, string value)
    {
        var fields = new Dictionary<string, string> { ["name"] = "明細", ["page"] = "all", ["x"] = "0", ["y"] = "0", ["w"] = "10", ["h"] = "10" };
        fields[key] = value;
        var yaml = "regions: [{" + string.Join(", ", fields.Select(f => f.Key + ": " + f.Value)) + "}]";
        Assert.Contains("regions[0]." + key, Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load(yaml)).Message);
    }

    [Theory]
    [InlineData("name")] [InlineData("page")] [InlineData("x")] [InlineData("y")] [InlineData("w")] [InlineData("h")]
    public void RequiredFieldsAreNamed(string key)
    {
        var fields = new Dictionary<string, string> { ["name"] = "明細", ["page"] = "all", ["x"] = "0", ["y"] = "0", ["w"] = "10", ["h"] = "10" };
        fields.Remove(key);
        var yaml = "regions: [{" + string.Join(", ", fields.Select(f => f.Key + ": " + f.Value)) + "}]";
        Assert.Contains("regions[0]." + key, Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load(yaml)).Message);
    }

    [Theory]
    [InlineData("profile: strict", "profile")] [InlineData("diff: {}", "diff")]
    public void ExclusionCannotHaveMeaninglessOverrides(string field, string key) => Assert.Contains("regions[0]." + key,
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load(One.Replace("profile: strict", "mode: exclude, " + field))).Message);

    [Fact]
    public void OverlapsAreValidatedInMillimetersAndAtBothFinalDpis()
    {
        const string prefix = "dpi: 254\nregions:\n - {name: A, page: all, x: 0, y: 0, w: 1, h: 1}\n - ";
        foreach (var other in new[] { "{name: B, page: 1, x: 0.5, y: 0.5, w: 1, h: 1}",
            "{name: B, page: all, x: 0, y: 0, w: 1, h: 1}", "{name: A, page: 2, x: 5, y: 5, w: 1, h: 1}" })
            Assert.Contains("regions[1]", Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load(prefix + other)).Message);
        var adjacent = prefix + "{name: B, page: all, x: 1, y: 0, w: 1, h: 1}";
        ConfigurationLoader.Load(adjacent);
        Assert.Contains("300dpi", Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load(adjacent, dpi: 300)).Message);
        Assert.Contains("300dpi", Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load("image_dpi: 300\n" + adjacent)).Message);
        ConfigurationLoader.Load(prefix + "{name: B, page: all, x: 0.01, y: 0.01, w: 0.98, h: 0.98}"); // 同じ px でも内包。
        ConfigurationLoader.Load(prefix.Replace("page: all", "page: 1") + "{name: B, page: 2, x: 0, y: 0, w: 1, h: 1}");
    }

    [Fact]
    public void LayerArraysReplaceClearOrInheritAndAuditPreservesValidatedDeclarations()
    {
        Assert.Single(ConfigurationLoader.LoadLayered(One, "diff: {color_threshold: 8}").Regions);
        Assert.Empty(ConfigurationLoader.LoadLayered(One, "regions: []").Regions);
        Assert.Equal("別", Assert.Single(ConfigurationLoader.LoadLayered(One, One.Replace("明細", "別")).Regions).Name);
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.LoadLayered(One.Replace("w: 10", "w: -1"), "regions: []"));
        var original = ConfigurationLoader.Load(One + "\nexclude: [{page: all, x: 0, y: 0, w: 1, h: 1}]\nalign: {enabled: true}");
        var command = CommandLine.Parse(["compare", "a", "b", "--out", "o", "--no-regions", "--profile", "loose"]);
        var settings = command.ApplyReportOptions(original);
        Assert.Empty(settings.Regions); Assert.Empty(settings.Exclude); Assert.True(settings.Align.Enabled);
        Assert.Single(settings.RegionAudit!.Regions); Assert.Single(settings.RegionAudit.Exclude);
        Assert.Equal("--no-regions", settings.RegionAudit.Reason);
        Assert.Same(settings, command.ApplyReportOptions(settings));
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["compare", "a", "b", "--out", "o", "--no-regions", "--no-regions"]));
        using var files = new DirectoryTestFiles();
        var common = files.Text("common.yaml", One);
        files.Text("selected.yaml", "regions: []");
        var rules = files.Text("rules.yaml", "schema_version: 1\nrules: [{pattern: invoice, config: selected.yaml}]");
        var loaded = new DirectoryRules(files.Command with { Config = common, Rules = rules, NoRegions = true });
        Assert.Single(loaded.Common.RegionAudit!.Regions);
        Assert.Empty(Assert.Single(loaded.Match("invoice.pdf", "invoice.pdf")).Settings.RegionAudit!.Regions);
        Assert.True(loaded.Description.Cli.NoRegions);
    }
}
