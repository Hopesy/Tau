using Tau.Agent;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentCliModelScopeTests
{
    [Fact]
    public void CycleModel_UsesStartupScopedModelsOverrideBeforeSettings()
    {
        using var temp = TempDirectory.Create();
        var settingsStore = new CodingAgentSettingsStore(Path.Combine(temp.Path, "settings.json"));
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            DefaultProvider: "openai",
            DefaultModel: "gpt-5.4",
            EnabledModels: ["openai/gpt-5.4"]));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyEvents());
        runner.ConfigureAuth("openai", "google");
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore: settingsStore,
            scopedModelsOverride: ["google/gemini-2.5-pro:low", "openai/gpt-5.4"]);

        var result = router.CycleModel();

        Assert.False(result.IsError);
        Assert.Equal("google", runner.Model.Provider);
        Assert.Equal("gemini-2.5-pro", runner.Model.Id);
        Assert.Equal("model: google/gemini-2.5-pro (scoped, thinking: low)", result.Message);
    }

    [Fact]
    public void TryResolveScopedModelEntries_ReportsInvalidPattern()
    {
        var runner = new FakeCodingAgentRunner((_, _) => EmptyEvents());
        var registered = CodingAgentModelAvailability.GetRegisteredModels(runner);

        var ok = CodingAgentModelAvailability.TryResolveScopedModelEntries(
            ["missing-model"],
            registered,
            out var entries,
            out var error);

        Assert.False(ok);
        Assert.Empty(entries);
        Assert.Equal("model 'missing-model' is not registered", error);
    }

    [Fact]
    public void TryResolveScopedModelEntries_ExpandsWildcardPatterns()
    {
        var runner = new FakeCodingAgentRunner((_, _) => EmptyEvents());
        var registered = CodingAgentModelAvailability.GetRegisteredModels(runner);

        var ok = CodingAgentModelAvailability.TryResolveScopedModelEntries(
            ["google/*:low"],
            registered,
            out var entries,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        var entry = Assert.Single(entries);
        Assert.Equal("google/gemini-2.5-pro:low", entry.Pattern);
    }

    private static async IAsyncEnumerable<AgentEvent> EmptyEvents()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-cli-model-scope-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
