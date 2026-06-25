using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentStartupNotice(string Text, bool Collapsed);

public interface ICodingAgentInstallTelemetryReporter
{
    void ReportInstall(string version);
}

public sealed class HttpCodingAgentInstallTelemetryReporter : ICodingAgentInstallTelemetryReporter
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public void ReportInstall(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var uri = $"https://pi.dev/install?version={Uri.EscapeDataString(version)}";
                using var response = await Client.GetAsync(uri).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort install/update ping; startup must never fail on telemetry.
            }
        });
    }
}

public sealed class CodingAgentStartupNoticeService
{
    private readonly CodingAgentSettingsStore _settingsStore;
    private readonly CodingAgentChangelogStore _changelogStore;
    private readonly ICodingAgentInstallTelemetryReporter _telemetryReporter;
    private readonly Func<string, string?> _environment;
    private readonly string _currentVersion;

    public CodingAgentStartupNoticeService(
        CodingAgentSettingsStore settingsStore,
        CodingAgentChangelogStore changelogStore,
        ICodingAgentInstallTelemetryReporter? telemetryReporter = null,
        string? currentVersion = null,
        Func<string, string?>? environment = null)
    {
        _settingsStore = settingsStore;
        _changelogStore = changelogStore;
        _telemetryReporter = telemetryReporter ?? new HttpCodingAgentInstallTelemetryReporter();
        _currentVersion = NormalizeVersion(currentVersion) ?? GetAssemblyVersion();
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public CodingAgentStartupNotice? Prepare(bool hasExistingMessages)
    {
        if (hasExistingMessages)
        {
            return null;
        }

        var settings = _settingsStore.Load();
        var lastVersion = NormalizeVersion(settings.LastChangelogVersion);
        if (string.IsNullOrWhiteSpace(lastVersion))
        {
            SaveVersionAndReport(settings);
            return null;
        }

        if (lastVersion.Equals(_currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var snapshot = _changelogStore.Load();
        SaveVersionAndReport(settings);
        if (!string.IsNullOrWhiteSpace(snapshot.Error) || snapshot.Entries.Count == 0)
        {
            return null;
        }

        var collapse = settings.CollapseChangelog ?? false;
        return new CodingAgentStartupNotice(
            collapse
                ? $"Updated to v{_currentVersion}. Use /changelog to view full changelog."
                : FormatFullNotice(snapshot.Entries, _currentVersion),
            collapse);
    }

    private void SaveVersionAndReport(CodingAgentSettingsSnapshot settings)
    {
        _settingsStore.Save(settings with { LastChangelogVersion = _currentVersion });
        if (IsTelemetryEnabled(settings))
        {
            _telemetryReporter.ReportInstall(_currentVersion);
        }
    }

    private bool IsTelemetryEnabled(CodingAgentSettingsSnapshot settings)
    {
        if (CodingAgentTelemetry.IsTruthyEnvFlag(_environment("PI_OFFLINE")))
        {
            return false;
        }

        return CodingAgentTelemetry.IsInstallTelemetryEnabled(
            settings,
            _environment("PI_TELEMETRY"));
    }

    private static string FormatFullNotice(IReadOnlyList<CodingAgentChangelogEntry> entries, string version)
    {
        var builder = new StringBuilder();
        builder.Append("What's New in v").AppendLine(version);
        var limit = Math.Min(entries.Count, 3);
        for (var i = 0; i < limit; i++)
        {
            var entry = entries[i];
            builder.Append("  [")
                .Append(i + 1)
                .Append("] ")
                .Append(entry.Date)
                .Append(' ')
                .AppendLine(entry.Area);
            builder.Append("      用户价值: ")
                .AppendLine(entry.UserValue);
            builder.Append("      变更摘要: ")
                .AppendLine(entry.Summary);
        }

        if (limit < entries.Count)
        {
            builder.Append("Use /changelog all to show all entries.");
        }

        return builder.ToString().TrimEnd();
    }

    private static string GetAssemblyVersion()
    {
        var version = typeof(CodingAgentStartupNoticeService).Assembly.GetName().Version;
        return version is null
            ? "0.1.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value.Trim(), @"\d+\.\d+\.\d+");
        return match.Success ? match.Value : value.Trim();
    }
}
