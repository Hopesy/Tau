using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentSettingsStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsDefaultModelTreeFilterModeRetryPolicyThinkingEnabledModelsQueueAndAutoCompaction()
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
                "reload-theme"));

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
}
