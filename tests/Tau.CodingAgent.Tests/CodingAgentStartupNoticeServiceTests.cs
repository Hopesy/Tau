using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentStartupNoticeServiceTests
{
    [Fact]
    public void Prepare_FreshInstallRecordsVersionAndReportsTelemetryWithoutNotice()
    {
        using var temp = TempDirectory.Create();
        var settings = new CodingAgentSettingsStore(Path.Combine(temp.Path, "settings.json"));
        var reporter = new FakeInstallTelemetryReporter();
        var service = new CodingAgentStartupNoticeService(
            settings,
            CreateChangelog(temp.Path),
            reporter,
            currentVersion: "0.1.0",
            environment: _ => null);

        var notice = service.Prepare(hasExistingMessages: false);

        Assert.Null(notice);
        Assert.Equal("0.1.0", settings.Load().LastChangelogVersion);
        Assert.Equal(["0.1.0"], reporter.Versions);
    }

    [Fact]
    public void Prepare_NewVersionRendersCollapsedNoticeUpdatesVersionAndReportsTelemetry()
    {
        using var temp = TempDirectory.Create();
        var settings = new CodingAgentSettingsStore(Path.Combine(temp.Path, "settings.json"));
        settings.Save(new CodingAgentSettingsSnapshot(
            null,
            null,
            CollapseChangelog: true,
            LastChangelogVersion: "0.0.9"));
        var reporter = new FakeInstallTelemetryReporter();
        var service = new CodingAgentStartupNoticeService(
            settings,
            CreateChangelog(temp.Path),
            reporter,
            currentVersion: "0.1.0",
            environment: _ => null);

        var notice = service.Prepare(hasExistingMessages: false);

        Assert.NotNull(notice);
        Assert.True(notice!.Collapsed);
        Assert.Equal("Updated to v0.1.0. Use /changelog to view full changelog.", notice.Text);
        Assert.Equal("0.1.0", settings.Load().LastChangelogVersion);
        Assert.Equal(["0.1.0"], reporter.Versions);
    }

    [Fact]
    public void Prepare_NewVersionRendersFullNoticeWhenCollapseIsDisabled()
    {
        using var temp = TempDirectory.Create();
        var settings = new CodingAgentSettingsStore(Path.Combine(temp.Path, "settings.json"));
        settings.Save(new CodingAgentSettingsSnapshot(
            null,
            null,
            CollapseChangelog: false,
            LastChangelogVersion: "0.0.9"));
        var reporter = new FakeInstallTelemetryReporter();
        var service = new CodingAgentStartupNoticeService(
            settings,
            CreateChangelog(temp.Path),
            reporter,
            currentVersion: "0.1.0",
            environment: _ => null);

        var notice = service.Prepare(hasExistingMessages: false);

        Assert.NotNull(notice);
        Assert.False(notice!.Collapsed);
        Assert.Contains("What's New in v0.1.0", notice.Text, StringComparison.Ordinal);
        Assert.Contains("2026-06-09 CodingAgent", notice.Text, StringComparison.Ordinal);
        Assert.Contains("启动时展示更新摘要。", notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_RespectsOfflineAndTelemetryEnvironmentOverrides()
    {
        using var temp = TempDirectory.Create();
        var settings = new CodingAgentSettingsStore(Path.Combine(temp.Path, "settings.json"));
        settings.Save(new CodingAgentSettingsSnapshot(
            null,
            null,
            EnableInstallTelemetry: true,
            LastChangelogVersion: "0.0.9"));
        var reporter = new FakeInstallTelemetryReporter();
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PI_OFFLINE"] = "1",
            ["PI_TELEMETRY"] = "true"
        };
        var service = new CodingAgentStartupNoticeService(
            settings,
            CreateChangelog(temp.Path),
            reporter,
            currentVersion: "0.1.0",
            environment: key => env.TryGetValue(key, out var value) ? value : null);

        var notice = service.Prepare(hasExistingMessages: false);

        Assert.NotNull(notice);
        Assert.Empty(reporter.Versions);
    }

    [Fact]
    public void Prepare_SkipsResumedSessionsWithoutUpdatingVersionOrTelemetry()
    {
        using var temp = TempDirectory.Create();
        var settings = new CodingAgentSettingsStore(Path.Combine(temp.Path, "settings.json"));
        settings.Save(new CodingAgentSettingsSnapshot(null, null, LastChangelogVersion: "0.0.9"));
        var reporter = new FakeInstallTelemetryReporter();
        var service = new CodingAgentStartupNoticeService(
            settings,
            CreateChangelog(temp.Path),
            reporter,
            currentVersion: "0.1.0",
            environment: _ => null);

        var notice = service.Prepare(hasExistingMessages: true);

        Assert.Null(notice);
        Assert.Equal("0.0.9", settings.Load().LastChangelogVersion);
        Assert.Empty(reporter.Versions);
    }

    private static CodingAgentChangelogStore CreateChangelog(string directory)
    {
        var path = Path.Combine(directory, "feature-release-notes.md");
        File.WriteAllText(
            path,
            """
            | 日期 | 功能域 | 用户价值 | 变更摘要 |
            | --- | --- | --- | --- |
            | 2026-06-09 | CodingAgent | 启动时展示更新摘要。 | 新增 startup changelog baseline。 |
            """);
        return new CodingAgentChangelogStore(path);
    }

    private sealed class FakeInstallTelemetryReporter : ICodingAgentInstallTelemetryReporter
    {
        public List<string> Versions { get; } = [];

        public void ReportInstall(string version) => Versions.Add(version);
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-startup-notice-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
