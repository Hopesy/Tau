using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentTelemetryTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("YES")]
    public void IsTruthyEnvFlag_AcceptsUpstreamTruthyValues(string value)
    {
        Assert.True(CodingAgentTelemetry.IsTruthyEnvFlag(value));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void IsTruthyEnvFlag_RejectsAllOtherValues(string? value)
    {
        Assert.False(CodingAgentTelemetry.IsTruthyEnvFlag(value));
    }

    [Fact]
    public void IsInstallTelemetryEnabled_EnvironmentTrueOverridesDisabledSetting()
    {
        var settings = new CodingAgentSettingsSnapshot(null, null, EnableInstallTelemetry: false);

        var enabled = CodingAgentTelemetry.IsInstallTelemetryEnabled(settings, "true");

        Assert.True(enabled);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("")]
    public void IsInstallTelemetryEnabled_EnvironmentFalseOverridesEnabledSetting(string value)
    {
        var settings = new CodingAgentSettingsSnapshot(null, null, EnableInstallTelemetry: true);

        var enabled = CodingAgentTelemetry.IsInstallTelemetryEnabled(settings, value);

        Assert.False(enabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsInstallTelemetryEnabled_UsesSettingWhenEnvironmentIsUnset(bool setting)
    {
        var settings = new CodingAgentSettingsSnapshot(null, null, EnableInstallTelemetry: setting);

        var enabled = CodingAgentTelemetry.IsInstallTelemetryEnabled(settings);

        Assert.Equal(setting, enabled);
    }

    [Fact]
    public void IsInstallTelemetryEnabled_DefaultsToEnabledWhenSettingIsUnset()
    {
        var settings = new CodingAgentSettingsSnapshot(null, null);

        var enabled = CodingAgentTelemetry.IsInstallTelemetryEnabled(settings);

        Assert.True(enabled);
    }
}
