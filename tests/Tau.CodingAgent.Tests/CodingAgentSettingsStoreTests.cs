using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentSettingsStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsDefaultModelAndTreeFilterMode()
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
            store.Save(new CodingAgentSettingsSnapshot("openai", "gpt-5.4", "no-tools"));

            var seeded = store.Load();
            Assert.Equal("openai", seeded.DefaultProvider);
            Assert.Equal("gpt-5.4", seeded.DefaultModel);
            Assert.Equal("no-tools", seeded.TreeFilterMode);

            store.SaveDefaultModel(model);

            var loaded = store.Load();

            Assert.Equal("google", loaded.DefaultProvider);
            Assert.Equal("gemini-2.5-pro", loaded.DefaultModel);
            Assert.Equal("no-tools", loaded.TreeFilterMode);
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
