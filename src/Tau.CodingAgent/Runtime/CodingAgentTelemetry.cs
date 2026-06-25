namespace Tau.CodingAgent.Runtime;

internal static class CodingAgentTelemetry
{
    public static bool IsTruthyEnvFlag(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    public static bool IsInstallTelemetryEnabled(
        CodingAgentSettingsSnapshot settings,
        string? telemetryEnvironmentOverride = null) =>
        telemetryEnvironmentOverride is not null
            ? IsTruthyEnvFlag(telemetryEnvironmentOverride)
            : settings.EnableInstallTelemetry ?? true;
}
