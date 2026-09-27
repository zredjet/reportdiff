using ReportDiff.Cli;
using Xunit;

namespace ReportDiff.Tests;

public sealed class PageFlowConfigurationTests
{
    [Fact]
    public void Carry_is_opt_in_and_inherits_scalar_values_after_merging()
    {
        Assert.False(ConfigurationLoader.Load("rows: {enabled: true}").Rows.CarryEnabled);
        var inherited = ConfigurationLoader.LoadLayered("rows: {enabled: true}", "rows: {carry_enabled: true}");
        Assert.True(inherited.Rows.Enabled); Assert.True(inherited.Rows.CarryEnabled);
        var disabled = ConfigurationLoader.LoadLayered("rows: {enabled: true, carry_enabled: true}", "rows: {carry_enabled: false}");
        Assert.True(disabled.Rows.Enabled); Assert.False(disabled.Rows.CarryEnabled);
        Assert.Throws<ConfigurationException>(() => ConfigurationLoader.LoadLayered("rows: {enabled: true, carry_enabled: true}", "rows: {enabled: false}"));
    }

    [Theory]
    [InlineData("rows: {carry_enabled: true}")]
    [InlineData("rows: {enabled: false, carry_enabled: true}")]
    [InlineData("rows: {enabled: true, carry_enabled: yes}")]
    [InlineData("rows: {enabled: true, carry_enabled: 1}")]
    [InlineData("rows: {enabled: true, carry_enabled: [true]}")]
    public void Invalid_carry_values_and_dependencies_name_the_key(string yaml) =>
        Assert.Contains("rows.carry_enabled", Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Load(yaml)).Message);
}
