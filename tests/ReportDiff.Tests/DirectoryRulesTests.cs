using System.Security.Cryptography;
using ReportDiff.Cli;
using Xunit;

namespace ReportDiff.Tests;

public sealed class DirectoryRulesTests
{
    [Fact]
    public void IdenticalAnchorNamesInSeparateFilesKeepTheirOwnResolvedValues()
    {
        var settings = ConfigurationLoader.LoadLayered(
            "diff: {color_threshold: &value 8}\nink: {contrast_threshold: *value}\n",
            "dpi: &value 144\nimage_dpi: *value\n");
        Assert.Equal(8, settings.Diff.ColorThreshold); Assert.Equal(8, settings.Ink.ContrastThreshold);
        Assert.Equal(144, settings.Dpi); Assert.Equal(144, settings.ImageDpi);
        var exclusions = ConfigurationLoader.LoadLayered("exclude: [&box {page: all, x: 1, y: 2, w: 3, h: 4}, *box]", "{}");
        Assert.Equal(2, exclusions.Exclude.Count); Assert.Equal(exclusions.Exclude[0], exclusions.Exclude[1]);
    }

    [Theory]
    [InlineData(null, null, 400)]
    [InlineData(180, null, 180)]
    [InlineData(null, 200, 200)]
    [InlineData(180, 200, 200)]
    public void PartialLayersPreserveValuesExclusionsAndImageDpiPresence(int? commonImage, int? selectedImage, int expected)
    {
        var common = "dpi: 144\ndiff: {color_threshold: 8, edge_tolerance: 0.4}\nexclude: [{page: all, x: 1, y: 2, w: 3, h: 4}]\n"
            + (commonImage is null ? "" : $"image_dpi: {commonImage}\n");
        var selected = "dpi: 300\ndiff: {edge_tolerance: 0.1}\n" + (selectedImage is null ? "" : $"image_dpi: {selectedImage}\n");
        var settings = ConfigurationLoader.LoadLayered(common, selected, "strict", 400);
        Assert.Equal(400, settings.Dpi); Assert.Equal(expected, settings.ImageDpi);
        Assert.Equal(8, settings.Diff.ColorThreshold); Assert.Equal(0, settings.Diff.EdgeTolerance); Assert.Single(settings.Exclude);
        Assert.Empty(ConfigurationLoader.LoadLayered(common, selected + "exclude: []\n").Exclude);
        Assert.Equal(.4, ConfigurationLoader.LoadLayered(common, "diff: {}\n").Diff.EdgeTolerance);
        Assert.Equal(2, ConfigurationLoader.LoadLayered(common, "exclude: [{page: 2, x: 0, y: 0, w: 1, h: 1}]\n").Exclude.Single().Page);
    }

    [Theory]
    [InlineData("dpi: 0", "dpi: 300")]
    [InlineData("diff: {color_threshold: NaN}", "diff: {color_threshold: 3}")]
    [InlineData("dpi: 200", "image_dpi: 0")]
    [InlineData("dpi: 300\ndpi: 400", "dpi: 300")]
    [InlineData("diff: {typo: 1}", "diff: {}")]
    public void InvalidLayerIsNotHiddenByLaterOverrides(string common, string selected) =>
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.LoadLayered(common, selected, "normal", 300));

    [Theory]
    [InlineData("")]
    [InlineData("schema_version: 2\nrules: []")]
    [InlineData("schema_version: 1")]
    [InlineData("rules: []")]
    [InlineData("schema_version: 1\nrules: {}")]
    [InlineData("schema_version: 1\nrules: [abc]")]
    [InlineData("schema_version: 1\nrules: [{pattern: a}]")]
    [InlineData("schema_version: 1\nrules: [{pattern: '', config: x}]")]
    [InlineData("schema_version: 1\nrules: [{pattern: a, config: ''}]")]
    [InlineData("schema_version: 1\nrules: []\nunknown: 1")]
    [InlineData("schema_version: 1\nrules: []\nrules: []")]
    [InlineData("schema_version: 1\nrules: [{pattern: a, pattern: b, config: x}]")]
    [InlineData("schema_version: 1\nrules: [{pattern: a, config: x, typo: 1}]")]
    [InlineData("schema_version: 1\nrules: [{pattern: [a], config: x}]")]
    [InlineData("schema_version: 1\nrules: []\n---\nrules: []")]
    [InlineData("schema_version: 1\nrules: [{pattern: '(?<=a)b', config: x}]")]
    [InlineData("schema_version: 1\nrules: [{pattern: '(', config: x}]")]
    public void InvalidRuleSchemaOrRegexIsRejected(string yaml)
    {
        using var files = new DirectoryTestFiles();
        var command = files.Command with { Rules = files.Text("rules.yaml", yaml) };
        Assert.Throws<ConfigurationException>(() => new DirectoryRules(command));
    }

    [Fact]
    public void RulesResolveRelativeConfigAndMatchEitherSideOnceWithSnapshots()
    {
        using var files = new DirectoryTestFiles();
        var config = files.Text("settings/partial.yaml", "# 注釈\ndiff: {color_threshold: 8}");
        var rulesPath = files.Text("rules/selection.yaml", "schema_version: 1\nrules:\n- pattern: '\\A帳票/が\\.pdf\\z'\n  config: ../settings/partial.yaml\n");
        var rules = new DirectoryRules(files.Command with { Rules = rulesPath });
        var match = Assert.Single(rules.Match("帳票/か\u3099.PDF", "帳票/が.pdf"));
        Assert.Equal(8, match.Settings.Diff.ColorThreshold); Assert.Equal(config, match.Description.Config);
        Assert.Equal(rules.Match("x.pdf", "帳票/が.pdf"), rules.Match("帳票/が.pdf", "x.pdf"));
        Assert.Empty(rules.Match("other.pdf", "other.pdf"));
        var source = Assert.Single(rules.Description.Referenced);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(config))), source.Sha256);
        File.WriteAllText(config, "invalid: true");
        Assert.Equal(8, Assert.Single(rules.Match("帳票/が.pdf", "帳票/が.pdf")).Settings.Diff.ColorThreshold);
        Assert.Equal(2, rules.ProtectedFiles.Count());
    }

    [Theory]
    [InlineData("absent.yaml", null)]
    [InlineData("invalid.yaml", "dpi: 0")]
    public void UnusedReferenceIsStillValidated(string name, string? yaml)
    {
        using var files = new DirectoryTestFiles();
        if (yaml is not null) files.Text(name, yaml);
        var path = files.Text("rules.yaml", $"schema_version: 1\nrules: [{{pattern: never, config: {name}}}]");
        Assert.Throws<ConfigurationException>(() => new DirectoryRules(files.Command with { Rules = path }));
    }

    [Fact]
    public void EmptyRulesAreValidAndRulesAreOnlyAllowedForDirectoryCommand()
    {
        using var files = new DirectoryTestFiles();
        var path = files.Text("rules.yaml", "schema_version: 1\nrules: []");
        Assert.Empty(new DirectoryRules(files.Command with { Rules = path }).Rules);
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["compare", "a", "b", "--out", "out", "--rules", path]));
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["compare-dir", "a", "b", "--out", "out", "--rules", path, "--rules", path]));
    }

    [Fact]
    public void BundledRulesAndTheirRelativeReferencesLoad()
    {
        using var files = new DirectoryTestFiles();
        var rules = new DirectoryRules(files.Command with { Rules = Path.Combine(AppContext.BaseDirectory, "examples/batch/rules.yaml") });
        Assert.Equal(2, rules.Rules.Count);
        Assert.Single(rules.Match("請求/見積.PDF", "請求/見積.pdf"));
        Assert.Single(rules.Match("スキャン/帳票.JPG", "スキャン/帳票.jpg"));
    }
}
