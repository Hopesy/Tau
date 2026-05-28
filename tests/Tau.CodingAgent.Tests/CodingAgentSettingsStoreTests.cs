using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentSettingsStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsDefaultModelTreeFilterModeRetryPolicyThinkingEnabledModelsQueueAutoCompactionAndTreeFolds()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-settings-{Guid.NewGuid():N}.json");
        var store = new CodingAgentSettingsStore(path);
        var model = new Model
        {
            Provider = "google",
            Id = "gemini-2.5-pro",
            Name = "Gemini 2.5 Pro",
            Api = "google-gemini"
        };

        try
        {
            store.Save(new CodingAgentSettingsSnapshot(
                "openai",
                "gpt-5.4",
                "no-tools",
                4,
                125,
                "high",
                ["openai/gpt-5.4", "google/gemini-2.5-pro"],
                "all",
                "one-at-a-time",
                false,
                "reload-theme",
                [" entry-a ", "entry-b", "ENTRY-A"]));

            var seeded = store.Load();
            Assert.Equal("openai", seeded.DefaultProvider);
            Assert.Equal("gpt-5.4", seeded.DefaultModel);
            Assert.Equal("no-tools", seeded.TreeFilterMode);
            Assert.Equal(4, seeded.RetryMaxAttempts);
            Assert.Equal(125, seeded.RetryBaseDelayMilliseconds);
            Assert.Equal("high", seeded.DefaultThinkingLevel);
            Assert.Equal(["openai/gpt-5.4", "google/gemini-2.5-pro"], seeded.EnabledModels);
            Assert.Equal("all", seeded.SteeringMode);
            Assert.Equal("one-at-a-time", seeded.FollowUpMode);
            Assert.False(seeded.AutoCompactionEnabled);
            Assert.Equal("reload-theme", seeded.Theme);
            Assert.Equal(["entry-a", "entry-b"], seeded.TreeCollapsedEntryIds);

            store.SaveDefaultModel(model);

            var loaded = store.Load();

            Assert.Equal("google", loaded.DefaultProvider);
            Assert.Equal("gemini-2.5-pro", loaded.DefaultModel);
            Assert.Equal("no-tools", loaded.TreeFilterMode);
            Assert.Equal(4, loaded.RetryMaxAttempts);
            Assert.Equal(125, loaded.RetryBaseDelayMilliseconds);
            Assert.Equal("high", loaded.DefaultThinkingLevel);
            Assert.Equal(["openai/gpt-5.4", "google/gemini-2.5-pro"], loaded.EnabledModels);
            Assert.Equal("all", loaded.SteeringMode);
            Assert.Equal("one-at-a-time", loaded.FollowUpMode);
            Assert.False(loaded.AutoCompactionEnabled);
            Assert.Equal("reload-theme", loaded.Theme);
            Assert.Equal(["entry-a", "entry-b"], loaded.TreeCollapsedEntryIds);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void SaveAndLoad_RoundTripsUpstreamShellTerminalImageAndMarkdownSettings()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-settings-upstream-{Guid.NewGuid():N}.json");
        var store = new CodingAgentSettingsStore(path);
        var model = new Model
        {
            Provider = "anthropic",
            Id = "claude-sonnet-4.5",
            Name = "Claude Sonnet 4.5",
            Api = "anthropic"
        };

        try
        {
            store.Save(new CodingAgentSettingsSnapshot(
                null,
                null,
                ShellPath: " C:\\tools\\bash.exe ",
                ShellCommandPrefix: " source ~/.bashrc ",
                NpmCommand: ["mise", "exec", "node@20", "--", " npm ", "npm"],
                QuietStartup: true,
                CollapseChangelog: true,
                EnableInstallTelemetry: false,
                TerminalShowImages: false,
                TerminalClearOnShrink: true,
                ImagesAutoResize: false,
                ImagesBlockImages: true,
                ShowHardwareCursor: true,
                EditorPaddingX: 99,
                AutocompleteMaxVisible: 1,
                MarkdownCodeBlockIndent: "    "));

            AssertUpstreamSettings(store.Load());

            store.SaveDefaultModel(model);

            var loaded = store.Load();
            Assert.Equal("anthropic", loaded.DefaultProvider);
            Assert.Equal("claude-sonnet-4.5", loaded.DefaultModel);
            AssertUpstreamSettings(loaded);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        static void AssertUpstreamSettings(CodingAgentSettingsSnapshot loaded)
        {
            Assert.Equal("C:\\tools\\bash.exe", loaded.ShellPath);
            Assert.Equal("source ~/.bashrc", loaded.ShellCommandPrefix);
            Assert.Equal(["mise", "exec", "node@20", "--", "npm", "npm"], loaded.NpmCommand);
            Assert.True(loaded.QuietStartup);
            Assert.True(loaded.CollapseChangelog);
            Assert.False(loaded.EnableInstallTelemetry);
            Assert.False(loaded.TerminalShowImages);
            Assert.True(loaded.TerminalClearOnShrink);
            Assert.False(loaded.ImagesAutoResize);
            Assert.True(loaded.ImagesBlockImages);
            Assert.True(loaded.ShowHardwareCursor);
            Assert.Equal(3, loaded.EditorPaddingX);
            Assert.Equal(3, loaded.AutocompleteMaxVisible);
            Assert.Equal("    ", loaded.MarkdownCodeBlockIndent);
        }
    }

    [Fact]
    public void Load_InvalidJson_ReturnsEmptySnapshot()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-settings-invalid-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, "not json");

            var loaded = new CodingAgentSettingsStore(path).Load();

            Assert.Null(loaded.DefaultProvider);
            Assert.Null(loaded.DefaultModel);
            Assert.Null(loaded.TreeFilterMode);
            Assert.Null(loaded.RetryMaxAttempts);
            Assert.Null(loaded.RetryBaseDelayMilliseconds);
            Assert.Null(loaded.DefaultThinkingLevel);
            Assert.Null(loaded.EnabledModels);
            Assert.Null(loaded.SteeringMode);
            Assert.Null(loaded.FollowUpMode);
            Assert.Null(loaded.AutoCompactionEnabled);
            Assert.Null(loaded.Theme);
            Assert.Null(loaded.TreeCollapsedEntryIds);
            Assert.Null(loaded.ShellPath);
            Assert.Null(loaded.ShellCommandPrefix);
            Assert.Null(loaded.NpmCommand);
            Assert.Null(loaded.QuietStartup);
            Assert.Null(loaded.CollapseChangelog);
            Assert.Null(loaded.EnableInstallTelemetry);
            Assert.Null(loaded.TerminalShowImages);
            Assert.Null(loaded.TerminalClearOnShrink);
            Assert.Null(loaded.ImagesAutoResize);
            Assert.Null(loaded.ImagesBlockImages);
            Assert.Null(loaded.ShowHardwareCursor);
            Assert.Null(loaded.EditorPaddingX);
            Assert.Null(loaded.AutocompleteMaxVisible);
            Assert.Null(loaded.MarkdownCodeBlockIndent);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Load_MigratesLegacyQueueModeToSteeringMode()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-settings-queue-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "queueMode": "all",
                  "followUpMode": "one-at-a-time",
                  "autoCompactionEnabled": true,
                  "theme": "  dark-plus  "
                }
                """);

            var loaded = new CodingAgentSettingsStore(path).Load();

            Assert.Equal("all", loaded.SteeringMode);
            Assert.Equal("one-at-a-time", loaded.FollowUpMode);
            Assert.True(loaded.AutoCompactionEnabled);
            Assert.Equal("dark-plus", loaded.Theme);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void SaveAndLoad_PreservesScopedModelThinkingSuffixes()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-settings-thinking-scope-{Guid.NewGuid():N}.json");
        var store = new CodingAgentSettingsStore(path);

        try
        {
            store.Save(new CodingAgentSettingsSnapshot(
                null,
                null,
                EnabledModels: [" openai/gpt-5.4:high ", "google/gemini-2.5-pro:off", "OPENAI/gpt-5.4:high"]));

            var loaded = store.Load();

            Assert.Equal(["openai/gpt-5.4:high", "google/gemini-2.5-pro:off"], loaded.EnabledModels);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
