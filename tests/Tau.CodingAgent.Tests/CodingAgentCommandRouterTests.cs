using System.Globalization;
using System.Text.Json;
using Tau.Agent;
using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Tools;
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentCommandRouterTests
{
    [Fact]
    public async Task TryHandleAsync_NonSlashInput_ReturnsNotCommand()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("hello");

        Assert.False(result.Handled);
        Assert.False(result.IsError);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task TryHandleAsync_ProvidersCommand_ReturnsProviderListWithoutInvokingRunner()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/providers");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal("providers: google, openai", result.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_ProvidersCommandWithExtraArgs_ReturnsCatalogUsage()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/providers openai");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("usage: /providers", result.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_ScopedModelsCommand_ShowsAllModelsWhenUnset()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-scoped-models-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var result = await router.TryHandleAsync("/scoped-models");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("scoped models: all enabled (2/2)", result.Message, StringComparison.Ordinal);
            Assert.Contains($"settings: {settingsPath}", result.Message, StringComparison.Ordinal);
            Assert.Contains("google/gemini-2.5-pro (enabled)", result.Message, StringComparison.Ordinal);
            Assert.Contains("openai/gpt-5.4 (enabled)", result.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ScopedModelsCommand_SetsAddsRemovesAndClearsEnabledModels()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-scoped-models-set-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            settingsStore.Save(new CodingAgentSettingsSnapshot(
                "openai",
                "gpt-5.4",
                "no-tools",
                RetryMaxAttempts: 4,
                RetryBaseDelayMilliseconds: 125,
                DefaultThinkingLevel: "high"));

            var set = await router.TryHandleAsync("/scoped-models set google/gemini-2.5-pro");
            var afterSet = settingsStore.Load();
            var add = await router.TryHandleAsync("/scoped-models add openai/gpt-5.4");
            var afterAdd = settingsStore.Load();
            var remove = await router.TryHandleAsync("/scoped-models remove google/gemini-2.5-pro");
            var afterRemove = settingsStore.Load();
            var clear = await router.TryHandleAsync("/scoped-models clear");
            var afterClear = settingsStore.Load();

            Assert.False(set.IsError);
            Assert.Contains("scoped models: 1/2 enabled", set.Message, StringComparison.Ordinal);
            Assert.Contains("order: google/gemini-2.5-pro", set.Message, StringComparison.Ordinal);
            Assert.Equal(["google/gemini-2.5-pro"], afterSet.EnabledModels);

            Assert.False(add.IsError);
            Assert.Contains("scoped models: all enabled (2/2)", add.Message, StringComparison.Ordinal);
            Assert.Null(afterAdd.EnabledModels);

            Assert.False(remove.IsError);
            Assert.Contains("scoped models: 1/2 enabled", remove.Message, StringComparison.Ordinal);
            Assert.Contains("order: openai/gpt-5.4", remove.Message, StringComparison.Ordinal);
            Assert.Equal(["openai/gpt-5.4"], afterRemove.EnabledModels);

            Assert.False(clear.IsError);
            Assert.Contains("scoped models: all enabled (2/2)", clear.Message, StringComparison.Ordinal);
            Assert.Null(afterClear.EnabledModels);

            Assert.Equal("openai", afterClear.DefaultProvider);
            Assert.Equal("gpt-5.4", afterClear.DefaultModel);
            Assert.Equal("no-tools", afterClear.TreeFilterMode);
            Assert.Equal(4, afterClear.RetryMaxAttempts);
            Assert.Equal(125, afterClear.RetryBaseDelayMilliseconds);
            Assert.Equal("high", afterClear.DefaultThinkingLevel);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ScopedModelsCommand_PersistsPerEntryThinkingLevels()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-scoped-models-thinking-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var set = await router.TryHandleAsync("/scoped-models set google/gemini-2.5-pro:high");
            var afterSet = settingsStore.Load();
            var add = await router.TryHandleAsync("/scoped-models add openai/gpt-5.4:off");
            var afterAdd = settingsStore.Load();

            Assert.False(set.IsError);
            Assert.Contains("order: google/gemini-2.5-pro:high", set.Message, StringComparison.Ordinal);
            Assert.Contains("google/gemini-2.5-pro (enabled, thinking high)", set.Message, StringComparison.Ordinal);
            Assert.Equal(["google/gemini-2.5-pro:high"], afterSet.EnabledModels);

            Assert.False(add.IsError);
            Assert.Contains("order: google/gemini-2.5-pro:high, openai/gpt-5.4:off", add.Message, StringComparison.Ordinal);
            Assert.Contains("openai/gpt-5.4 (enabled, thinking off)", add.Message, StringComparison.Ordinal);
            Assert.Equal(["google/gemini-2.5-pro:high", "openai/gpt-5.4:off"], afterAdd.EnabledModels);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ScopedModelsCommand_ReturnsUsageOrModelErrors()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-scoped-models-invalid-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var missingAction = await router.TryHandleAsync("/scoped-models set");
            var invalidAction = await router.TryHandleAsync("/scoped-models toggle openai/gpt-5.4");
            var extraClear = await router.TryHandleAsync("/scoped-models clear openai/gpt-5.4");
            var missingModel = await router.TryHandleAsync("/scoped-models set openai/nope");

            Assert.All(
                [missingAction, invalidAction, extraClear],
                result =>
                {
                    Assert.True(result.Handled);
                    Assert.True(result.IsError);
                    Assert.Equal("usage: /scoped-models [current|select|set|add|remove|clear|all] [provider/model ...]", result.Message);
                });

            Assert.True(missingModel.Handled);
            Assert.True(missingModel.IsError);
            Assert.Equal("model 'openai/nope' is not registered", missingModel.Message);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ScopedModelsCommand_UsesInjectedSelectorAndPersistsSelection()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-scoped-models-selector-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var selectorStates = new List<CodingAgentScopedModelsSelectorState>();
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            scopedModelsSelector: (state, _) =>
            {
                selectorStates.Add(state);
                return Task.FromResult(CodingAgentScopedModelsSelection.Saved(["google/gemini-2.5-pro"]));
            });

        try
        {
            var result = await router.TryHandleAsync("/scoped-models");
            var saved = settingsStore.Load();

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("scoped models: 1/2 enabled", result.Message, StringComparison.Ordinal);
            Assert.Equal(["google/gemini-2.5-pro"], saved.EnabledModels);
            var state = Assert.Single(selectorStates);
            Assert.Equal(settingsPath, state.SettingsPath);
            Assert.Equal("openai/gpt-5.4", $"{state.CurrentModel.Provider}/{state.CurrentModel.Id}");
            Assert.Equal(["google/gemini-2.5-pro", "openai/gpt-5.4"], state.AvailableModels.Select(model => $"{model.Provider}/{model.Id}").ToArray());
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ScopedModelsCommand_SelectorPreservesExistingThinkingLevels()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-scoped-models-selector-thinking-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            null,
            null,
            EnabledModels: ["google/gemini-2.5-pro:high"]));
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            scopedModelsSelector: (_, _) => Task.FromResult(CodingAgentScopedModelsSelection.Saved(
                ["google/gemini-2.5-pro", "openai/gpt-5.4"])));

        try
        {
            var result = await router.TryHandleAsync("/scoped-models select");
            var saved = settingsStore.Load();

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("order: google/gemini-2.5-pro:high, openai/gpt-5.4", result.Message, StringComparison.Ordinal);
            Assert.Equal(["google/gemini-2.5-pro:high", "openai/gpt-5.4"], saved.EnabledModels);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ScopedModelsCommand_SelectCancelAndUnavailableDoNotChangeSettings()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-scoped-models-selector-cancel-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(null, null, EnabledModels: ["google/gemini-2.5-pro"]));
        var cancelRouter = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            scopedModelsSelector: (_, _) => Task.FromResult(CodingAgentScopedModelsSelection.Cancelled));
        var unavailableRouter = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var cancel = await cancelRouter.TryHandleAsync("/scoped-models select");
            var unavailable = await unavailableRouter.TryHandleAsync("/scoped-models select");

            Assert.True(cancel.Handled);
            Assert.False(cancel.IsError);
            Assert.Equal("scoped model selection cancelled", cancel.Message);
            Assert.Equal(["google/gemini-2.5-pro"], settingsStore.Load().EnabledModels);

            Assert.True(unavailable.Handled);
            Assert.True(unavailable.IsError);
            Assert.Equal("scoped model selector is not available in this session", unavailable.Message);
            Assert.Equal(["google/gemini-2.5-pro"], settingsStore.Load().EnabledModels);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_SettingsCommand_ShowsCurrentSettingsSnapshotWithoutInvokingRunner()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-settings-summary-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            ThinkingLevel = ThinkingLevel.High
        };
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            settingsStore.Save(new CodingAgentSettingsSnapshot(
                "openai",
                "gpt-5.4",
                "no-tools",
                RetryMaxAttempts: 4,
                RetryBaseDelayMilliseconds: 125,
                DefaultThinkingLevel: "low",
                EnabledModels: ["google/gemini-2.5-pro"],
                SteeringMode: "all",
                FollowUpMode: "one-at-a-time",
                AutoCompactionEnabled: false));

            var result = await router.TryHandleAsync("/settings");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains($"settings: {settingsPath}", result.Message, StringComparison.Ordinal);
            Assert.Contains("current model: openai/gpt-5.4", result.Message, StringComparison.Ordinal);
            Assert.Contains("current thinking: high", result.Message, StringComparison.Ordinal);
            Assert.Contains("default model: openai/gpt-5.4", result.Message, StringComparison.Ordinal);
            Assert.Contains("tree filter: no-tools", result.Message, StringComparison.Ordinal);
            Assert.Contains("retry: enabled 4 attempts, base 125ms", result.Message, StringComparison.Ordinal);
            Assert.Contains("default thinking: low", result.Message, StringComparison.Ordinal);
            Assert.Contains("steering mode: all", result.Message, StringComparison.Ordinal);
            Assert.Contains("follow-up mode: one-at-a-time", result.Message, StringComparison.Ordinal);
            Assert.Contains("auto compaction: disabled", result.Message, StringComparison.Ordinal);
            Assert.Contains("theme: dark", result.Message, StringComparison.Ordinal);
            Assert.Contains("scoped models: 1 enabled (google/gemini-2.5-pro)", result.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_SettingsCommand_PathUnavailableAndUsage()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-settings-path-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(runner, settingsStore);
        var unavailableRouter = new CodingAgentCommandRouter(runner);

        try
        {
            var path = await router.TryHandleAsync("/settings path");
            var current = await router.TryHandleAsync("/settings current");
            var selectorUnavailable = await router.TryHandleAsync("/settings select");
            var unavailable = await unavailableRouter.TryHandleAsync("/settings");
            var invalid = await router.TryHandleAsync("/settings edit");

            Assert.True(path.Handled);
            Assert.False(path.IsError);
            Assert.Equal($"settings: {settingsPath}", path.Message);

            Assert.True(current.Handled);
            Assert.False(current.IsError);
            Assert.Contains("default model: unset", current.Message, StringComparison.Ordinal);
            Assert.Contains("tree filter: default", current.Message, StringComparison.Ordinal);
            Assert.Contains("default thinking: off", current.Message, StringComparison.Ordinal);
            Assert.Contains("steering mode: one-at-a-time", current.Message, StringComparison.Ordinal);
            Assert.Contains("follow-up mode: one-at-a-time", current.Message, StringComparison.Ordinal);
            Assert.Contains("auto compaction: default", current.Message, StringComparison.Ordinal);
            Assert.Contains("theme: dark", current.Message, StringComparison.Ordinal);
            Assert.Contains("scoped models: all enabled", current.Message, StringComparison.Ordinal);

            Assert.True(selectorUnavailable.Handled);
            Assert.True(selectorUnavailable.IsError);
            Assert.Equal("settings selector is not available in this session", selectorUnavailable.Message);

            Assert.True(unavailable.Handled);
            Assert.True(unavailable.IsError);
            Assert.Equal("settings are not available in this session", unavailable.Message);

            Assert.True(invalid.Handled);
            Assert.True(invalid.IsError);
            Assert.Equal("usage: /settings [current|path|select]", invalid.Message);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_SettingsCommand_SelectsAndPersistsSettingsWithInjectedSelector()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-settings-select-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            ThinkingLevel = ThinkingLevel.Low
        };
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var selections = new Queue<string?>(
        [
            CodingAgentSettingsSelector.SteeringModeAction,
            CodingAgentSettingsSelector.FollowUpModeAction,
            CodingAgentSettingsSelector.TreeFilterModeAction,
            CodingAgentSettingsSelector.ThinkingLevelAction,
            CodingAgentSettingsSelector.AutoCompactionAction,
            null
        ]);
        var selectorStates = new List<CodingAgentSettingsSelectorState>();
        bool? autoCompactionChanged = null;
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            autoCompaction: new CodingAgentAutoCompactionOptions(1000),
            autoCompactionChanged: enabled => autoCompactionChanged = enabled,
            settingsSelector: (state, _) =>
            {
                selectorStates.Add(state);
                return Task.FromResult(selections.Dequeue());
            });

        try
        {
            settingsStore.Save(new CodingAgentSettingsSnapshot(
                "openai",
                "gpt-5.4",
                RetryMaxAttempts: 4,
                RetryBaseDelayMilliseconds: 125,
                DefaultThinkingLevel: "low",
                SteeringMode: "one-at-a-time",
                FollowUpMode: "one-at-a-time"));

            var steering = await router.TryHandleAsync("/settings select");
            var followUp = await router.TryHandleAsync("/settings select");
            var tree = await router.TryHandleAsync("/settings select");
            var thinking = await router.TryHandleAsync("/settings select");
            var autoCompaction = await router.TryHandleAsync("/settings select");
            var cancel = await router.TryHandleAsync("/settings select");
            var saved = settingsStore.Load();

            Assert.All(
                [steering, followUp, tree, thinking, autoCompaction, cancel],
                result =>
                {
                    Assert.True(result.Handled);
                    Assert.False(result.IsError);
                });
            Assert.Equal("steering mode: all", steering.Message);
            Assert.Equal("follow-up mode: all", followUp.Message);
            Assert.Equal("tree filter: no-tools", tree.Message);
            Assert.Equal("thinking: medium", thinking.Message);
            Assert.Equal("auto compaction: disabled", autoCompaction.Message);
            Assert.Equal("settings selection cancelled", cancel.Message);

            Assert.Equal(AgentQueueMode.All, runner.SteeringMode);
            Assert.Equal(AgentQueueMode.All, runner.FollowUpMode);
            Assert.Equal(ThinkingLevel.Medium, runner.ThinkingLevel);
            Assert.Equal("all", saved.SteeringMode);
            Assert.Equal("all", saved.FollowUpMode);
            Assert.Equal("no-tools", saved.TreeFilterMode);
            Assert.Equal("medium", saved.DefaultThinkingLevel);
            Assert.False(saved.AutoCompactionEnabled);
            Assert.False(autoCompactionChanged);
            Assert.Equal("openai", saved.DefaultProvider);
            Assert.Equal("gpt-5.4", saved.DefaultModel);
            Assert.Equal(4, saved.RetryMaxAttempts);
            Assert.Equal(125, saved.RetryBaseDelayMilliseconds);

            Assert.Equal(6, selectorStates.Count);
            Assert.Equal(settingsPath, selectorStates[0].SettingsPath);
            Assert.True(selectorStates[0].AutoCompactionEnabled);
            Assert.Equal("dark", selectorStates[0].CurrentTheme);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_SettingsCommand_AppliesSettingsListValuePayloads()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-settings-list-values-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var selections = new Queue<string?>(
        [
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.TerminalShowImagesAction, "false"),
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.ImagesAutoResizeAction, "false"),
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.ImagesBlockImagesAction, "true"),
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.ShowHardwareCursorAction, "true"),
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.EditorPaddingAction, "3"),
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.AutocompleteMaxVisibleAction, "15"),
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.TerminalClearOnShrinkAction, "true"),
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.QuietStartupAction, "true"),
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.CollapseChangelogAction, "true"),
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.InstallTelemetryAction, "false"),
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.SteeringModeAction, "all"),
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.FollowUpModeAction, "all"),
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.TreeFilterModeAction, "labeled-only"),
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.ThinkingLevelAction, "high"),
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.AutoCompactionAction, "true")
        ]);
        bool? autoCompactionChanged = null;
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            autoCompactionChanged: enabled => autoCompactionChanged = enabled,
            settingsSelector: (_, _) => Task.FromResult(selections.Dequeue()));

        try
        {
            settingsStore.Save(new CodingAgentSettingsSnapshot(
                "openai",
                "gpt-5.4",
                RetryMaxAttempts: 4,
                RetryBaseDelayMilliseconds: 125,
                EnabledModels: ["google/gemini-2.5-pro"]));

            var results = new List<CodingAgentCommandResult>();
            while (selections.Count > 0)
            {
                results.Add(await router.TryHandleAsync("/settings select"));
            }

            var saved = settingsStore.Load();

            Assert.All(
                results,
                result =>
                {
                    Assert.True(result.Handled);
                    Assert.False(result.IsError);
                });
            Assert.Contains(results, result => result.Message == "show images: disabled");
            Assert.Contains(results, result => result.Message == "auto-resize images: disabled");
            Assert.Contains(results, result => result.Message == "block images: enabled");
            Assert.Contains(results, result => result.Message == "show hardware cursor: enabled");
            Assert.Contains(results, result => result.Message == "editor padding: 3");
            Assert.Contains(results, result => result.Message == "autocomplete max items: 15");
            Assert.Contains(results, result => result.Message == "clear on shrink: enabled");
            Assert.Contains(results, result => result.Message == "quiet startup: enabled");
            Assert.Contains(results, result => result.Message == "collapse changelog: enabled");
            Assert.Contains(results, result => result.Message == "install telemetry: disabled");
            Assert.Contains(results, result => result.Message == "steering mode: all");
            Assert.Contains(results, result => result.Message == "follow-up mode: all");
            Assert.Contains(results, result => result.Message == "tree filter: labeled-only");
            Assert.Contains(results, result => result.Message == "thinking: high");
            Assert.Contains(results, result => result.Message == "auto compaction: enabled");

            Assert.False(saved.TerminalShowImages);
            Assert.False(saved.ImagesAutoResize);
            Assert.True(saved.ImagesBlockImages);
            Assert.True(saved.ShowHardwareCursor);
            Assert.Equal(3, saved.EditorPaddingX);
            Assert.Equal(15, saved.AutocompleteMaxVisible);
            Assert.True(saved.TerminalClearOnShrink);
            Assert.True(saved.QuietStartup);
            Assert.True(saved.CollapseChangelog);
            Assert.False(saved.EnableInstallTelemetry);
            Assert.Equal("all", saved.SteeringMode);
            Assert.Equal("all", saved.FollowUpMode);
            Assert.Equal("labeled-only", saved.TreeFilterMode);
            Assert.Equal("high", saved.DefaultThinkingLevel);
            Assert.True(saved.AutoCompactionEnabled);
            Assert.Equal(AgentQueueMode.All, runner.SteeringMode);
            Assert.Equal(AgentQueueMode.All, runner.FollowUpMode);
            Assert.Equal(ThinkingLevel.High, runner.ThinkingLevel);
            Assert.True(autoCompactionChanged);
            Assert.Equal("openai", saved.DefaultProvider);
            Assert.Equal("gpt-5.4", saved.DefaultModel);
            Assert.Equal(4, saved.RetryMaxAttempts);
            Assert.Equal(125, saved.RetryBaseDelayMilliseconds);
            Assert.Equal(["google/gemini-2.5-pro"], saved.EnabledModels);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_SettingsCommand_ThemeSelectionUsesThemeSelector()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-settings-theme-select-" + Guid.NewGuid().ToString("N"));
        var projectThemes = Path.Combine(directory, ".tau", "themes");
        Directory.CreateDirectory(projectThemes);
        var themeFile = Path.Combine(projectThemes, "solarized.json");
        await File.WriteAllTextAsync(themeFile, CodingAgentThemeStoreTests.CreateThemeJson("solarized"));
        var settingsPath = Path.Combine(directory, "settings.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var themeStore = new CodingAgentThemeStore(
            cwd: directory,
            userThemesDirectory: Path.Combine(directory, "missing-user-themes"),
            explicitPaths: []);
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            themeStore: themeStore,
            settingsSelector: (_, _) => Task.FromResult<string?>(CodingAgentSettingsSelector.ThemeAction),
            themeSelector: (_, _, _) => Task.FromResult<string?>("SOLARIZED"));

        try
        {
            var result = await router.TryHandleAsync("/settings select");
            var saved = settingsStore.Load();

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("theme: solarized", result.Message);
            Assert.Equal("solarized", saved.Theme);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_SettingsCommand_ScopedModelsSelectionUsesScopedModelsSelector()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-settings-scoped-models-select-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            settingsSelector: (_, _) => Task.FromResult<string?>(CodingAgentSettingsSelector.ScopedModelsAction),
            scopedModelsSelector: (_, _) => Task.FromResult(
                CodingAgentScopedModelsSelection.Saved(["google/gemini-2.5-pro"])));

        try
        {
            var result = await router.TryHandleAsync("/settings select");
            var saved = settingsStore.Load();

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("scoped models: 1/2 enabled", result.Message, StringComparison.Ordinal);
            Assert.Equal(["google/gemini-2.5-pro"], saved.EnabledModels);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ThemeCommand_ListsSetsAndClearsPersistedTheme()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-theme-router-" + Guid.NewGuid().ToString("N"));
        var projectThemes = Path.Combine(directory, ".tau", "themes");
        Directory.CreateDirectory(projectThemes);
        var themeFile = Path.Combine(projectThemes, "solarized.json");
        await File.WriteAllTextAsync(themeFile, CodingAgentThemeStoreTests.CreateThemeJson("solarized"));
        var settingsPath = Path.Combine(directory, "settings.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var themeStore = new CodingAgentThemeStore(
            cwd: directory,
            userThemesDirectory: Path.Combine(directory, "missing-user-themes"),
            explicitPaths: []);
        var router = new CodingAgentCommandRouter(runner, settingsStore, themeStore: themeStore);

        try
        {
            settingsStore.Save(new CodingAgentSettingsSnapshot(
                "openai",
                "gpt-5.4",
                "no-tools",
                RetryMaxAttempts: 4,
                RetryBaseDelayMilliseconds: 125,
                DefaultThinkingLevel: "high",
                EnabledModels: ["google/gemini-2.5-pro"]));

            var list = await router.TryHandleAsync("/theme list");
            var set = await router.TryHandleAsync("/theme set solarized");
            var afterSet = settingsStore.Load();
            var current = await router.TryHandleAsync("/theme current");
            var settings = await router.TryHandleAsync("/settings");
            var clear = await router.TryHandleAsync("/theme clear");
            var afterClear = settingsStore.Load();

            Assert.True(list.Handled);
            Assert.False(list.IsError);
            Assert.Contains("themes: 3, current dark", list.Message, StringComparison.Ordinal);
            Assert.Contains("* dark (builtin)", list.Message, StringComparison.Ordinal);
            Assert.Contains($"- solarized (project {themeFile})", list.Message, StringComparison.Ordinal);

            Assert.False(set.IsError);
            Assert.Equal("theme: solarized", set.Message);
            Assert.Equal("solarized", afterSet.Theme);
            Assert.Equal("openai", afterSet.DefaultProvider);
            Assert.Equal("gpt-5.4", afterSet.DefaultModel);
            Assert.Equal("no-tools", afterSet.TreeFilterMode);
            Assert.Equal(4, afterSet.RetryMaxAttempts);
            Assert.Equal(125, afterSet.RetryBaseDelayMilliseconds);
            Assert.Equal("high", afterSet.DefaultThinkingLevel);
            Assert.Equal(["google/gemini-2.5-pro"], afterSet.EnabledModels);

            Assert.False(current.IsError);
            Assert.Equal($"theme: solarized (project {themeFile})", current.Message);
            Assert.Contains("theme: solarized", settings.Message, StringComparison.Ordinal);

            Assert.False(clear.IsError);
            Assert.Equal("theme: dark", clear.Message);
            Assert.Null(afterClear.Theme);
            Assert.Equal("openai", afterClear.DefaultProvider);
            Assert.Equal("gpt-5.4", afterClear.DefaultModel);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryHandleAsync_ThemeCommand_SelectsPersistedThemeWithInjectedSelector()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-theme-select-router-" + Guid.NewGuid().ToString("N"));
        var projectThemes = Path.Combine(directory, ".tau", "themes");
        Directory.CreateDirectory(projectThemes);
        var themeFile = Path.Combine(projectThemes, "solarized.json");
        await File.WriteAllTextAsync(themeFile, CodingAgentThemeStoreTests.CreateThemeJson("solarized"));
        var settingsPath = Path.Combine(directory, "settings.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var themeStore = new CodingAgentThemeStore(
            cwd: directory,
            userThemesDirectory: Path.Combine(directory, "missing-user-themes"),
            explicitPaths: []);
        CodingAgentThemeStatus? selectorStatus = null;
        string? selectorCurrentTheme = null;
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            themeStore: themeStore,
            themeSelector: (status, currentTheme, _) =>
            {
                selectorStatus = status;
                selectorCurrentTheme = currentTheme;
                return Task.FromResult<string?>("SOLARIZED");
            });

        try
        {
            settingsStore.Save(new CodingAgentSettingsSnapshot(
                "openai",
                "gpt-5.4",
                "no-tools",
                RetryMaxAttempts: 4,
                RetryBaseDelayMilliseconds: 125,
                DefaultThinkingLevel: "high",
                EnabledModels: ["google/gemini-2.5-pro"]));

            var result = await router.TryHandleAsync("/theme select");
            var afterSelect = settingsStore.Load();

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("theme: solarized", result.Message);
            Assert.Equal("solarized", afterSelect.Theme);
            Assert.Equal("openai", afterSelect.DefaultProvider);
            Assert.Equal("gpt-5.4", afterSelect.DefaultModel);
            Assert.Equal("no-tools", afterSelect.TreeFilterMode);
            Assert.Equal(4, afterSelect.RetryMaxAttempts);
            Assert.Equal(125, afterSelect.RetryBaseDelayMilliseconds);
            Assert.Equal("high", afterSelect.DefaultThinkingLevel);
            Assert.Equal(["google/gemini-2.5-pro"], afterSelect.EnabledModels);
            Assert.NotNull(selectorStatus);
            Assert.Contains(selectorStatus.Themes, theme => theme.Name == "solarized");
            Assert.Equal("dark", selectorCurrentTheme);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryHandleAsync_ThemeCommand_SelectCancelDoesNotChangeSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-theme-select-cancel-" + Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(directory, "settings.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var themeStore = new CodingAgentThemeStore(
            cwd: directory,
            userThemesDirectory: Path.Combine(directory, "missing-user-themes"),
            explicitPaths: []);
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            themeStore: themeStore,
            themeSelector: (_, _, _) => Task.FromResult<string?>(null));

        try
        {
            settingsStore.Save(new CodingAgentSettingsSnapshot(
                "openai",
                "gpt-5.4",
                Theme: "light"));

            var result = await router.TryHandleAsync("/theme select");
            var afterCancel = settingsStore.Load();

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("theme selection cancelled", result.Message);
            Assert.Equal("light", afterCancel.Theme);
            Assert.Equal("openai", afterCancel.DefaultProvider);
            Assert.Equal("gpt-5.4", afterCancel.DefaultModel);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ThemeCommand_ReturnsUsageAvailabilityAndMissingThemeErrors()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-theme-errors-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var unavailableRouter = new CodingAgentCommandRouter(runner);
        var noThemeStoreRouter = new CodingAgentCommandRouter(runner, settingsStore);
        var themeStore = new CodingAgentThemeStore(
            cwd: Path.GetTempPath(),
            userThemesDirectory: Path.Combine(Path.GetTempPath(), "missing-user-themes-" + Guid.NewGuid().ToString("N")),
            explicitPaths: [],
            includeDefaults: false);
        var router = new CodingAgentCommandRouter(runner, settingsStore, themeStore: themeStore);

        try
        {
            var unavailableSettings = await unavailableRouter.TryHandleAsync("/theme");
            var unavailableDiscovery = await noThemeStoreRouter.TryHandleAsync("/theme list");
            var missingTheme = await router.TryHandleAsync("/theme set missing");
            var unavailableSelector = await router.TryHandleAsync("/theme select");
            var invalid = await router.TryHandleAsync("/theme set");
            var extra = await router.TryHandleAsync("/theme clear dark");

            Assert.True(unavailableSettings.Handled);
            Assert.True(unavailableSettings.IsError);
            Assert.Equal("theme settings are not available in this session", unavailableSettings.Message);

            Assert.True(unavailableDiscovery.Handled);
            Assert.True(unavailableDiscovery.IsError);
            Assert.Equal("theme discovery is not available in this session", unavailableDiscovery.Message);

            Assert.True(missingTheme.Handled);
            Assert.True(missingTheme.IsError);
            Assert.Equal("theme 'missing' is not available", missingTheme.Message);

            Assert.True(unavailableSelector.Handled);
            Assert.True(unavailableSelector.IsError);
            Assert.Equal("theme selector is not available in this session", unavailableSelector.Message);

            Assert.All(
                [invalid, extra],
                result =>
                {
                    Assert.True(result.Handled);
                    Assert.True(result.IsError);
                    Assert.Equal("usage: /theme [current|list|select|set|clear] [name]", result.Message);
                });
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_PromptsCommand_ListsPromptTemplates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-prompts-router-" + Guid.NewGuid().ToString("N"));
        var prompts = Path.Combine(directory, ".tau", "prompts");
        Directory.CreateDirectory(prompts);
        await File.WriteAllTextAsync(
            Path.Combine(prompts, "review.md"),
            """
            ---
            description: Review a file
            argument-hint: <file>
            ---
            Review $1
            """);
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var promptStore = new CodingAgentPromptTemplateStore(cwd: directory);
        var router = new CodingAgentCommandRouter(runner, promptTemplateStore: promptStore);

        try
        {
            var result = await router.TryHandleAsync("/prompts");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("prompts: /review <file> - Review a file", result.Message);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryHandleAsync_SkillsCommand_ListsSkillCommands()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-skills-router-" + Guid.NewGuid().ToString("N"));
        var skillDirectory = Path.Combine(directory, ".tau", "skills", "reviewer");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(skillDirectory, "SKILL.md"),
            """
            ---
            name: reviewer
            description: Review source changes
            ---
            Check the diff.
            """);
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var skillStore = new CodingAgentSkillStore(cwd: directory);
        var router = new CodingAgentCommandRouter(runner, skillStore: skillStore);

        try
        {
            var result = await router.TryHandleAsync("/skills");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("skills: /skill:reviewer - Review source changes", result.Message);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExtensionsCommand_ListsExtensionCommands()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-router-" + Guid.NewGuid().ToString("N"));
        var extensions = Path.Combine(directory, ".tau", "extensions");
        Directory.CreateDirectory(extensions);
        var extensionFile = Path.Combine(extensions, "commands.json");
        await File.WriteAllTextAsync(
            extensionFile,
            """
            {
              "commands": [
                {
                  "name": "hello",
                  "description": "Say hello",
                  "argumentHint": "<name>",
                  "response": "Hello $ARGUMENTS"
                },
                {
                  "name": "review",
                  "description": "Review source",
                  "prompt": "Review $1",
                  "sendToRunner": true
                }
              ]
            }
            """);
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var extensionStore = new CodingAgentExtensionCommandStore(
            cwd: directory,
            userExtensionsDirectory: Path.Combine(directory, "missing-user-extensions"));
        var router = new CodingAgentCommandRouter(runner, extensionCommandStore: extensionStore);

        try
        {
            var result = await router.TryHandleAsync("/extensions");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("extensions: /hello <name> - Say hello (project); /review - Review source (project, runner)", result.Message, StringComparison.Ordinal);
            Assert.Contains($"extension files: {extensionFile} (project, 2 commands, 0 prompts, 0 skills, 0 themes)", result.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExtensionsCommand_ListsJavascriptRegisteredCommandsAndModuleStatus()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-router-js-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "hello-js");
        Directory.CreateDirectory(extensionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(extensionDirectory, "package.json"),
            """
            {
              "type": "module",
              "pi": {
                "extensions": ["index.js"]
              }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(extensionDirectory, "index.js"),
            """
            export default function(pi) {
              pi.registerCommand("hello-js", {
                description: "Say hello from JS",
                argumentHint: "<name>",
                handler: async (args, ctx) => {
                  ctx.sendMessage(`Hello ${args}`);
                }
              });
            }
            """);
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var extensionStore = new CodingAgentExtensionCommandStore(
            cwd: directory,
            userExtensionsDirectory: Path.Combine(directory, "missing-user-extensions"),
            javaScriptRuntime: new CodingAgentJavaScriptExtensionRuntime(directory, nodeExecutable: "node"));
        var router = new CodingAgentCommandRouter(runner, extensionCommandStore: extensionStore);

        try
        {
            var result = await router.TryHandleAsync("/extensions");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("extensions: /hello-js <name> - Say hello from JS (project, javascript)", result.Message, StringComparison.Ordinal);
            Assert.Contains($"extension modules: {Path.Combine(extensionDirectory, "index.js")} (project, javascript, loaded; commands 1; limited runtime)", result.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExtensionsCommand_ShowsLoadDiagnostics()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-router-diagnostics-" + Guid.NewGuid().ToString("N"));
        var extensions = Path.Combine(directory, ".tau", "extensions");
        Directory.CreateDirectory(extensions);
        var badFile = Path.Combine(extensions, "bad.json");
        var missingFile = Path.Combine(directory, "missing.json");
        await File.WriteAllTextAsync(badFile, "{ invalid");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var extensionStore = new CodingAgentExtensionCommandStore(
            cwd: directory,
            explicitPaths: [missingFile]);
        var router = new CodingAgentCommandRouter(runner, extensionCommandStore: extensionStore);

        try
        {
            var result = await router.TryHandleAsync("/extensions");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("extensions: none", result.Message, StringComparison.Ordinal);
            Assert.Contains($"error {badFile} (project) - failed to load extension json:", result.Message, StringComparison.Ordinal);
            Assert.Contains($"warning {missingFile} (path) - extension path does not exist", result.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CommandCatalog_HelpLine_MatchesSupportedCommandNames()
    {
        Assert.Equal(
            "commands: /help, /reload, /hotkeys, /settings, /theme, /name, /copy, /files, /export, /share, /import, /new, /session, /metadata, /tree, /label, /fork, /clone, /resume, /quit, /model, /provider, /models, /providers, /scoped-models, /prompts, /skills, /extensions, /auth, /login, /logout, /changelog, /retry, /thinking, /history, /find, /clear, /compact",
            CodingAgentCommandCatalog.HelpLine);
        Assert.All(CodingAgentCommandCatalog.SupportedCommands, command =>
        {
            Assert.StartsWith("/", command.Name, StringComparison.Ordinal);
            Assert.StartsWith(command.Name, command.Usage, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(command.Description));
        });
    }

    [Fact]
    public async Task TryHandleAsync_HelpCommand_ReturnsSupportedCommandsWithoutInvokingRunner()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/help");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal(
            "commands: /help, /reload, /hotkeys, /settings, /theme, /name, /copy, /files, /export, /share, /import, /new, /session, /metadata, /tree, /label, /fork, /clone, /resume, /quit, /model, /provider, /models, /providers, /scoped-models, /prompts, /skills, /extensions, /auth, /login, /logout, /changelog, /retry, /thinking, /history, /find, /clear, /compact",
            result.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_HelpCommandWithExtraArgs_ReturnsUsage()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/help all");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("usage: /help", result.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_AuthSelect_UsesSelectorAndReturnsSelectedProviderStatus()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("openai", false, "none", false, true, "No credentials found.")
        };
        var selectorStates = new List<CodingAgentAuthSelectorState>();
        var router = new CodingAgentCommandRouter(
            runner,
            authSelector: (state, _) =>
            {
                selectorStates.Add(state);
                return Task.FromResult<string?>("google");
            });

        var result = await router.TryHandleAsync("/auth select");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal("auth google: missing via none. No credentials found.", result.Message);
        var state = Assert.Single(selectorStates);
        Assert.Equal("openai", state.CurrentProvider);
        Assert.Equal(["google", "openai"], state.Providers.Select(status => status.Provider).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_AuthSelect_CancelAndUnavailableDoNotInvokeRunner()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var cancelRouter = new CodingAgentCommandRouter(
            runner,
            authSelector: (_, _) => Task.FromResult<string?>(null));
        var unavailableRouter = new CodingAgentCommandRouter(runner);

        var cancelled = await cancelRouter.TryHandleAsync("/auth select");
        var unavailable = await unavailableRouter.TryHandleAsync("/auth select");

        Assert.True(cancelled.Handled);
        Assert.False(cancelled.IsError);
        Assert.Equal("auth selection cancelled", cancelled.Message);
        Assert.True(unavailable.Handled);
        Assert.True(unavailable.IsError);
        Assert.Equal("auth selector is not available in this session", unavailable.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_AuthCurrentAndExplicitProvider_ReturnStatuses()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("openai", true, "environment", false, false, "Credentials are available.")
        };
        var router = new CodingAgentCommandRouter(runner);

        var current = await router.TryHandleAsync("/auth current");
        var explicitProvider = await router.TryHandleAsync("/auth anthropic");

        Assert.True(current.Handled);
        Assert.False(current.IsError);
        Assert.Equal("auth openai: configured via environment. Credentials are available.", current.Message);
        Assert.True(explicitProvider.Handled);
        Assert.False(explicitProvider.IsError);
        Assert.Equal("auth anthropic: configured via environment. Credentials are available.", explicitProvider.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_LoginBareCommandWithSelector_UsesSelectedOAuthProvider()
    {
        var oauthProvider = new FakeOAuthProvider { Id = "google", Name = "Google" };
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("openai", false, "none", false, true, "No credentials found."),
            OAuthProvider = oauthProvider
        };
        var selectorStates = new List<CodingAgentAuthSelectorState>();
        var router = new CodingAgentCommandRouter(
            runner,
            authSelector: (state, _) =>
            {
                selectorStates.Add(state);
                return Task.FromResult<string?>("google");
            });

        var result = await router.TryHandleAsync("/login");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal("login google: authenticated successfully. Credentials saved to auth.json.", result.Message);
        Assert.Equal(1, oauthProvider.LoginCalls);
        Assert.NotNull(oauthProvider.LastCallbacks);
        var saved = runner.SavedOAuthCredentials;
        Assert.True(saved.HasValue);
        Assert.Equal("google", saved.Value.ProviderId);
        var state = Assert.Single(selectorStates);
        var provider = Assert.Single(state.Providers);
        Assert.Equal("google", provider.Provider);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_LoginSelector_CancelAndUnavailableDoNotStartOAuth()
    {
        var oauthProvider = new FakeOAuthProvider { Id = "google", Name = "Google" };
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("openai", false, "none", false, true, "No credentials found."),
            OAuthProvider = oauthProvider
        };
        var cancelRouter = new CodingAgentCommandRouter(
            runner,
            authSelector: (_, _) => Task.FromResult<string?>(null));
        var unavailableRouter = new CodingAgentCommandRouter(runner);

        var cancelled = await cancelRouter.TryHandleAsync("/login");
        var unavailable = await unavailableRouter.TryHandleAsync("/login select");

        Assert.True(cancelled.Handled);
        Assert.False(cancelled.IsError);
        Assert.Equal("login selection cancelled", cancelled.Message);
        Assert.True(unavailable.Handled);
        Assert.True(unavailable.IsError);
        Assert.Equal("login selector is not available in this session", unavailable.Message);
        Assert.Equal(0, oauthProvider.LoginCalls);
        Assert.Null(runner.SavedOAuthCredentials);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_LoginBareCommandWithoutSelector_PreservesCurrentProviderLogin()
    {
        var oauthProvider = new FakeOAuthProvider { Id = "openai" };
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("openai", false, "none", false, true, "No credentials found."),
            OAuthProvider = oauthProvider
        };
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/login");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal("login openai: authenticated successfully. Credentials saved to auth.json.", result.Message);
        Assert.Equal(1, oauthProvider.LoginCalls);
        var saved = runner.SavedOAuthCredentials;
        Assert.True(saved.HasValue);
        Assert.Equal("openai", saved.Value.ProviderId);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_LoginExplicitProvider_DoesNotUseSelector()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("openai", true, "auth.json api_key", false, false, "API key entry found in auth.json.")
        };
        var selectorCalls = 0;
        var router = new CodingAgentCommandRouter(
            runner,
            authSelector: (_, _) =>
            {
                selectorCalls++;
                return Task.FromResult<string?>("google");
            });

        var result = await router.TryHandleAsync("/login anthropic");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal("auth anthropic: already configured via auth.json api_key.", result.Message);
        Assert.Equal(0, selectorCalls);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_LogoutBareCommandWithSelector_UsesSelectedOAuthProvider()
    {
        var oauthProvider = new FakeOAuthProvider { Id = "google", Name = "Google" };
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("openai", false, "none", false, false, "No credentials found."),
            OAuthProvider = oauthProvider,
            LogoutResult = true
        };
        runner.AuthStatuses["google"] = new("google", true, "auth.json oauth", true, true, "OAuth credentials found in auth.json.");
        var selectorStates = new List<CodingAgentAuthSelectorState>();
        var router = new CodingAgentCommandRouter(
            runner,
            authSelector: (state, _) =>
            {
                selectorStates.Add(state);
                return Task.FromResult<string?>("google");
            });

        var result = await router.TryHandleAsync("/logout");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal(
            "logout google: auth.json credentials removed. Environment variables and models.json credentials are unchanged.",
            result.Message);
        Assert.Equal(["google"], runner.LoggedOutProviders);
        var state = Assert.Single(selectorStates);
        var provider = Assert.Single(state.Providers);
        Assert.Equal("google", provider.Provider);
        Assert.True(provider.UsesOAuth);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_LogoutSelector_CancelUnavailableAndNoOAuthDoNotRemoveCredentials()
    {
        var oauthProvider = new FakeOAuthProvider { Id = "google", Name = "Google" };
        var cancelRunner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            OAuthProvider = oauthProvider,
            LogoutResult = true
        };
        cancelRunner.AuthStatuses["google"] = new("google", true, "auth.json oauth", true, true, "OAuth credentials found in auth.json.");
        var cancelRouter = new CodingAgentCommandRouter(
            cancelRunner,
            authSelector: (_, _) => Task.FromResult<string?>(null));
        var unavailableRunner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            OAuthProvider = oauthProvider
        };
        unavailableRunner.AuthStatuses["google"] = new("google", true, "auth.json oauth", true, true, "OAuth credentials found in auth.json.");
        var unavailableRouter = new CodingAgentCommandRouter(unavailableRunner);
        var noOAuthRunner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("openai", true, "auth.json api_key", false, false, "API key entry found in auth.json."),
            OAuthProvider = oauthProvider
        };
        var selectorCalls = 0;
        var noOAuthRouter = new CodingAgentCommandRouter(
            noOAuthRunner,
            authSelector: (_, _) =>
            {
                selectorCalls++;
                return Task.FromResult<string?>("google");
            });

        var cancelled = await cancelRouter.TryHandleAsync("/logout");
        var unavailable = await unavailableRouter.TryHandleAsync("/logout select");
        var noOAuth = await noOAuthRouter.TryHandleAsync("/logout select");

        Assert.True(cancelled.Handled);
        Assert.False(cancelled.IsError);
        Assert.Equal("logout selection cancelled", cancelled.Message);
        Assert.Empty(cancelRunner.LoggedOutProviders);
        Assert.True(unavailable.Handled);
        Assert.True(unavailable.IsError);
        Assert.Equal("logout selector is not available in this session", unavailable.Message);
        Assert.Empty(unavailableRunner.LoggedOutProviders);
        Assert.True(noOAuth.Handled);
        Assert.False(noOAuth.IsError);
        Assert.Equal("No OAuth providers logged in. Use /login first.", noOAuth.Message);
        Assert.Equal(0, selectorCalls);
        Assert.Empty(noOAuthRunner.LoggedOutProviders);
        Assert.Empty(cancelRunner.Inputs);
        Assert.Empty(unavailableRunner.Inputs);
        Assert.Empty(noOAuthRunner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_LogoutCommand_RemovesDefaultProviderAuthJsonCredentials()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("openai", true, "auth.json api_key", false, false, "API key entry found in auth.json."),
            LogoutResult = true
        };
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/logout");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal(
            "logout openai: auth.json credentials removed. Environment variables and models.json credentials are unchanged.",
            result.Message);
        Assert.Equal(["openai"], runner.LoggedOutProviders);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_LogoutCommand_UsesExplicitProviderAndReportsMissingAuthJsonEntry()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("openai", false, "none", false, false, "No credentials found."),
            LogoutResult = false
        };
        var selectorCalls = 0;
        var router = new CodingAgentCommandRouter(
            runner,
            authSelector: (_, _) =>
            {
                selectorCalls++;
                return Task.FromResult<string?>("google");
            });

        var result = await router.TryHandleAsync("/logout anthropic");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal(
            "logout anthropic: no auth.json credentials found. Environment variables and models.json credentials are unchanged.",
            result.Message);
        Assert.Equal(["anthropic"], runner.LoggedOutProviders);
        Assert.Equal(0, selectorCalls);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_LogoutCommandWithExtraArgs_ReturnsUsage()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/logout anthropic now");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("usage: /logout [select|provider]", result.Message);
        Assert.Empty(runner.LoggedOutProviders);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_ChangelogCommand_ListsRecentReleaseNotesWithoutInvokingRunner()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-changelog-router-" + Guid.NewGuid().ToString("N"));
        var changelog = Path.Combine(directory, "feature-release-notes.md");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            changelog,
            """
            # 功能发布记录

            ## 2026-05

            | 日期 | 功能域 | 用户价值 | 变更摘要 |
            | --- | --- | --- | --- |
            | 2026-05-21 | CodingAgent | 用户可以查看最近变更。 | 新增 /changelog baseline。 |
            | 2026-05-20 | CodingAgent | 用户可以刷新本地资源。 | 新增 /reload baseline。 |
            """);
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(
            runner,
            changelogStore: new CodingAgentChangelogStore(changelog));

        try
        {
            var result = await router.TryHandleAsync("/changelog 1");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains($"changelog: 1/2 entries from {changelog}", result.Message, StringComparison.Ordinal);
            Assert.Contains("[1] 2026-05-21 CodingAgent", result.Message, StringComparison.Ordinal);
            Assert.Contains("用户价值: 用户可以查看最近变更。", result.Message, StringComparison.Ordinal);
            Assert.Contains("Use /changelog all to show all entries.", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("2026-05-20", result.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryHandleAsync_ChangelogCommand_AllListsEveryReleaseNote()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-changelog-router-all-" + Guid.NewGuid().ToString("N"));
        var changelog = Path.Combine(directory, "feature-release-notes.md");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            changelog,
            """
            | 日期 | 功能域 | 用户价值 | 变更摘要 |
            | --- | --- | --- | --- |
            | 2026-05-21 | CodingAgent | 用户可以查看最近变更。 | 新增 /changelog baseline。 |
            | 2026-05-20 | CodingAgent | 用户可以刷新本地资源。 | 新增 /reload baseline。 |
            """);
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(
            runner,
            changelogStore: new CodingAgentChangelogStore(changelog));

        try
        {
            var result = await router.TryHandleAsync("/changelog all");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("changelog: 2/2 entries", result.Message, StringComparison.Ordinal);
            Assert.Contains("2026-05-21", result.Message, StringComparison.Ordinal);
            Assert.Contains("2026-05-20", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Use /changelog all", result.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryHandleAsync_ChangelogCommand_ReturnsUsageForInvalidArguments()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var invalid = await router.TryHandleAsync("/changelog nope");
        var extra = await router.TryHandleAsync("/changelog 1 extra");

        Assert.True(invalid.Handled);
        Assert.True(invalid.IsError);
        Assert.Equal("usage: /changelog [count|all]", invalid.Message);
        Assert.True(extra.Handled);
        Assert.True(extra.IsError);
        Assert.Equal("usage: /changelog [count|all]", extra.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_ReloadCommand_ReloadsSettingsResourcesSkillsAndKeybindings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-reload-router-" + Guid.NewGuid().ToString("N"));
        var extensions = Path.Combine(directory, ".tau", "extensions");
        var extensionPrompts = Path.Combine(extensions, "prompts");
        var extensionSkills = Path.Combine(extensions, "skills", "reload-skill");
        var extensionThemes = Path.Combine(directory, ".tau", "theme-resources");
        Directory.CreateDirectory(extensionPrompts);
        Directory.CreateDirectory(extensionSkills);
        Directory.CreateDirectory(extensionThemes);
        await File.WriteAllTextAsync(
            Path.Combine(extensions, "reload.json"),
            """
            {
              "name": "reload-ext",
              "description": "Reload extension",
              "response": "ok",
              "resources": {
                "promptPaths": ["./prompts"],
                "skillPaths": ["./skills"],
                "themePaths": ["../theme-resources"]
              }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(extensionPrompts, "from-extension.md"),
            """
            ---
            description: From extension
            ---
            Prompt from extension.
            """);
        await File.WriteAllTextAsync(
            Path.Combine(extensionSkills, "SKILL.md"),
            """
            ---
            name: reload-skill
            description: Skill from extension
            ---
            Reloaded skill body.
            """);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "AGENTS.md"),
            "Reload context rules.");
        await File.WriteAllTextAsync(
            Path.Combine(extensionThemes, "reload-theme.json"),
            CodingAgentThemeStoreTests.CreateThemeJson("reload-theme"));

        var settingsPath = Path.Combine(directory, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            null,
            null,
            RetryMaxAttempts: 4,
            RetryBaseDelayMilliseconds: 25,
            DefaultThinkingLevel: "high",
            SteeringMode: "all",
            FollowUpMode: "all",
            Theme: "reload-theme"));
        var extensionResourceState = new CodingAgentExtensionResourceState();
        var extensionStore = new CodingAgentExtensionCommandStore(
            cwd: directory,
            userExtensionsDirectory: Path.Combine(directory, "missing-user-extensions"),
            explicitPaths: []);
        var promptStore = new CodingAgentPromptTemplateStore(
            cwd: directory,
            explicitPaths: [],
            additionalPathsProvider: () => extensionResourceState.PromptPaths);
        var skillStore = new CodingAgentSkillStore(
            cwd: directory,
            explicitPaths: [],
            additionalPathsProvider: () => extensionResourceState.SkillPaths);
        var contextFileStore = new CodingAgentContextFileStore(
            cwd: directory,
            userContextDirectory: Path.Combine(directory, "missing-user"));
        var themeStore = new CodingAgentThemeStore(
            cwd: directory,
            userThemesDirectory: Path.Combine(directory, "missing-user-themes"),
            explicitPaths: [],
            additionalPathsProvider: () => extensionResourceState.ThemePaths);
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            ThinkingLevel = ThinkingLevel.Low
        };
        CodingAgentRetryOptions? changedRetry = null;
        var reloadedBindings = KeyBindingMap.WithOverrides(new Dictionary<KeyBinding, EditorAction>
        {
            [new KeyBinding(ConsoleKey.F2, ConsoleModifiers.None)] = EditorAction.Submit,
            [new KeyBinding(ConsoleKey.Enter, ConsoleModifiers.None)] = EditorAction.None
        });
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            retryOptions: CodingAgentRetryOptions.Disabled,
            retryOptionsChanged: options => changedRetry = options,
            promptTemplateStore: promptStore,
            skillStore: skillStore,
            contextFileStore: contextFileStore,
            themeStore: themeStore,
            extensionCommandStore: extensionStore,
            keyBindings: KeyBindingMap.Default,
            extensionResourceState: extensionResourceState,
            reloadKeyBindings: () => reloadedBindings);

        try
        {
            var result = await router.TryHandleAsync("/reload");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("reload complete:", result.Message, StringComparison.Ordinal);
            Assert.Contains("settings: loaded, retry enabled 4 attempts, base 25ms, thinking high", result.Message, StringComparison.Ordinal);
            Assert.Contains("steering all, follow-up all", result.Message, StringComparison.Ordinal);
            Assert.Contains("extensions: 1 commands, 1 files, 0 issues", result.Message, StringComparison.Ordinal);
            Assert.Contains("prompts: 1", result.Message, StringComparison.Ordinal);
            Assert.Contains("skills: 1, runner prompt refreshed", result.Message, StringComparison.Ordinal);
            Assert.Contains("context files: 1, runner prompt refreshed", result.Message, StringComparison.Ordinal);
            Assert.Contains("keybindings:", result.Message, StringComparison.Ordinal);
            Assert.Contains("themes: 3, current reload-theme, issues 0", result.Message, StringComparison.Ordinal);
            Assert.Equal(ThinkingLevel.High, runner.ThinkingLevel);
            Assert.Equal(AgentQueueMode.All, runner.SteeringMode);
            Assert.Equal(AgentQueueMode.All, runner.FollowUpMode);
            Assert.Equal(new CodingAgentRetryOptions(4, 25), changedRetry);
            var refreshedSkill = Assert.Single(runner.LastRefreshedSkills ?? []);
            Assert.Equal("reload-skill", refreshedSkill.Name);
            var refreshedContextFile = Assert.Single(runner.LastRefreshedContextFiles ?? []);
            Assert.Equal(Path.Combine(directory, "AGENTS.md"), refreshedContextFile.FilePath);
            Assert.Equal("Reload context rules.", refreshedContextFile.Content);

            var prompts = await router.TryHandleAsync("/prompts");
            Assert.Equal("prompts: /from-extension - From extension", prompts.Message);

            var skills = await router.TryHandleAsync("/skills");
            Assert.Equal("skills: /skill:reload-skill - Skill from extension", skills.Message);

            var hotkeys = await router.TryHandleAsync("/hotkeys");
            Assert.Contains("F2", hotkeys.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Enter", hotkeys.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryHandleAsync_ReloadCommand_RejectsExtraArguments()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/reload all");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("usage: /reload", result.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_HotkeysCommand_ListsCurrentEditorBindingsWithoutInvokingRunner()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var bindings = KeyBindingMap.WithOverrides(new Dictionary<KeyBinding, EditorAction>
        {
            [new KeyBinding(ConsoleKey.F1, ConsoleModifiers.None)] = EditorAction.Submit,
            [new KeyBinding(ConsoleKey.Enter, ConsoleModifiers.None)] = EditorAction.None
        });
        var router = new CodingAgentCommandRouter(runner, keyBindings: bindings);

        var result = await router.TryHandleAsync("/hotkeys");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Contains("hotkeys:", result.Message, StringComparison.Ordinal);
        Assert.Contains("submit", result.Message, StringComparison.Ordinal);
        Assert.Contains("F1", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Enter", result.Message, StringComparison.Ordinal);
        Assert.Contains("cancel", result.Message, StringComparison.Ordinal);
        Assert.Contains("Ctrl+C", result.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_HotkeysCommand_WithoutBindingsReturnsError()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/hotkeys");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Contains("not available", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_HotkeysCommand_RejectsExtraArguments()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner, keyBindings: KeyBindingMap.Default);

        var result = await router.TryHandleAsync("/hotkeys all");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("usage: /hotkeys", result.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_CopyCommand_CopiesLastAssistantTextWithoutInvokingRunner()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("hello"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("first")]));
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new ThinkingContent("hidden"),
                new TextContent(" second "),
                new ToolCallContent("tool-1", "read_file", "{}"),
                new TextContent("third")
            ]));
        var clipboard = new FakeCodingAgentClipboard();
        var router = new CodingAgentCommandRouter(runner, clipboard: clipboard);

        var result = await router.TryHandleAsync("/copy");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal("copied last assistant message to clipboard", result.Message);
        Assert.Equal("second\n\nthird", Assert.Single(clipboard.CopiedTexts));
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_CopyCommandWithoutAssistantText_ReturnsError()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("hello"));
        var clipboard = new FakeCodingAgentClipboard();
        var router = new CodingAgentCommandRouter(runner, clipboard: clipboard);

        var result = await router.TryHandleAsync("/copy");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("no assistant text to copy", result.Message);
        Assert.Empty(clipboard.CopiedTexts);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_CopyCommandWithExtraArgs_ReturnsUsage()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var clipboard = new FakeCodingAgentClipboard();
        var router = new CodingAgentCommandRouter(runner, clipboard: clipboard);

        var result = await router.TryHandleAsync("/copy last");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("usage: /copy", result.Message);
        Assert.Empty(clipboard.CopiedTexts);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_ExportCommand_WritesFlatSessionSnapshotWithoutInvokingRunner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "exported session";
        runner.MutableMessages.Add(new UserMessage("hello"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("world")]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            var result = await router.TryHandleAsync($"/export {path}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal($"exported session to {Path.GetFullPath(path)}", result.Message);
            Assert.Empty(runner.Inputs);

            var loaded = new CodingAgentSessionStore(path).Load();
            Assert.Equal("exported session", loaded.Name);
            Assert.Equal("openai", loaded.Provider);
            Assert.Equal("gpt-5.4", loaded.Model);
            Assert.Equal(2, loaded.Messages.Count);
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
    public async Task TryHandleAsync_ExportCommandWithoutPath_WritesDefaultHtmlTranscript()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-default-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sessionFile = Path.Combine(directory, "session.json");
        var expectedPath = Path.Combine(directory, "tau-session-session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "default export";
        runner.MutableMessages.Add(new UserMessage("hello <tau>"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("world")]));
        var router = new CodingAgentCommandRouter(
            runner,
            sessionFile: sessionFile,
            retryOptions: new CodingAgentRetryOptions(2, 125));

        try
        {
            var result = await router.TryHandleAsync("/export");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal($"exported session transcript to {expectedPath}", result.Message);
            Assert.Empty(runner.Inputs);
            var html = File.ReadAllText(expectedPath);
            Assert.Contains("default export", html, StringComparison.Ordinal);
            Assert.Contains("hello &lt;tau&gt;", html, StringComparison.Ordinal);
            Assert.Contains("world", html, StringComparison.Ordinal);
            Assert.Contains("Download JSONL", html, StringComparison.Ordinal);
            Assert.Contains("session-jsonl", html, StringComparison.Ordinal);
            Assert.Contains("Branch Outline", html, StringComparison.Ordinal);
            Assert.Contains("id=\"message-1\"", html, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersTextCodeFences()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-code-fence-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "code fence export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new TextContent(
                    """
                    Before code
                    ```csharp
                    Console.WriteLine("<tau>");
                    ```
                    After code
                    """)
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal($"exported session transcript to {Path.GetFullPath(htmlPath)}", result.Message);
            Assert.Empty(runner.Inputs);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<pre class=\"content-text\">Before code</pre>", html, StringComparison.Ordinal);
            Assert.Contains("<figure class=\"code-block\">", html, StringComparison.Ordinal);
            Assert.Contains("<figcaption>csharp</figcaption>", html, StringComparison.Ordinal);
            Assert.Contains("<code data-language=\"csharp\">Console", html, StringComparison.Ordinal);
            Assert.Contains("<span class=\"syntax-string\">&quot;&lt;tau&gt;&quot;</span>", html, StringComparison.Ordinal);
            Assert.Contains("<pre class=\"content-text\">After code</pre>", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersPlainTextLinks()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-links-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "link export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new TextContent(
                    """
                    See [docs <tau>](https://example.com/docs?q=<tau>) and https://example.org/path?x=1.
                    Ignore [unsafe](javascript:alert(1)).
                    ```text
                    https://code.example/path
                    ```
                    """)
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal($"exported session transcript to {Path.GetFullPath(htmlPath)}", result.Message);
            Assert.Empty(runner.Inputs);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains(
                "<a href=\"https://example.com/docs?q=&lt;tau&gt;\" target=\"_blank\" rel=\"noreferrer noopener\">docs &lt;tau&gt;</a>",
                html,
                StringComparison.Ordinal);
            Assert.Contains(
                "<a href=\"https://example.org/path?x=1\" target=\"_blank\" rel=\"noreferrer noopener\">https://example.org/path?x=1</a>.",
                html,
                StringComparison.Ordinal);
            Assert.Contains("[unsafe](javascript:alert(1)).", html, StringComparison.Ordinal);
            Assert.DoesNotContain("href=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<code data-language=\"text\">https://code.example/path</code>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("href=\"https://code.example/path\"", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersInlineCodeSpans()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-inline-code-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "inline code export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new TextContent(
                    """
                    Run `dotnet test <tau>` and keep `https://inline.example/path` literal.
                    See [docs](https://example.com/docs) and https://example.org/path.
                    ```text
                    `not inline`
                    ```
                    """)
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal($"exported session transcript to {Path.GetFullPath(htmlPath)}", result.Message);
            Assert.Empty(runner.Inputs);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<code class=\"inline-code\">dotnet test &lt;tau&gt;</code>", html, StringComparison.Ordinal);
            Assert.Contains("<code class=\"inline-code\">https://inline.example/path</code>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("href=\"https://inline.example/path\"", html, StringComparison.Ordinal);
            Assert.Contains(
                "<a href=\"https://example.com/docs\" target=\"_blank\" rel=\"noreferrer noopener\">docs</a>",
                html,
                StringComparison.Ordinal);
            Assert.Contains(
                "<a href=\"https://example.org/path\" target=\"_blank\" rel=\"noreferrer noopener\">https://example.org/path</a>.",
                html,
                StringComparison.Ordinal);
            Assert.Contains("<code data-language=\"text\">`not inline`</code>", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersMarkdownBlockStructure()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-markdown-blocks-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "markdown block export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new TextContent(
                    """
                    # Plan <tau>
                    Intro with `src/Tau.cs` and [docs](https://example.com/docs).
                    - first item
                    - second https://example.org/list
                    1. ordered `one`
                    > quoted <safe>
                    ```md
                    # not a heading
                    - not a list
                    ```
                    """)
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal($"exported session transcript to {Path.GetFullPath(htmlPath)}", result.Message);
            Assert.Empty(runner.Inputs);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<div class=\"content-text rich-text\">", html, StringComparison.Ordinal);
            Assert.Contains("<h1>Plan &lt;tau&gt;</h1>", html, StringComparison.Ordinal);
            Assert.Contains(
                "<p>Intro with <code class=\"inline-code\">src/Tau.cs</code> and <a href=\"https://example.com/docs\" target=\"_blank\" rel=\"noreferrer noopener\">docs</a>.</p>",
                html,
                StringComparison.Ordinal);
            Assert.Contains("<ul>", html, StringComparison.Ordinal);
            Assert.Contains("<li>first item</li>", html, StringComparison.Ordinal);
            Assert.Contains(
                "<li>second <a href=\"https://example.org/list\" target=\"_blank\" rel=\"noreferrer noopener\">https://example.org/list</a></li>",
                html,
                StringComparison.Ordinal);
            Assert.Contains("<ol>", html, StringComparison.Ordinal);
            Assert.Contains("<li>ordered <code class=\"inline-code\">one</code></li>", html, StringComparison.Ordinal);
            Assert.Contains("<blockquote>", html, StringComparison.Ordinal);
            Assert.Contains("<p>quoted &lt;safe&gt;</p>", html, StringComparison.Ordinal);
            Assert.Contains("<code data-language=\"md\"># not a heading", html, StringComparison.Ordinal);
            Assert.Contains("- not a list</code>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<h1>not a heading</h1>", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersBlockquoteMarkdownBlocks()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-blockquote-blocks-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "blockquote blocks export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new TextContent(
                    """
                    > ## Quoted plan
                    > - quoted one
                    >   - quoted child
                    > 1. ordered quoted
                    > > nested quote with `code`
                    ```md
                    > - not quoted list
                    ```
                    """)
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);

            var html = File.ReadAllText(htmlPath);
            var normalizedHtml = html.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Contains("<blockquote>", html, StringComparison.Ordinal);
            Assert.Contains("<h2>Quoted plan</h2>", html, StringComparison.Ordinal);
            Assert.Contains(
                """
                <li>quoted one<ul>
                <li>quoted child</li>
                </ul>
                </li>
                """,
                normalizedHtml,
                StringComparison.Ordinal);
            Assert.Contains("<ol>", html, StringComparison.Ordinal);
            Assert.Contains("<li>ordered quoted</li>", html, StringComparison.Ordinal);
            Assert.Contains("<p>nested quote with <code class=\"inline-code\">code</code></p>", html, StringComparison.Ordinal);
            Assert.Contains("<code data-language=\"md\">&gt; - not quoted list</code>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<p>- quoted one</p>", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersMarkdownTaskLists()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-task-list-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "task list export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new TextContent(
                    """
                    - [x] **done <tau>**
                    - [ ] [docs](https://example.com/docs) `next`
                    1. [X] ordered _ready_
                    ```md
                    - [x] not a task
                    ```
                    """)
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal($"exported session transcript to {Path.GetFullPath(htmlPath)}", result.Message);
            Assert.Empty(runner.Inputs);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<li class=\"task-list-item\"><input type=\"checkbox\" disabled checked> <span><strong>done &lt;tau&gt;</strong></span></li>", html, StringComparison.Ordinal);
            Assert.Contains(
                "<li class=\"task-list-item\"><input type=\"checkbox\" disabled> <span><a href=\"https://example.com/docs\" target=\"_blank\" rel=\"noreferrer noopener\">docs</a> <code class=\"inline-code\">next</code></span></li>",
                html,
                StringComparison.Ordinal);
            Assert.Contains("<li class=\"task-list-item\"><input type=\"checkbox\" disabled checked> <span>ordered <em>ready</em></span></li>", html, StringComparison.Ordinal);
            Assert.Contains("<code data-language=\"md\">- [x] not a task</code>", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersNestedLists()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-nested-list-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "nested list export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new TextContent(
                    """
                    - outer one
                        - inner one
                        - inner two
                    - outer two
                        1. ordered inner
                        2. another inner
                    - outer three
                    """)
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");
            Assert.True(result.Handled);
            Assert.False(result.IsError);

            var html = File.ReadAllText(htmlPath);
            var normalizedHtml = html.Replace("\r\n", "\n", StringComparison.Ordinal);
            var ulOpens = System.Text.RegularExpressions.Regex.Matches(html, "<ul>").Count;
            Assert.True(ulOpens >= 2, $"expected at least two <ul> opens (outer + nested), saw {ulOpens}");
            Assert.Contains("<ol>", html, StringComparison.Ordinal);
            Assert.Contains(
                """
                <li>outer one<ul>
                <li>inner one</li>
                <li>inner two</li>
                </ul>
                </li>
                """,
                normalizedHtml,
                StringComparison.Ordinal);
            Assert.Contains(
                """
                <li>outer two<ol>
                <li>ordered inner</li>
                <li>another inner</li>
                </ol>
                </li>
                <li>outer three</li>
                """,
                normalizedHtml,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersAutolinkAngles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-autolink-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "autolink export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new TextContent(
                    """
                    See <https://example.com/docs> and <http://intranet/login>.
                    Inline code: `<https://no-link.example/>` keeps angles.
                    ```md
                    <https://fenced.example/>
                    ```
                    """)
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<a href=\"https://example.com/docs\" target=\"_blank\" rel=\"noreferrer noopener\">https://example.com/docs</a>", html, StringComparison.Ordinal);
            Assert.Contains("<a href=\"http://intranet/login\" target=\"_blank\" rel=\"noreferrer noopener\">http://intranet/login</a>", html, StringComparison.Ordinal);
            // Inline code keeps the literal angle-bracket form.
            Assert.Contains("<code class=\"inline-code\">&lt;https://no-link.example/&gt;</code>", html, StringComparison.Ordinal);
            // Fenced code keeps the literal angle-bracket form.
            Assert.Contains("<code data-language=\"md\">&lt;https://fenced.example/&gt;</code>", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersStrikethroughSpans()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-strike-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "strike export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new TextContent(
                    """
                    Inline ~~struck~~ word and ~~**both**~~ together.
                    Also ~~spaced  ~~ should not strike.
                    `~~code~~` keeps tildes.
                    ```md
                    ~~fenced~~
                    ```
                    """)
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<del>struck</del>", html, StringComparison.Ordinal);
            // Nested emphasis inside strikethrough renders both wrappers.
            Assert.Contains("<del><strong>both</strong></del>", html, StringComparison.Ordinal);
            // Spaced strike with trailing space should not be wrapped.
            Assert.DoesNotContain("<del>spaced", html, StringComparison.Ordinal);
            // Inline code keeps the literal tildes.
            Assert.Contains("<code class=\"inline-code\">~~code~~</code>", html, StringComparison.Ordinal);
            // Fenced code stays as code, no strikethrough.
            Assert.Contains("<code data-language=\"md\">~~fenced~~</code>", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersHorizontalRules()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-hr-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "hr export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new TextContent(
                    """
                    Above

                    ---
                    Between

                    ***
                    After

                    ___
                    Final

                    ```md
                    ---
                    ```
                    """)
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);

            var html = File.ReadAllText(htmlPath);
            // Three Markdown horizontal-rule variants must each render as <hr>.
            var hrCount = System.Text.RegularExpressions.Regex.Matches(html, "<hr>").Count;
            Assert.True(hrCount >= 3, $"expected at least three <hr> blocks, saw {hrCount}");
            // Surrounding plaintext segments still render normally.
            Assert.Contains("Above", html, StringComparison.Ordinal);
            Assert.Contains("Between", html, StringComparison.Ordinal);
            Assert.Contains("After", html, StringComparison.Ordinal);
            Assert.Contains("Final", html, StringComparison.Ordinal);
            // The fenced code block must keep --- as literal code, not an <hr>.
            Assert.Contains("<code data-language=\"md\">---</code>", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersEmphasisSpans()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-emphasis-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "emphasis export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new TextContent(
                    """
                    Plain **bold <tau>** and *italic* and __strong [docs](https://example.com)__ and _em `code`_.
                    Keep foo_bar_baz literal and `**not bold**` literal.
                    - **listed** item
                    ```text
                    **not bold**
                    ```
                    """)
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal($"exported session transcript to {Path.GetFullPath(htmlPath)}", result.Message);
            Assert.Empty(runner.Inputs);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<strong>bold &lt;tau&gt;</strong>", html, StringComparison.Ordinal);
            Assert.Contains("<em>italic</em>", html, StringComparison.Ordinal);
            Assert.Contains(
                "<strong>strong <a href=\"https://example.com\" target=\"_blank\" rel=\"noreferrer noopener\">docs</a></strong>",
                html,
                StringComparison.Ordinal);
            Assert.Contains("<em>em <code class=\"inline-code\">code</code></em>", html, StringComparison.Ordinal);
            Assert.Contains("Keep foo_bar_baz literal", html, StringComparison.Ordinal);
            Assert.Contains("<code class=\"inline-code\">**not bold**</code>", html, StringComparison.Ordinal);
            Assert.Contains("<li><strong>listed</strong> item</li>", html, StringComparison.Ordinal);
            Assert.Contains("<code data-language=\"text\">**not bold**</code>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<strong>not bold</strong>", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersMarkdownTables()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-tables-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "table export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new TextContent(
                    """
                    | Area | Status |
                    | --- | --- |
                    | `core` | **done <tau>** |
                    | [docs](https://example.com/docs) | _next_ |
                    ```md
                    | Not | Table |
                    | --- | --- |
                    ```
                    """)
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal($"exported session transcript to {Path.GetFullPath(htmlPath)}", result.Message);
            Assert.Empty(runner.Inputs);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<div class=\"table-scroll\"><table>", html, StringComparison.Ordinal);
            Assert.Contains("<th>Area</th>", html, StringComparison.Ordinal);
            Assert.Contains("<th>Status</th>", html, StringComparison.Ordinal);
            Assert.Contains("<td><code class=\"inline-code\">core</code></td>", html, StringComparison.Ordinal);
            Assert.Contains("<td><strong>done &lt;tau&gt;</strong></td>", html, StringComparison.Ordinal);
            Assert.Contains(
                "<td><a href=\"https://example.com/docs\" target=\"_blank\" rel=\"noreferrer noopener\">docs</a></td>",
                html,
                StringComparison.Ordinal);
            Assert.Contains("<td><em>next</em></td>", html, StringComparison.Ordinal);
            Assert.Contains("<code data-language=\"md\">| Not | Table |", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersImageMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-image-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "image export";
        runner.MutableMessages.Add(new UserMessage([new ImageContent("aGVsbG8=", "image/png")]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal($"exported session transcript to {Path.GetFullPath(htmlPath)}", result.Message);
            Assert.Empty(runner.Inputs);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<figure class=\"image-block\"><img alt=\"session image\" src=\"data:image/png;base64,aGVsbG8=\">", html, StringComparison.Ordinal);
            Assert.Contains("<figcaption>image/png, 5 bytes</figcaption>", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersReadFileToolResultMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-read-file-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "read file export";
        runner.MutableMessages.Add(new AssistantMessage(
        [
            new ToolCallContent("call-read", "read_file", """{"path":"src/Program.cs"}""")
        ]));
        runner.MutableMessages.Add(new ToolResultMessage(
            "call-read",
            [new TextContent("using System;\nConsole.WriteLine(\"hello\");")]));
        runner.MutableToolResultDetailsByToolCallId["call-read"] = ReadFileToolDetails.ForText(
            "src/Program.cs",
            "csharp",
            startLine: 1,
            endLine: 2,
            totalLines: 20,
            hasMore: true,
            truncation: null);
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<div class=\"tool-result-section tool-result-metadata\">", html, StringComparison.Ordinal);
            Assert.Contains("<dt>path</dt>", html, StringComparison.Ordinal);
            Assert.Contains("<dd>src/Program.cs</dd>", html, StringComparison.Ordinal);
            Assert.Contains("<dt>language</dt>", html, StringComparison.Ordinal);
            Assert.Contains("<dd>csharp</dd>", html, StringComparison.Ordinal);
            Assert.Contains("<dt>lines</dt>", html, StringComparison.Ordinal);
            Assert.Contains("<dd>1-2</dd>", html, StringComparison.Ordinal);
            Assert.Contains("<dt>total lines</dt>", html, StringComparison.Ordinal);
            Assert.Contains("<dd>20</dd>", html, StringComparison.Ordinal);
            Assert.Contains("<dt>has more</dt>", html, StringComparison.Ordinal);
            Assert.Contains("<dd>true</dd>", html, StringComparison.Ordinal);
            Assert.Contains("<code data-language=\"csharp\">", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersListDirectoryToolResultDetails()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-ls-details-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "ls export";
        runner.MutableMessages.Add(new AssistantMessage(
        [
            new ToolCallContent("call-ls", "ls", """{"path":"src","limit":3}""")
        ]));
        runner.MutableMessages.Add(new ToolResultMessage(
            "call-ls",
            [new TextContent("src/\nProgram.cs\n[50.0KB limit reached]")]));
        runner.MutableToolResultDetailsByToolCallId["call-ls"] = new ListDirectoryToolDetails(
            Truncation: new ToolOutputTruncationResult(
                Content: "src/\nProgram.cs",
                Truncated: true,
                TruncatedBy: "bytes",
                TotalLines: 4,
                TotalBytes: 65536,
                OutputLines: 2,
                OutputBytes: 120,
                LastLinePartial: false,
                FirstLineExceedsLimit: false,
                MaxLines: int.MaxValue,
                MaxBytes: 50 * 1024),
            EntryLimitReached: 3);
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<span class=\"tool-summary-key\">limit</span>", html, StringComparison.Ordinal);
            Assert.Contains("<span class=\"tool-summary-value\">3</span>", html, StringComparison.Ordinal);
            Assert.Contains("<li class=\"tool-result-directory\">src/</li>", html, StringComparison.Ordinal);
            Assert.Contains("<li>Program.cs</li>", html, StringComparison.Ordinal);
            Assert.Contains("<li>[50.0KB limit reached]</li>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<li class=\"tool-result-directory\">[50.0KB limit reached]</li>", html, StringComparison.Ordinal);
            Assert.Contains("<div class=\"tool-result-label\">directory metadata</div>", html, StringComparison.Ordinal);
            Assert.Contains("<dt>entry limit reached</dt>", html, StringComparison.Ordinal);
            Assert.Contains("<dd>3 entries</dd>", html, StringComparison.Ordinal);
            Assert.Contains("<dt>truncation</dt>", html, StringComparison.Ordinal);
            Assert.Contains("bytes, output 2 lines / 120B, total 4 lines / 64.0KB", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_FoldsLongToolResults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-tool-fold-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var longAssistantText = "assistant long text " + new string('a', 4100);
        var longToolText =
            """
            tool output <unsafe>
            ```json
            {"ok":"<yes>"}
            ```
            """
            + new string('x', 4100);
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "tool fold export";
        runner.MutableMessages.Add(new AssistantMessage([new TextContent(longAssistantText)]));
        runner.MutableMessages.Add(new ToolResultMessage("call-1", [new TextContent(longToolText)]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal($"exported session transcript to {Path.GetFullPath(htmlPath)}", result.Message);
            Assert.Empty(runner.Inputs);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<details class=\"tool-result-fold\">", html, StringComparison.Ordinal);
            Assert.Contains(
                $"<summary>Tool output, {longToolText.Length.ToString("N0", CultureInfo.InvariantCulture)} characters</summary>",
                html,
                StringComparison.Ordinal);
            Assert.Contains("tool output &lt;unsafe&gt;", html, StringComparison.Ordinal);
            Assert.Contains("<figcaption>json</figcaption>", html, StringComparison.Ordinal);
            Assert.Contains("<span class=\"syntax-property\">&quot;ok&quot;</span>", html, StringComparison.Ordinal);
            Assert.Contains("<span class=\"syntax-string\">&quot;&lt;yes&gt;&quot;</span>", html, StringComparison.Ordinal);
            Assert.Contains(new string('x', 120), html, StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"Tool output, {longAssistantText.Length.ToString("N0", CultureInfo.InvariantCulture)} characters",
                html,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RendersToolCallJsonArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-tool-json-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "tool json export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new ToolCallContent(
                    "call-json",
                    "write_file",
                    """{"path":"src/<tau>.cs","lines":["one","two"]}"""),
                new ToolCallContent("call-raw", "legacy_tool", "not-json <unsafe>")
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal($"exported session transcript to {Path.GetFullPath(htmlPath)}", result.Message);
            Assert.Empty(runner.Inputs);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<summary>write_file <span>call-json</span></summary>", html, StringComparison.Ordinal);
            Assert.Contains("<figure class=\"code-block\">", html, StringComparison.Ordinal);
            Assert.Contains("<figcaption>json</figcaption>", html, StringComparison.Ordinal);
            Assert.Contains("<span class=\"syntax-property\">&quot;path&quot;</span>", html, StringComparison.Ordinal);
            Assert.Contains("<span class=\"syntax-string\">&quot;src/&lt;tau&gt;.cs&quot;</span>", html, StringComparison.Ordinal);
            Assert.Contains("<span class=\"syntax-property\">&quot;lines&quot;</span>", html, StringComparison.Ordinal);
            Assert.Contains("<summary>legacy_tool <span>call-raw</span></summary>", html, StringComparison.Ordinal);
            Assert.Contains("<pre>not-json &lt;unsafe&gt;</pre>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<details class=\"tool-call-arguments-fold\">", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_FoldsLongToolCallArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-tool-arguments-fold-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var jsonArguments = "{\"payload\":\"<tau>" + new string('x', 4100) + "\"}";
        var rawArguments = "raw <unsafe>" + new string('y', 4100);
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "tool arguments fold export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new ToolCallContent("call-json-long", "bulk_write", jsonArguments),
                new ToolCallContent("call-raw-long", "legacy_tool", rawArguments)
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal($"exported session transcript to {Path.GetFullPath(htmlPath)}", result.Message);
            Assert.Empty(runner.Inputs);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<details class=\"tool-call-arguments-fold\">", html, StringComparison.Ordinal);
            Assert.Contains(
                $"<summary>Tool arguments, {jsonArguments.Length.ToString("N0", CultureInfo.InvariantCulture)} characters</summary>",
                html,
                StringComparison.Ordinal);
            Assert.Contains(
                $"<summary>Tool arguments, {rawArguments.Length.ToString("N0", CultureInfo.InvariantCulture)} characters</summary>",
                html,
                StringComparison.Ordinal);
            Assert.Contains("<summary>bulk_write <span>call-json-long</span></summary>", html, StringComparison.Ordinal);
            Assert.Contains("<summary>legacy_tool <span>call-raw-long</span></summary>", html, StringComparison.Ordinal);
            Assert.Contains("<figcaption>json</figcaption>", html, StringComparison.Ordinal);
            Assert.Contains("<span class=\"syntax-property\">&quot;payload&quot;</span>", html, StringComparison.Ordinal);
            Assert.Contains("<span class=\"syntax-string\">&quot;&lt;tau&gt;", html, StringComparison.Ordinal);
            Assert.Contains(new string('x', 120), html, StringComparison.Ordinal);
            Assert.Contains("<pre>raw &lt;unsafe&gt;", html, StringComparison.Ordinal);
            Assert.Contains(new string('y', 120), html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommandWithTreeSession_IncludesSessionMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-tree-{Guid.NewGuid():N}");
        var treePath = Path.Combine(directory, "session.jsonl");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "tree export";
        runner.MutableMessages.Add(new UserMessage("export tree metadata"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("metadata visible")]));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);
            tree.SyncFromRunner(runner);
            var userEntryId = ReadMessageEntryId(treePath, "user", "export tree metadata");
            tree.AppendLabelChange(userEntryId, "checkpoint");
            tree.Store.AppendModelChange("google", "gemini-2.5-pro");
            tree.Store.AppendCompaction(
                "summary after compacted history",
                userEntryId,
                42,
                fromHook: true,
                turnPrefixSummary: "## Original Request\nexport tree metadata\n\n## Early Progress\n- assistant: metadata visible");
            tree.AppendAutoRetryStart(1, 2, 0, "retry-token provider returned error 503");
            tree.AppendAutoRetryEnd(success: false, 1, "retry-token provider returned error 503");

            var cloneResult = await router.TryHandleAsync("/clone");
            var exportResult = await router.TryHandleAsync($"/export {htmlPath}");

            Assert.False(cloneResult.IsError);
            Assert.True(exportResult.Handled);
            Assert.False(exportResult.IsError);
            Assert.Equal($"exported session transcript to {Path.GetFullPath(htmlPath)}", exportResult.Message);
            Assert.Empty(runner.Inputs);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<dt>Cwd</dt>", html, StringComparison.Ordinal);
            Assert.Contains(Environment.CurrentDirectory, html, StringComparison.Ordinal);
            Assert.Contains("<dt>Parent session</dt>", html, StringComparison.Ordinal);
            Assert.Contains(treePath, html, StringComparison.Ordinal);
            Assert.Contains("metadata visible", html, StringComparison.Ordinal);
            Assert.Contains("data-entry-id=\"", html, StringComparison.Ordinal);
            Assert.Contains("copy-link-button", html, StringComparison.Ordinal);
            Assert.Contains("buildShareUrl", html, StringComparison.Ordinal);
            Assert.Contains("targetId", html, StringComparison.Ordinal);
            Assert.Contains("leafId", html, StringComparison.Ordinal);
            Assert.Contains("deep-linked", html, StringComparison.Ordinal);
            Assert.Contains("id=\"tree-search\"", html, StringComparison.Ordinal);
            Assert.Contains("tree-filter-button", html, StringComparison.Ordinal);
            Assert.Contains("data-filter=\"labeled-only\"", html, StringComparison.Ordinal);
            Assert.Contains("shouldShowTreeEntry", html, StringComparison.Ordinal);
            Assert.Contains("checkpoint", html, StringComparison.Ordinal);
            Assert.Contains("model change", html, StringComparison.Ordinal);
            Assert.Contains("google/gemini-2.5-pro", html, StringComparison.Ordinal);
            Assert.Contains("auto compaction", html, StringComparison.Ordinal);
            Assert.Contains("summary after compacted history", html, StringComparison.Ordinal);
            Assert.Contains("Turn Context (split turn)", html, StringComparison.Ordinal);
            Assert.Contains("assistant: metadata visible", html, StringComparison.Ordinal);
            Assert.Contains("42 estimated tokens", html, StringComparison.Ordinal);
            Assert.Contains("auto retry start", html, StringComparison.Ordinal);
            Assert.Contains("Retry attempt 1/2 after 0ms: retry-token provider returned error 503", html, StringComparison.Ordinal);
            Assert.Contains("auto retry end", html, StringComparison.Ordinal);
            Assert.Contains("Retry failed after attempt 1: retry-token provider returned error 503", html, StringComparison.Ordinal);
            Assert.Contains("retry-event", html, StringComparison.Ordinal);
            Assert.Contains("auto_retry_start", html, StringComparison.Ordinal);
            Assert.Contains("auto_retry_end", html, StringComparison.Ordinal);
            Assert.Contains("label change", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ShareCommand_ExportsTempHtmlAndCreatesGistWithoutInvokingRunner()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "share session";
        runner.MutableMessages.Add(new UserMessage("share this"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("shared answer")]));
        var shareClient = new FakeShareClient();
        var router = new CodingAgentCommandRouter(runner, shareClient: shareClient);

        var result = await router.TryHandleAsync("/share");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal("Share URL: https://pi.dev/session/#abc123\nGist: https://gist.github.com/user/abc123", result.Message);
        Assert.Empty(runner.Inputs);
        var sharedPath = Assert.IsType<string>(shareClient.SharedPath);
        var html = Assert.IsType<string>(shareClient.Html);
        Assert.False(File.Exists(sharedPath));
        Assert.Contains("share session", html, StringComparison.Ordinal);
        Assert.Contains("share this", html, StringComparison.Ordinal);
        Assert.Contains("shared answer", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_ShareCommandWithoutMessages_ReturnsError()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var shareClient = new FakeShareClient();
        var router = new CodingAgentCommandRouter(runner, shareClient: shareClient);

        var result = await router.TryHandleAsync("/share");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("nothing to share yet", result.Message);
        Assert.Null(shareClient.SharedPath);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_ImportCommand_RestoresFlatSessionSnapshotWithoutInvokingRunner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-import-{Guid.NewGuid():N}.json");
        var model = new Model
        {
            Provider = "google",
            Id = "gemini-2.5-pro",
            Name = "Gemini 2.5 Pro",
            Api = "google-gemini"
        };
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("stale"));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            new CodingAgentSessionStore(path).Save(
                [
                    new UserMessage("hello"),
                    new AssistantMessage([new TextContent("world")])
                ],
                model,
                "imported session");

            var result = await router.TryHandleAsync($"/import {path}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal(
                $"imported session from {Path.GetFullPath(path)}: 2 messages, model google/gemini-2.5-pro, name imported session",
                result.Message);
            Assert.Equal("google", runner.Model.Provider);
            Assert.Equal("gemini-2.5-pro", runner.Model.Id);
            Assert.Equal("imported session", runner.SessionName);
            Assert.Collection(
                runner.Messages,
                message => Assert.IsType<UserMessage>(message),
                message => Assert.IsType<AssistantMessage>(message));
            Assert.Empty(runner.Inputs);
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
    public async Task TryHandleAsync_ImportCommand_WithJsonlPathCanSummarizeCurrentBranchBeforeSwitching()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-import-jsonl-summary-" + Guid.NewGuid().ToString("N"));
        var currentPath = Path.Combine(directory, "current.jsonl");
        var importPath = Path.Combine(directory, "import.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            SessionName = "current session"
        };
        runner.MutableMessages.Add(new UserMessage("current task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("current answer")]));
        runner.BranchSummaryHandler = (messages, instructions, replaceInstructions, _) =>
        {
            Assert.Equal("focus imported session", instructions);
            Assert.False(replaceInstructions);
            Assert.Equal(2, messages.Count);
            Assert.Equal("current task", ReadText(messages[0]));
            Assert.Equal("current answer", ReadText(messages[1]));
            return Task.FromResult(new CodingAgentBranchSummaryResult("import summary body", messages.Count, 29));
        };

        try
        {
            Directory.CreateDirectory(directory);
            var currentTree = CodingAgentTreeSessionController.OpenOrCreate(currentPath);
            currentTree.SyncFromRunner(runner);

            var importedRunner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
            {
                SessionName = "imported session"
            };
            importedRunner.MutableMessages.Add(new UserMessage("imported task"));
            importedRunner.MutableMessages.Add(new AssistantMessage([new TextContent("imported answer")]));
            CodingAgentTreeSessionController.OpenOrCreate(importPath).SyncFromRunner(importedRunner);

            CodingAgentSessionSwitchPromptState? capturedPromptState = null;
            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: currentTree,
                sessionSwitchPrompt: (state, _) =>
                {
                    capturedPromptState = state;
                    return Task.FromResult(CodingAgentTreeNavigationDecision.SummarizeWith("focus imported session"));
                });

            var result = await router.TryHandleAsync($"/import {importPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains($"resumed session from {importPath}:", result.Message, StringComparison.Ordinal);
            Assert.Contains("previous branch summary 2 entries, tokens ~29", result.Message, StringComparison.Ordinal);
            Assert.Equal(importPath, currentTree.Path);
            Assert.Equal("imported session", runner.SessionName);
            Assert.Equal(2, runner.Messages.Count);
            Assert.Equal("imported task", ReadText(runner.Messages[0]));
            Assert.Equal("imported answer", ReadText(runner.Messages[1]));
            Assert.NotNull(capturedPromptState);
            Assert.Equal(CodingAgentTreeNavigationReason.ImportSession, capturedPromptState!.Reason);
            Assert.Equal(Path.GetFullPath(importPath), capturedPromptState.TargetSessionPath);
            Assert.Equal(2, capturedPromptState.EntryCount);

            var summarizedCurrent = new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(currentPath));
            var summarizedSnapshot = summarizedCurrent.LoadSnapshot();
            Assert.Single(summarizedSnapshot.Messages);
            Assert.Contains("import summary body", ReadText(summarizedSnapshot.Messages[0]), StringComparison.Ordinal);
            Assert.Equal("current session", summarizedSnapshot.Name);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ImportCommand_WithJsonlPathWhenSummaryPromptCancelled_KeepsCurrentSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-import-jsonl-cancel-" + Guid.NewGuid().ToString("N"));
        var currentPath = Path.Combine(directory, "current.jsonl");
        var importPath = Path.Combine(directory, "import.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            SessionName = "current session"
        };
        runner.MutableMessages.Add(new UserMessage("current task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("current answer")]));

        try
        {
            Directory.CreateDirectory(directory);
            var currentTree = CodingAgentTreeSessionController.OpenOrCreate(currentPath);
            currentTree.SyncFromRunner(runner);

            var importedRunner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
            {
                SessionName = "imported session"
            };
            importedRunner.MutableMessages.Add(new UserMessage("imported task"));
            CodingAgentTreeSessionController.OpenOrCreate(importPath).SyncFromRunner(importedRunner);

            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: currentTree,
                sessionSwitchPrompt: (_, _) => Task.FromResult(CodingAgentTreeNavigationDecision.CancelledDecision));

            var result = await router.TryHandleAsync($"/import {importPath}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("import cancelled", result.Message);
            Assert.Equal(currentPath, currentTree.Path);
            Assert.Equal("current session", runner.SessionName);
            Assert.Equal(2, runner.Messages.Count);
            Assert.Equal("current task", ReadText(runner.Messages[0]));
            Assert.Equal("current answer", ReadText(runner.Messages[1]));
            Assert.DoesNotContain("\"type\":\"branch_summary\"", File.ReadAllText(currentPath), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ImportCommandWithoutPath_ReturnsUsage()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/import");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("usage: /import <path>", result.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_ImportCommandWithMissingFile_ReturnsErrorAndKeepsCurrentSession()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-missing-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("current"));
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync($"/import {path}");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal($"session file not found: {Path.GetFullPath(path)}", result.Message);
        Assert.Equal("openai", runner.Model.Provider);
        Assert.Single(runner.Messages);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_NameCommand_ShowsSetsAndClearsSessionNameWithoutInvokingRunner()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var current = await router.TryHandleAsync("/name");
        var set = await router.TryHandleAsync("/name port slice");
        var updated = await router.TryHandleAsync("/name");
        var clear = await router.TryHandleAsync("/name clear");

        Assert.Equal("session name: none", current.Message);
        Assert.Equal("session name: port slice", set.Message);
        Assert.Equal("session name: port slice", updated.Message);
        Assert.Equal("session name: none", clear.Message);
        Assert.Null(runner.SessionName);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_NewCommand_ResetsSessionWithoutInvokingRunner()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("stale"));
        runner.SessionName = "stale name";
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/new");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal("started new session with model openai/gpt-5.4", result.Message);
        Assert.Equal(1, runner.ResetSessionCalls);
        Assert.Empty(runner.Messages);
        Assert.Null(runner.SessionName);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_NewCommand_CanSummarizeCurrentBranchBeforeResettingSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-new-summary-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            SessionName = "current session"
        };
        runner.MutableMessages.Add(new UserMessage("current task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("current answer")]));
        runner.BranchSummaryHandler = (messages, instructions, replaceInstructions, _) =>
        {
            Assert.Equal("focus reset", instructions);
            Assert.True(replaceInstructions);
            Assert.Equal(2, messages.Count);
            Assert.Equal("current task", ReadText(messages[0]));
            Assert.Equal("current answer", ReadText(messages[1]));
            return Task.FromResult(new CodingAgentBranchSummaryResult("new session summary body", messages.Count, 44));
        };

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);

            CodingAgentSessionSwitchPromptState? capturedPromptState = null;
            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: tree,
                sessionSwitchPrompt: (state, _) =>
                {
                    capturedPromptState = state;
                    return Task.FromResult(new CodingAgentTreeNavigationDecision(
                        Cancelled: false,
                        Summarize: true,
                        CustomInstructions: "focus reset",
                        ReplaceInstructions: true));
                });

            var result = await router.TryHandleAsync("/new");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("started new session with model openai/gpt-5.4", result.Message, StringComparison.Ordinal);
            Assert.Contains("previous branch summary 2 entries, tokens ~44", result.Message, StringComparison.Ordinal);
            Assert.Equal(1, runner.ResetSessionCalls);
            Assert.Empty(runner.Messages);
            Assert.Null(runner.SessionName);
            Assert.Empty(runner.Inputs);
            Assert.NotNull(capturedPromptState);
            Assert.Equal(CodingAgentTreeNavigationReason.NewSession, capturedPromptState!.Reason);
            Assert.Null(capturedPromptState.TargetSessionPath);
            Assert.Equal(2, capturedPromptState.EntryCount);

            var persisted = File.ReadAllText(treePath);
            Assert.Contains("\"type\":\"branch_summary\"", persisted, StringComparison.Ordinal);
            Assert.Contains("new session summary body", persisted, StringComparison.Ordinal);
            Assert.Contains("\"action\":\"new\"", persisted, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_NewCommand_WhenSwitchSummaryPromptCancelled_KeepsCurrentSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-new-summary-cancel-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            SessionName = "current session"
        };
        runner.MutableMessages.Add(new UserMessage("current task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("current answer")]));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);

            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: tree,
                sessionSwitchPrompt: (_, _) => Task.FromResult(CodingAgentTreeNavigationDecision.CancelledDecision));

            var result = await router.TryHandleAsync("/new");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("new session cancelled", result.Message);
            Assert.Equal(0, runner.ResetSessionCalls);
            Assert.Equal("current session", runner.SessionName);
            Assert.Equal(2, runner.Messages.Count);
            Assert.Equal("current task", ReadText(runner.Messages[0]));
            Assert.Equal("current answer", ReadText(runner.Messages[1]));
            Assert.DoesNotContain("\"type\":\"branch_summary\"", File.ReadAllText(treePath), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_NewCommand_WhenSessionSwitchHookCancels_KeepsCurrentSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-new-hook-cancel-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            SessionName = "current session"
        };
        runner.MutableMessages.Add(new UserMessage("current task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("current answer")]));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);

            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: tree,
                sessionSwitchHook: (_, _) => Task.FromResult<CodingAgentSessionSwitchHookResult?>(CodingAgentSessionSwitchHookResult.Cancel()));

            var result = await router.TryHandleAsync("/new");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("new session cancelled", result.Message);
            Assert.Equal(0, runner.ResetSessionCalls);
            Assert.Equal("current session", runner.SessionName);
            Assert.Equal(2, runner.Messages.Count);
            Assert.DoesNotContain("\"type\":\"branch_summary\"", File.ReadAllText(treePath), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_QuitCommand_ReturnsExitWithoutInvokingRunner()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/quit");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.True(result.ShouldExit);
        Assert.Equal("Goodbye!", result.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_QuitCommandWithExtraArgs_ReturnsUsage()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/quit now");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.False(result.ShouldExit);
        Assert.Equal("usage: /quit", result.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_SessionCommand_ReturnsFlatSessionStatsWithoutInvokingRunner()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("hello"));
        runner.SessionName = "port slice";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new TextContent("checking"),
                new ToolCallContent("tool-1", "read_file", "{}")
            ])
        {
            Usage = new Usage(
                InputTokens: 100,
                OutputTokens: 20,
                CacheReadTokens: 3,
                CacheWriteTokens: 4,
                ServiceTier: "flex",
                Cost: new UsageCost(0.01m, 0.02m, 0.003m, 0.004m))
        });
        runner.MutableMessages.Add(new ToolResultMessage("tool-1", [new TextContent("done")]));
        var sessionFile = Path.Combine(Path.GetTempPath(), "tau-session.json");
        var router = new CodingAgentCommandRouter(
            runner,
            sessionFile: sessionFile,
            retryOptions: new CodingAgentRetryOptions(2, 125));

        var result = await router.TryHandleAsync("/session");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal(
            $"session: name port slice, model openai/gpt-5.4, messages 3 (user 1, assistant 1, tool 1, toolCalls 1), tokens ~9/128000 context (127991 remaining), usage in/out/cache 100/20/7, cost $0.037, retry enabled 2 attempts, base 125ms, file {sessionFile}",
            result.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_SessionCommand_ReturnsAutoCompactionBudgetWhenConfigured()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage(new string('x', 40)));
        var router = new CodingAgentCommandRouter(
            runner,
            autoCompaction: new CodingAgentAutoCompactionOptions(32));

        var result = await router.TryHandleAsync("/session");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal(
            "session: name none, model openai/gpt-5.4, messages 1 (user 1, assistant 0, tool 0, toolCalls 0), tokens ~10/128000 context (127990 remaining), auto-compact 32 (22 remaining), retry off, file none",
            result.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_SessionCommand_WithTreeSessionIncludesTokenBudgetAndTreeStats()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-session-tree-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage(new string('t', 20)));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var result = await router.TryHandleAsync("/session");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.NotNull(result.Message);
            Assert.Contains("tokens ~5/128000 context (127995 remaining)", result.Message, StringComparison.Ordinal);
            Assert.Contains($", tree {treePath}, leaf ", result.Message, StringComparison.Ordinal);
            Assert.Contains("branch messages 1", result.Message, StringComparison.Ordinal);
            Assert.Contains($", cwd {Environment.CurrentDirectory}", result.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_SessionCommand_WithTreeSessionUsesPersistedBranchUsageCost()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-session-tree-cost-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("price this"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("priced")])
        {
            Usage = new Usage(
                InputTokens: 100,
                OutputTokens: 20,
                CacheReadTokens: 3,
                CacheWriteTokens: 4,
                ServiceTier: "flex",
                Cost: new UsageCost(0.01m, 0.02m, 0.003m, 0.004m))
        });

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);
            runner.MutableMessages[1] = new AssistantMessage([new TextContent("priced without runtime usage")]);
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var result = await router.TryHandleAsync("/session");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.NotNull(result.Message);
            Assert.Contains("usage in/out/cache 100/20/7", result.Message, StringComparison.Ordinal);
            Assert.Contains("cost $0.037", result.Message, StringComparison.Ordinal);
            Assert.Contains($", tree {treePath}, leaf ", result.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_MetadataCommand_ShowsTreeHeaderAndRecentMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-metadata-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            SessionName = "metadata slice"
        };
        runner.MutableMessages.Add(new UserMessage("inspect session metadata"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("metadata ready")]));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = new CodingAgentTreeSessionController(
                new CodingAgentTreeSessionStore(treePath, cwd: directory));
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var result = await router.TryHandleAsync("/metadata");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.NotNull(result.Message);
            Assert.Contains($"metadata: file {treePath}", result.Message, StringComparison.Ordinal);
            Assert.Contains($"cwd: {directory}", result.Message, StringComparison.Ordinal);
            Assert.Contains("parent session: none", result.Message, StringComparison.Ordinal);
            Assert.Contains("counts: entries 4, branch entries 4, messages 2, branch messages 2, branches 0, labels 0", result.Message, StringComparison.Ordinal);
            Assert.Contains("latest metadata (2):", result.Message, StringComparison.Ordinal);
            Assert.Contains("model openai/gpt-5.4", result.Message, StringComparison.Ordinal);
            Assert.Contains("session name metadata slice", result.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_MetadataCommand_InspectsSpecificTreeEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-entry-metadata-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("inspect this metadata"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("entry details")]));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);
            var userEntryId = ReadMessageEntryId(treePath, "user", "inspect this metadata");
            tree.AppendLabelChange(userEntryId, "checkpoint");
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var result = await router.TryHandleAsync($"/metadata {userEntryId}");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.NotNull(result.Message);
            Assert.Contains($"entry: {userEntryId}", result.Message, StringComparison.Ordinal);
            Assert.Contains("type: message", result.Message, StringComparison.Ordinal);
            Assert.Contains("path: branch", result.Message, StringComparison.Ordinal);
            Assert.Contains("depth: 1, children 1", result.Message, StringComparison.Ordinal);
            Assert.Contains("label: checkpoint", result.Message, StringComparison.Ordinal);
            Assert.Contains("message role: user", result.Message, StringComparison.Ordinal);
            Assert.Contains("content types: text", result.Message, StringComparison.Ordinal);
            Assert.Contains("preview: inspect this metadata", result.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void GetMetadataSnapshot_FocusedEntryIncludesRelationsAndSections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-entry-metadata-snapshot-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("inspect structured metadata"));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);
            var userEntryId = ReadMessageEntryId(treePath, "user", "inspect structured metadata");

            var snapshot = tree.GetMetadataSnapshot(userEntryId);

            Assert.Equal(userEntryId, snapshot.FocusEntryId);
            Assert.Contains(userEntryId, snapshot.VisibleEntryIds);
            Assert.All(snapshot.VisibleEntryIds, id => Assert.True(snapshot.EntriesById.ContainsKey(id)));
            var entry = snapshot.EntriesById[userEntryId];
            Assert.Contains(entry.OverviewLines, line => line.StartsWith("parent:", StringComparison.Ordinal));
            Assert.Contains(entry.Relations, relation => relation.Label == "parent");
            Assert.Contains(entry.Sections, section => section.Title == "Message");
            Assert.Contains(entry.Sections.SelectMany(section => section.Lines), line =>
                line.Contains("preview: inspect structured metadata", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_MetadataCommand_WithViewer_InvokesViewerWithoutTranscriptMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-metadata-viewer-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            SessionName = "viewer metadata"
        };
        runner.MutableMessages.Add(new UserMessage("inspect session metadata"));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = new CodingAgentTreeSessionController(
                new CodingAgentTreeSessionStore(treePath, cwd: directory));
            CodingAgentTreeMetadataSnapshot? capturedSnapshot = null;
            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: tree,
                metadataViewer: (snapshot, _) =>
                {
                    capturedSnapshot = snapshot;
                    return Task.CompletedTask;
                });

            var result = await router.TryHandleAsync("/metadata");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Null(result.Message);
            Assert.NotNull(capturedSnapshot);
            Assert.Equal(treePath, capturedSnapshot!.FilePath);
            Assert.Equal(directory, capturedSnapshot.Cwd);
            Assert.Null(capturedSnapshot.FocusEntryId);
            Assert.Contains(capturedSnapshot.VisibleEntryIds, entryId =>
            {
                var entry = capturedSnapshot.EntriesById[entryId];
                return entry.SummaryLine.Contains("session name viewer metadata", StringComparison.Ordinal) ||
                       entry.OverviewLines.Any(line => line.Contains("viewer metadata", StringComparison.Ordinal)) ||
                       entry.Sections.Any(section => section.Lines.Any(line => line.Contains("viewer metadata", StringComparison.Ordinal)));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_MetadataCommand_RequiresTreeSessionAndRejectsExtraArgs()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var unavailable = await router.TryHandleAsync("/metadata");
        var extraArgs = await router.TryHandleAsync("/metadata one two");

        Assert.True(unavailable.Handled);
        Assert.True(unavailable.IsError);
        Assert.Equal("tree sessions are not enabled", unavailable.Message);

        Assert.True(extraArgs.Handled);
        Assert.True(extraArgs.IsError);
        Assert.Equal("usage: /metadata [entry-id]", extraArgs.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_TreeCommand_AppliesFilterModesAndLabelTimestamps()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-filter-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("first task"));
        runner.MutableMessages.Add(new AssistantMessage([new ToolCallContent("call-1", "read_file", "{}")]));
        runner.MutableMessages.Add(new ToolResultMessage("call-1", [new TextContent("done")]));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);
            var userEntryId = ReadMessageEntryId(treePath, "user");
            tree.AppendLabelChange(userEntryId, "checkpoint");
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var defaultResult = await router.TryHandleAsync("/tree 20");
            var noToolsResult = await router.TryHandleAsync("/tree no-tools");
            var labeledResult = await router.TryHandleAsync("/tree labeled-only --label-time");
            var allResult = await router.TryHandleAsync("/tree 20 all");

            Assert.True(defaultResult.Handled);
            Assert.False(defaultResult.IsError);
            Assert.Contains("filter default", defaultResult.Message, StringComparison.Ordinal);
            Assert.Contains("message user first task", defaultResult.Message, StringComparison.Ordinal);
            Assert.Contains("message toolResult done", defaultResult.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("message assistant", defaultResult.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("model openai/gpt-5.4", defaultResult.Message, StringComparison.Ordinal);

            Assert.False(noToolsResult.IsError);
            Assert.Contains("filter no-tools", noToolsResult.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("message toolResult", noToolsResult.Message, StringComparison.Ordinal);

            Assert.False(labeledResult.IsError);
            Assert.Contains("filter labeled-only", labeledResult.Message, StringComparison.Ordinal);
            Assert.Contains("message user first task [checkpoint] @", labeledResult.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("message toolResult", labeledResult.Message, StringComparison.Ordinal);

            Assert.False(allResult.IsError);
            Assert.Contains("filter all", allResult.Message, StringComparison.Ordinal);
            Assert.Contains("model openai/gpt-5.4", allResult.Message, StringComparison.Ordinal);
            Assert.Contains("message assistant", allResult.Message, StringComparison.Ordinal);
            Assert.Contains("label ", allResult.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_TreeCommand_SearchesVisibleEntryTextAndLabels()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-search-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("investigate renderer layout"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("renderer fixed")]));
        runner.MutableMessages.Add(new UserMessage("update provider auth docs"));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);
            var authEntryId = ReadMessageEntryId(treePath, "user", "provider auth");
            tree.AppendLabelChange(authEntryId, "docs-checkpoint");
            tree.AppendAutoRetryStart(1, 2, 0, "retry-token provider returned error 503");
            tree.AppendAutoRetryEnd(success: false, 1, "retry-token provider returned error 503");
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var rendererResult = await router.TryHandleAsync("/tree 20 --search renderer");
            var labelResult = await router.TryHandleAsync("/tree labeled-only --search docs-checkpoint");
            var retryResult = await router.TryHandleAsync("/tree 20 --search retry-token");
            var noMatchResult = await router.TryHandleAsync("/tree user-only --search no-such-token");
            var badSearchResult = await router.TryHandleAsync("/tree --search");

            Assert.True(rendererResult.Handled);
            Assert.False(rendererResult.IsError);
            Assert.Contains("filter default, search renderer", rendererResult.Message, StringComparison.Ordinal);
            Assert.Contains("message user investigate renderer layout", rendererResult.Message, StringComparison.Ordinal);
            Assert.Contains("message assistant renderer fixed", rendererResult.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("provider auth docs", rendererResult.Message, StringComparison.Ordinal);

            Assert.False(labelResult.IsError);
            Assert.Contains("filter labeled-only, search docs-checkpoint", labelResult.Message, StringComparison.Ordinal);
            Assert.Contains("provider auth docs [docs-checkpoint]", labelResult.Message, StringComparison.Ordinal);

            Assert.False(retryResult.IsError);
            Assert.Contains("filter default, search retry-token", retryResult.Message, StringComparison.Ordinal);
            Assert.Contains("auto-retry start 1/2 0ms retry-token provider returned error 503", retryResult.Message, StringComparison.Ordinal);
            Assert.Contains("auto-retry end failed attempt 1 retry-token provider returned error 503", retryResult.Message, StringComparison.Ordinal);

            Assert.False(noMatchResult.IsError);
            Assert.Contains("tree has no entries matching filter", noMatchResult.Message, StringComparison.Ordinal);

            Assert.True(badSearchResult.IsError);
            Assert.Equal("usage: /tree [max entries] [default|no-tools|user-only|labeled-only|all] [--label-time] [--search query] [--interactive]", badSearchResult.Message);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_TreeCommand_UsesConfiguredDefaultFilterMode()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-settings-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var settingsPath = Path.Combine(directory, "settings.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("settings task"));
        runner.MutableMessages.Add(new ToolResultMessage("call-1", [new TextContent("tool output")]));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);
            var settingsStore = new CodingAgentSettingsStore(settingsPath);
            settingsStore.Save(new CodingAgentSettingsSnapshot(null, null, "no-tools"));
            var router = new CodingAgentCommandRouter(runner, settingsStore, treeSessionController: tree);

            var configuredDefaultResult = await router.TryHandleAsync("/tree 20");
            var explicitAllResult = await router.TryHandleAsync("/tree 20 all");

            Assert.True(configuredDefaultResult.Handled);
            Assert.False(configuredDefaultResult.IsError);
            Assert.Contains("filter no-tools", configuredDefaultResult.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("message toolResult", configuredDefaultResult.Message, StringComparison.Ordinal);

            Assert.False(explicitAllResult.IsError);
            Assert.Contains("filter all", explicitAllResult.Message, StringComparison.Ordinal);
            Assert.Contains("message toolResult tool output", explicitAllResult.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ForkCommandWithSummarize_AppendsBranchSummaryAndRestoresContext()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-branch-summary-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var exportPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("root task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("root answer")]));
        runner.MutableMessages.Add(new UserMessage("abandoned branch request"));
        runner.MutableMessages.Add(new AssistantMessage(
        [
            new TextContent("abandoned branch progress"),
            new ToolCallContent("call-1", "edit_file", """{"path":"src/Branch.cs"}""")
        ]));
        runner.BranchSummaryHandler = (messages, instructions, replaceInstructions, _) =>
        {
            Assert.Equal("focus decisions", instructions);
            Assert.False(replaceInstructions);
            Assert.Equal(3, messages.Count);
            Assert.Equal("root answer", ReadText(messages[0]));
            Assert.Equal("abandoned branch request", ReadText(messages[1]));
            Assert.Contains("abandoned branch progress", ReadText(messages[2]), StringComparison.Ordinal);
            return Task.FromResult(new CodingAgentBranchSummaryResult(
                "branch summary body",
                messages.Count,
                123,
                ["docs/readme.md"],
                ["src/Branch.cs"]));
        };

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);
            var targetEntryId = ReadMessageEntryId(treePath, "user", "root task");
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var result = await router.TryHandleAsync($"/fork {targetEntryId} --summarize focus decisions");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("branch summary 3 entries, tokens ~123", result.Message, StringComparison.Ordinal);
            Assert.Equal("focus decisions", runner.LastBranchSummaryInstructions);
            Assert.Equal(2, runner.Messages.Count);
            Assert.Equal("root task", ReadText(runner.Messages[0]));
            Assert.Contains("Branch summary from", ReadText(runner.Messages[1]), StringComparison.Ordinal);
            Assert.Contains("branch summary body", ReadText(runner.Messages[1]), StringComparison.Ordinal);

            var branchSummary = ReadBranchSummaryEntry(treePath);
            Assert.Equal("branch_summary", branchSummary.GetProperty("type").GetString());
            Assert.Equal(targetEntryId, branchSummary.GetProperty("parentId").GetString());
            Assert.Equal(targetEntryId, branchSummary.GetProperty("fromId").GetString());
            Assert.Equal("branch summary body", branchSummary.GetProperty("summary").GetString());
            Assert.Equal("docs/readme.md", branchSummary.GetProperty("readFiles")[0].GetString());
            Assert.Equal("src/Branch.cs", branchSummary.GetProperty("modifiedFiles")[0].GetString());

            var treeResult = await router.TryHandleAsync("/tree 20 all --search branch summary body");
            Assert.False(treeResult.IsError);
            Assert.Contains("branch summary from", treeResult.Message, StringComparison.Ordinal);
            Assert.Contains("branch summary body", treeResult.Message, StringComparison.Ordinal);

            var exportResult = await router.TryHandleAsync($"/export {exportPath}");
            Assert.False(exportResult.IsError);
            var html = File.ReadAllText(exportPath);
            Assert.Contains("branch summary", html, StringComparison.Ordinal);
            Assert.Contains("branch summary body", html, StringComparison.Ordinal);
            Assert.Contains("docs/readme.md", html, StringComparison.Ordinal);
            Assert.Contains("src/Branch.cs", html, StringComparison.Ordinal);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_CloneCommand_DuplicatesCurrentBranchIntoNewSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-clone-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "clone source";
        runner.MutableMessages.Add(new UserMessage("clone this branch"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("branch copied")]));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var result = await router.TryHandleAsync("/clone");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.NotEqual(treePath, tree.Path);
            Assert.StartsWith(Path.Combine(directory, "coding-agent-sessions"), tree.Path, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(tree.Path));
            Assert.Contains($"cloned session to {tree.Path}: leaf ", result.Message, StringComparison.Ordinal);
            Assert.Contains("messages 2, model openai/gpt-5.4", result.Message, StringComparison.Ordinal);
            Assert.Equal("clone source", runner.SessionName);
            Assert.Equal(2, runner.Messages.Count);
            Assert.IsType<UserMessage>(runner.Messages[0]);
            var assistant = Assert.IsType<AssistantMessage>(runner.Messages[1]);
            Assert.Equal("branch copied", Assert.IsType<TextContent>(Assert.Single(assistant.Content)).Text);
            Assert.Empty(runner.Inputs);

            var cloneSnapshot = tree.LoadSnapshot();
            Assert.Equal(tree.Path, cloneSnapshot.FilePath);
            Assert.Equal(2, cloneSnapshot.Messages.Count);
            Assert.Equal("clone source", cloneSnapshot.Name);

            var cloneSummary = tree.GetSummary();
            Assert.Equal(Environment.CurrentDirectory, cloneSummary.Cwd);
            Assert.Equal(treePath, cloneSummary.ParentSession);

            var sessionResult = await router.TryHandleAsync("/session");
            var treeResult = await router.TryHandleAsync("/tree 20 all");

            Assert.False(sessionResult.IsError);
            Assert.Contains($", cwd {Environment.CurrentDirectory}, parent {treePath}", sessionResult.Message, StringComparison.Ordinal);
            Assert.False(treeResult.IsError);
            Assert.Contains($", cwd {Environment.CurrentDirectory}, parent {treePath}, filter all", treeResult.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_CloneCommandWithoutMessages_ReturnsNothingToClone()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-empty-clone-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var result = await router.TryHandleAsync("/clone");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("Nothing to clone yet", result.Message);
            Assert.Equal(treePath, tree.Path);
            Assert.False(Directory.Exists(Path.Combine(directory, "coding-agent-sessions")));
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ResumeCommandWithoutArgs_UsesSelectorAndSyncsCurrentSessionBeforeSwitching()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-resume-selector-" + Guid.NewGuid().ToString("N"));
        var currentPath = Path.Combine(directory, "current.jsonl");
        var otherPath = Path.Combine(directory, "coding-agent-sessions", "other.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "current session";
        runner.MutableMessages.Add(new UserMessage("current task"));

        try
        {
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);

            var currentTree = CodingAgentTreeSessionController.OpenOrCreate(currentPath);
            currentTree.SyncFromRunner(runner);

            var otherRunner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
            otherRunner.SessionName = "other session";
            otherRunner.MutableMessages.Add(new UserMessage("other task"));
            otherRunner.MutableMessages.Add(new AssistantMessage([new TextContent("other answer")]));
            var otherTree = CodingAgentTreeSessionController.OpenOrCreate(otherPath);
            otherTree.SyncFromRunner(otherRunner);

            runner.MutableMessages.Add(new AssistantMessage([new TextContent("unsaved current answer")]));

            CodingAgentResumeSelectorState? capturedState = null;
            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: currentTree,
                resumeSelector: (state, _) =>
                {
                    capturedState = state;
                    return Task.FromResult(new CodingAgentResumeSelectionResult(otherPath));
                });

            var result = await router.TryHandleAsync("/resume");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains($"resumed session from {otherPath}:", result.Message, StringComparison.Ordinal);
            Assert.Equal(otherPath, currentTree.Path);
            Assert.Equal("other session", runner.SessionName);
            Assert.Equal(2, runner.Messages.Count);
            Assert.Equal("other task", ReadText(runner.Messages[0]));
            Assert.Equal("other answer", ReadText(runner.Messages[1]));

            Assert.NotNull(capturedState);
            Assert.Equal(currentPath, capturedState!.CurrentSessionPath);
            Assert.Contains(capturedState.Sessions, session => session.FilePath == currentPath && session.IsCurrent);
            Assert.Contains(capturedState.Sessions, session => session.FilePath == otherPath);

            var persistedCurrent = new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(currentPath));
            var persistedSnapshot = persistedCurrent.LoadSnapshot();
            Assert.Equal(2, persistedSnapshot.Messages.Count);
            Assert.Equal("current task", ReadText(persistedSnapshot.Messages[0]));
            Assert.Equal("unsaved current answer", ReadText(persistedSnapshot.Messages[1]));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ResumeCommandWithoutArgs_WhenSelectionCancelled_ReturnsCancelled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-resume-selector-cancel-" + Guid.NewGuid().ToString("N"));
        var currentPath = Path.Combine(directory, "current.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "current session";
        runner.MutableMessages.Add(new UserMessage("current task"));

        try
        {
            Directory.CreateDirectory(directory);
            var currentTree = CodingAgentTreeSessionController.OpenOrCreate(currentPath);
            currentTree.SyncFromRunner(runner);

            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: currentTree,
                resumeSelector: (_, _) => Task.FromResult(new CodingAgentResumeSelectionResult(null)));

            var result = await router.TryHandleAsync("/resume");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("resume selection cancelled", result.Message);
            Assert.Equal(currentPath, currentTree.Path);
            Assert.Single(runner.Messages);
            Assert.Equal("current session", runner.SessionName);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ResumeCommandWithoutArgs_WhenCurrentSessionRenamedAndCancelled_UpdatesRunnerName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-resume-selector-rename-current-" + Guid.NewGuid().ToString("N"));
        var currentPath = Path.Combine(directory, "current.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "current session";
        runner.MutableMessages.Add(new UserMessage("current task"));

        try
        {
            Directory.CreateDirectory(directory);
            var currentTree = CodingAgentTreeSessionController.OpenOrCreate(currentPath);
            currentTree.SyncFromRunner(runner);

            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: currentTree,
                resumeSelector: (_, _) => Task.FromResult(new CodingAgentResumeSelectionResult(
                    SelectedPath: null,
                    RenamedCurrentSessionName: "renamed session")));

            var result = await router.TryHandleAsync("/resume");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("resume selection cancelled", result.Message);
            Assert.Equal("renamed session", runner.SessionName);
            Assert.Equal(currentPath, currentTree.Path);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ResumeCommandWithoutArgs_CanSummarizeCurrentBranchBeforeSwitching()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-resume-selector-summary-" + Guid.NewGuid().ToString("N"));
        var currentPath = Path.Combine(directory, "current.jsonl");
        var otherPath = Path.Combine(directory, "coding-agent-sessions", "other.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            SessionName = "current session"
        };
        runner.MutableMessages.Add(new UserMessage("current task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("current answer")]));
        runner.MutableMessages.Add(new UserMessage("follow up task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("follow up answer")]));
        runner.BranchSummaryHandler = (messages, instructions, replaceInstructions, _) =>
        {
            Assert.Equal("focus resume switch", instructions);
            Assert.False(replaceInstructions);
            Assert.Equal(4, messages.Count);
            Assert.Equal("current task", ReadText(messages[0]));
            Assert.Equal("current answer", ReadText(messages[1]));
            Assert.Equal("follow up task", ReadText(messages[2]));
            Assert.Equal("follow up answer", ReadText(messages[3]));
            return Task.FromResult(new CodingAgentBranchSummaryResult("resume summary body", messages.Count, 77));
        };

        try
        {
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);

            var currentTree = CodingAgentTreeSessionController.OpenOrCreate(currentPath);
            currentTree.SyncFromRunner(runner);

            var otherRunner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
            {
                SessionName = "other session"
            };
            otherRunner.MutableMessages.Add(new UserMessage("other task"));
            otherRunner.MutableMessages.Add(new AssistantMessage([new TextContent("other answer")]));
            CodingAgentTreeSessionController.OpenOrCreate(otherPath).SyncFromRunner(otherRunner);

            CodingAgentSessionSwitchPromptState? capturedPromptState = null;
            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: currentTree,
                resumeSelector: (_, _) => Task.FromResult(new CodingAgentResumeSelectionResult(otherPath)),
                sessionSwitchPrompt: (state, _) =>
                {
                    capturedPromptState = state;
                    return Task.FromResult(new CodingAgentTreeNavigationDecision(
                        Cancelled: false,
                        Summarize: true,
                        CustomInstructions: "focus resume switch",
                        ReplaceInstructions: false,
                        Label: "parking-lot"));
                });

            var result = await router.TryHandleAsync("/resume");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("previous branch summary 4 entries, tokens ~77", result.Message, StringComparison.Ordinal);
            Assert.Equal(otherPath, currentTree.Path);
            Assert.Equal("other session", runner.SessionName);
            Assert.Equal(2, runner.Messages.Count);
            Assert.Equal("other task", ReadText(runner.Messages[0]));
            Assert.Equal("other answer", ReadText(runner.Messages[1]));
            Assert.NotNull(capturedPromptState);
            Assert.Equal(CodingAgentTreeNavigationReason.ResumeSession, capturedPromptState!.Reason);
            Assert.Equal(Path.GetFullPath(otherPath), capturedPromptState.TargetSessionPath);
            Assert.Equal(4, capturedPromptState.EntryCount);

            var summarizedCurrent = new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(currentPath));
            var summarizedSnapshot = summarizedCurrent.LoadSnapshot();
            Assert.Single(summarizedSnapshot.Messages);
            Assert.Contains("resume summary body", ReadText(summarizedSnapshot.Messages[0]), StringComparison.Ordinal);
            Assert.Equal("current session", summarizedSnapshot.Name);
            Assert.Equal("openai", summarizedSnapshot.Provider);
            Assert.Equal("gpt-5.4", summarizedSnapshot.Model);
            var branchSummary = ReadBranchSummaryEntry(currentPath);
            var summaryEntryId = branchSummary.GetProperty("id").GetString();
            Assert.Equal("parking-lot", summarizedCurrent.GetLabel(summaryEntryId!));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ResumeCommandWithoutArgs_CanUseSessionSwitchHookDecisionWithoutPrompt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-resume-hook-summary-" + Guid.NewGuid().ToString("N"));
        var currentPath = Path.Combine(directory, "current.jsonl");
        var otherPath = Path.Combine(directory, "coding-agent-sessions", "other.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            SessionName = "current session"
        };
        runner.MutableMessages.Add(new UserMessage("current task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("current answer")]));
        runner.BranchSummaryHandler = (messages, instructions, replaceInstructions, _) =>
        {
            Assert.Equal("hook summary", instructions);
            Assert.True(replaceInstructions);
            Assert.Equal(2, messages.Count);
            return Task.FromResult(new CodingAgentBranchSummaryResult("hooked summary body", messages.Count, 19));
        };

        try
        {
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);

            var currentTree = CodingAgentTreeSessionController.OpenOrCreate(currentPath);
            currentTree.SyncFromRunner(runner);

            var otherRunner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
            {
                SessionName = "other session"
            };
            otherRunner.MutableMessages.Add(new UserMessage("other task"));
            CodingAgentTreeSessionController.OpenOrCreate(otherPath).SyncFromRunner(otherRunner);

            CodingAgentSessionSwitchHookState? capturedHookState = null;
            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: currentTree,
                resumeSelector: (_, _) => Task.FromResult(new CodingAgentResumeSelectionResult(otherPath)),
                sessionSwitchHook: (state, _) =>
                {
                    capturedHookState = state;
                    return Task.FromResult<CodingAgentSessionSwitchHookResult?>(
                        CodingAgentSessionSwitchHookResult.Continue(
                            CodingAgentTreeNavigationDecision.SummarizeWith("hook summary", replaceInstructions: true)));
                });

            var result = await router.TryHandleAsync("/resume");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("previous branch summary 2 entries, tokens ~19", result.Message, StringComparison.Ordinal);
            Assert.NotNull(capturedHookState);
            Assert.Equal(CodingAgentTreeNavigationReason.ResumeSession, capturedHookState!.Reason);
            Assert.Equal(Path.GetFullPath(currentPath), capturedHookState.CurrentSessionPath);
            Assert.Equal("current session", capturedHookState.CurrentSessionName);
            Assert.Equal("openai", capturedHookState.CurrentProvider);
            Assert.Equal("gpt-5.4", capturedHookState.CurrentModel);
            Assert.Equal(Path.GetFullPath(otherPath), capturedHookState.TargetSessionPath);
            Assert.NotNull(capturedHookState.TargetSession);
            Assert.Equal("other session", capturedHookState.TargetSession!.Name);
            Assert.Equal("openai", capturedHookState.TargetSession.Provider);
            Assert.Equal("gpt-5.4", capturedHookState.TargetSession.Model);
            Assert.Equal(1, capturedHookState.TargetSession.MessageCount);
            Assert.Equal(2, capturedHookState.EntryCount);
            Assert.Equal(otherPath, currentTree.Path);
            Assert.Equal("other session", runner.SessionName);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ResumeCommandWithoutArgs_WhenSwitchSummaryPromptCancelled_KeepsCurrentSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-resume-selector-summary-cancel-" + Guid.NewGuid().ToString("N"));
        var currentPath = Path.Combine(directory, "current.jsonl");
        var otherPath = Path.Combine(directory, "coding-agent-sessions", "other.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            SessionName = "current session"
        };
        runner.MutableMessages.Add(new UserMessage("current task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("current answer")]));

        try
        {
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);

            var currentTree = CodingAgentTreeSessionController.OpenOrCreate(currentPath);
            currentTree.SyncFromRunner(runner);

            var otherRunner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
            {
                SessionName = "other session"
            };
            otherRunner.MutableMessages.Add(new UserMessage("other task"));
            CodingAgentTreeSessionController.OpenOrCreate(otherPath).SyncFromRunner(otherRunner);

            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: currentTree,
                resumeSelector: (_, _) => Task.FromResult(new CodingAgentResumeSelectionResult(otherPath)),
                sessionSwitchPrompt: (_, _) => Task.FromResult(CodingAgentTreeNavigationDecision.CancelledDecision));

            var result = await router.TryHandleAsync("/resume");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("resume switch cancelled", result.Message);
            Assert.Equal(currentPath, currentTree.Path);
            Assert.Equal("current session", runner.SessionName);
            Assert.Equal(2, runner.Messages.Count);
            Assert.Equal("current task", ReadText(runner.Messages[0]));
            Assert.Equal("current answer", ReadText(runner.Messages[1]));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_CloneCommandWithExtraArgs_ReturnsUsage()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/clone now");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("usage: /clone", result.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_SessionCommandWithExtraArgs_ReturnsUsage()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/session extra");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("usage: /session", result.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_RetryCommand_ShowsUpdatesPersistsAndNotifiesRuntime()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-retry-settings-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var changed = new List<CodingAgentRetryOptions>();
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            retryOptions: CodingAgentRetryOptions.Disabled,
            retryOptionsChanged: changed.Add);

        try
        {
            settingsStore.Save(new CodingAgentSettingsSnapshot("openai", "gpt-5.4", "no-tools"));

            var current = await router.TryHandleAsync("/retry");
            var configured = await router.TryHandleAsync("/retry 4 125");
            var afterConfigure = await router.TryHandleAsync("/retry current");
            var disabled = await router.TryHandleAsync("/retry off");
            var defaulted = await router.TryHandleAsync("/retry default");

            Assert.True(current.Handled);
            Assert.False(current.IsError);
            Assert.Equal("retry: off", current.Message);

            Assert.False(configured.IsError);
            Assert.Equal("retry: enabled 4 attempts, base 125ms", configured.Message);
            Assert.False(afterConfigure.IsError);
            Assert.Equal("retry: enabled 4 attempts, base 125ms", afterConfigure.Message);
            Assert.False(disabled.IsError);
            Assert.Equal("retry: off", disabled.Message);
            Assert.False(defaulted.IsError);
            Assert.StartsWith("retry: ", defaulted.Message, StringComparison.Ordinal);

            Assert.Equal(3, changed.Count);
            Assert.Equal(new CodingAgentRetryOptions(4, 125), changed[0]);
            Assert.Equal(CodingAgentRetryOptions.Disabled, changed[1]);
            Assert.Equal(changed[2], CodingAgentRetryOptions.FromSettingsOrEnvironment(settingsStore.Load()));

            var settings = settingsStore.Load();
            Assert.Equal("openai", settings.DefaultProvider);
            Assert.Equal("gpt-5.4", settings.DefaultModel);
            Assert.Equal("no-tools", settings.TreeFilterMode);
            Assert.Null(settings.RetryMaxAttempts);
            Assert.Null(settings.RetryBaseDelayMilliseconds);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_RetryCommand_InvalidArgumentsReturnUsage()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var negativeAttempts = await router.TryHandleAsync("/retry -1");
        var invalidAttempts = await router.TryHandleAsync("/retry nope");
        var invalidDelay = await router.TryHandleAsync("/retry 2 nope");
        var extraArgs = await router.TryHandleAsync("/retry 2 100 extra");

        Assert.All(
            [negativeAttempts, invalidAttempts, invalidDelay, extraArgs],
            result =>
            {
                Assert.True(result.Handled);
                Assert.True(result.IsError);
                Assert.Equal("usage: /retry [current|default|off|<max attempts> [base delay ms]]", result.Message);
        });
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_ThinkingCommand_ShowsDefaultOffSetsLevelAndPersistsSettings()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-thinking-settings-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            settingsStore.Save(new CodingAgentSettingsSnapshot("openai", "gpt-5.4", "no-tools"));

            var current = await router.TryHandleAsync("/thinking");
            var configured = await router.TryHandleAsync("/thinking high");

            Assert.True(current.Handled);
            Assert.False(current.IsError);
            Assert.Equal("thinking: off", current.Message);

            Assert.False(configured.IsError);
            Assert.Equal("thinking: high", configured.Message);
            Assert.Equal(ThinkingLevel.High, runner.ThinkingLevel);

            var settings = settingsStore.Load();
            Assert.Equal("openai", settings.DefaultProvider);
            Assert.Equal("gpt-5.4", settings.DefaultModel);
            Assert.Equal("no-tools", settings.TreeFilterMode);
            Assert.Equal("high", settings.DefaultThinkingLevel);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ThinkingCommand_CyclesAndClearsSettingsAtOff()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-thinking-cycle-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var current = await router.TryHandleAsync("/thinking current");
            var low = await router.TryHandleAsync("/thinking cycle");
            var medium = await router.TryHandleAsync("/thinking cycle");
            var high = await router.TryHandleAsync("/thinking cycle");
            var xhigh = await router.TryHandleAsync("/thinking cycle");
            var off = await router.TryHandleAsync("/thinking cycle");

            Assert.Equal("thinking: off", current.Message);
            Assert.Equal("thinking: low", low.Message);
            Assert.Equal("thinking: medium", medium.Message);
            Assert.Equal("thinking: high", high.Message);
            Assert.Equal("thinking: xhigh", xhigh.Message);
            Assert.Equal("thinking: off", off.Message);
            Assert.Null(runner.ThinkingLevel);
            Assert.Null(settingsStore.Load().DefaultThinkingLevel);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ThinkingCommand_ClampsToCurrentModelCapabilities()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-thinking-clamp-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SelectModel("google", "gemini-2.5-pro");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var xhigh = await router.TryHandleAsync("/thinking xhigh");

            Assert.True(xhigh.Handled);
            Assert.False(xhigh.IsError);
            Assert.Equal("thinking: high", xhigh.Message);
            Assert.Equal(ThinkingLevel.High, runner.ThinkingLevel);
            Assert.Equal("high", settingsStore.Load().DefaultThinkingLevel);

            runner.SetModelReasoning("google", "gemini-2.5-pro", false);
            var nonReasoning = await router.TryHandleAsync("/thinking high");

            Assert.True(nonReasoning.Handled);
            Assert.False(nonReasoning.IsError);
            Assert.Equal("thinking: off", nonReasoning.Message);
            Assert.Null(runner.ThinkingLevel);
            Assert.Null(settingsStore.Load().DefaultThinkingLevel);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ThinkingCommand_CycleSkipsXhighWhenUnsupported()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-thinking-cycle-clamp-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            ThinkingLevel = ThinkingLevel.Medium
        };
        runner.SelectModel("google", "gemini-2.5-pro");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var high = await router.TryHandleAsync("/thinking cycle");

            Assert.False(high.IsError);
            Assert.Equal("thinking: high", high.Message);
            Assert.Equal(ThinkingLevel.High, runner.ThinkingLevel);
            Assert.Equal("high", settingsStore.Load().DefaultThinkingLevel);

            var off = await router.TryHandleAsync("/thinking cycle");

            Assert.False(off.IsError);
            Assert.Equal("thinking: off", off.Message);
            Assert.Null(runner.ThinkingLevel);
            Assert.Null(settingsStore.Load().DefaultThinkingLevel);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ThinkingCommand_OffClearsRuntimeAndSettings()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-thinking-off-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            ThinkingLevel = ThinkingLevel.ExtraHigh
        };
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(null, null, DefaultThinkingLevel: "xhigh"));
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var result = await router.TryHandleAsync("/thinking off");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("thinking: off", result.Message);
            Assert.Null(runner.ThinkingLevel);
            Assert.Null(settingsStore.Load().DefaultThinkingLevel);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ThinkingCommand_SelectUsesSelectorAndPersistsSettings()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-thinking-select-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            ThinkingLevel = ThinkingLevel.Low
        };
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot("openai", "gpt-5.4", "no-tools", DefaultThinkingLevel: "low"));
        var selectorStates = new List<CodingAgentThinkingSelectorState>();
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            thinkingSelector: (state, _) =>
            {
                selectorStates.Add(state);
                return Task.FromResult<string?>("high");
            });

        try
        {
            var result = await router.TryHandleAsync("/thinking select");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("thinking: high", result.Message);
            Assert.Equal(ThinkingLevel.High, runner.ThinkingLevel);
            Assert.Single(selectorStates);
            Assert.Equal(ThinkingLevel.Low, selectorStates[0].CurrentLevel);
            Assert.Equal(CodingAgentThinkingSelector.DefaultLevels, selectorStates[0].AvailableLevels);

            var settings = settingsStore.Load();
            Assert.Equal("openai", settings.DefaultProvider);
            Assert.Equal("gpt-5.4", settings.DefaultModel);
            Assert.Equal("no-tools", settings.TreeFilterMode);
            Assert.Equal("high", settings.DefaultThinkingLevel);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ThinkingCommand_SelectUsesModelAvailableLevelsAndClampsSelection()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-thinking-select-clamp-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SelectModel("google", "gemini-2.5-pro");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot("google", "gemini-2.5-pro"));
        var selectorStates = new List<CodingAgentThinkingSelectorState>();
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            thinkingSelector: (state, _) =>
            {
                selectorStates.Add(state);
                return Task.FromResult<string?>("xhigh");
            });

        try
        {
            var result = await router.TryHandleAsync("/thinking select");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("thinking: high", result.Message);
            Assert.Equal(ThinkingLevel.High, runner.ThinkingLevel);
            Assert.Single(selectorStates);
            Assert.Equal(["off", "minimal", "low", "medium", "high"], selectorStates[0].AvailableLevels);
            Assert.Equal("high", settingsStore.Load().DefaultThinkingLevel);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ThinkingCommand_SelectOffClearsRuntimeAndSettings()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-thinking-select-off-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            ThinkingLevel = ThinkingLevel.ExtraHigh
        };
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(null, null, DefaultThinkingLevel: "xhigh"));
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            thinkingSelector: (_, _) => Task.FromResult<string?>("off"));

        try
        {
            var result = await router.TryHandleAsync("/thinking select");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("thinking: off", result.Message);
            Assert.Null(runner.ThinkingLevel);
            Assert.Null(settingsStore.Load().DefaultThinkingLevel);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ThinkingCommand_SelectUnavailableCancelAndInvalidDoNotChangeSettings()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-thinking-select-errors-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            ThinkingLevel = ThinkingLevel.Low
        };
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(null, null, DefaultThinkingLevel: "low"));

        try
        {
            var unavailableRouter = new CodingAgentCommandRouter(runner, settingsStore);
            var unavailable = await unavailableRouter.TryHandleAsync("/thinking select");

            var cancelRouter = new CodingAgentCommandRouter(
                runner,
                settingsStore,
                thinkingSelector: (_, _) => Task.FromResult<string?>(null));
            var cancelled = await cancelRouter.TryHandleAsync("/thinking select");

            var invalidRouter = new CodingAgentCommandRouter(
                runner,
                settingsStore,
                thinkingSelector: (_, _) => Task.FromResult<string?>("turbo"));
            var invalid = await invalidRouter.TryHandleAsync("/thinking select");

            Assert.True(unavailable.Handled);
            Assert.True(unavailable.IsError);
            Assert.Equal("thinking selector is not available in this session", unavailable.Message);

            Assert.True(cancelled.Handled);
            Assert.False(cancelled.IsError);
            Assert.Equal("thinking selection cancelled", cancelled.Message);

            Assert.True(invalid.Handled);
            Assert.True(invalid.IsError);
            Assert.Equal("thinking selector returned unsupported level 'turbo'", invalid.Message);

            Assert.Equal(ThinkingLevel.Low, runner.ThinkingLevel);
            Assert.Equal("low", settingsStore.Load().DefaultThinkingLevel);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ThinkingCommand_InvalidArgumentsReturnUsage()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var invalidLevel = await router.TryHandleAsync("/thinking turbo");
        var extraArgs = await router.TryHandleAsync("/thinking high now");

        Assert.All(
            [invalidLevel, extraArgs],
            result =>
            {
                Assert.True(result.Handled);
                Assert.True(result.IsError);
                Assert.Equal("usage: /thinking [current|select|cycle|off|minimal|low|medium|high|xhigh]", result.Message);
            });
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_UnknownCommand_ReturnsErrorWithoutInvokingRunner()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/wat");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("unknown command '/wat'", result.Message);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_ExportHtmlCommand_RedactsSecretsByDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-export-redact-{Guid.NewGuid():N}");
        var htmlPath = Path.Combine(directory, "session.html");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "redaction export";
        runner.MutableMessages.Add(new AssistantMessage(
            [
                new TextContent(
                    """
                    Set AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE
                    Auth: Bearer abcdef1234567890abcdef1234567890
                    Anthropic: sk-ant-api03-EXAMPLE-SECRET-TOKEN-9999
                    keep this line intact
                    """)
            ]));
        var router = new CodingAgentCommandRouter(runner);

        try
        {
            Directory.CreateDirectory(directory);

            var result = await router.TryHandleAsync($"/export {htmlPath}");
            Assert.True(result.Handled);
            Assert.False(result.IsError);

            var html = File.ReadAllText(htmlPath);
            Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer abcdef1234567890", html, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-ant-api03-EXAMPLE-SECRET-TOKEN", html, StringComparison.Ordinal);
            Assert.Contains("[redacted]", html, StringComparison.Ordinal);
            Assert.Contains("keep this line intact", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_FindCommand_LocatesSubstringInMessages()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("Tell me about pi-mono port plan"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("the plan covers AWS credential chain")]));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("unrelated thoughts about kittens")]));
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/find AWS");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Contains("Matches for \"AWS\"", result.Message, StringComparison.Ordinal);
        Assert.Contains("assistant", result.Message, StringComparison.Ordinal);
        Assert.Contains("AWS credential chain", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("kittens", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_FindCommand_IsCaseInsensitive()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("Note about Bedrock"));
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/find bedrock");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Contains("Bedrock", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_FindCommand_ReportsNoMatches()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("hello"));
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/find missing");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Contains("No matches", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_FindCommand_RejectsMissingPattern()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/find");

        Assert.True(result.IsError);
        Assert.Contains("usage:", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryHandleAsync_ClearCommand_InvokesClearAction()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var invocations = 0;
        var router = new CodingAgentCommandRouter(runner, clearScreenAction: () => invocations++);

        var result = await router.TryHandleAsync("/clear");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public async Task TryHandleAsync_ClearCommand_WithoutActionReturnsError()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/clear");

        Assert.True(result.IsError);
        Assert.Contains("not supported", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_ClearCommand_RejectsExtraArguments()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner, clearScreenAction: () => { });

        var result = await router.TryHandleAsync("/clear extra");

        Assert.True(result.IsError);
        Assert.Contains("usage:", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryHandleAsync_HistoryCommand_ReturnsErrorWhenNoProviderSet()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/history");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Contains("not available", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_HistoryCommand_ListsRecentEntriesNewestFirst()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(
            runner,
            historySnapshotProvider: limit => new[] { "third command", "second command", "first command" }.Take(limit).ToArray());

        var result = await router.TryHandleAsync("/history");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Contains("Recent inputs (3)", result.Message, StringComparison.Ordinal);
        Assert.Contains("[ 1] third command", result.Message, StringComparison.Ordinal);
        Assert.Contains("[ 2] second command", result.Message, StringComparison.Ordinal);
        Assert.Contains("[ 3] first command", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_HistoryCommand_RespectsExplicitCount()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var captured = 0;
        var router = new CodingAgentCommandRouter(
            runner,
            historySnapshotProvider: limit =>
            {
                captured = limit;
                return new[] { "only" };
            });

        var result = await router.TryHandleAsync("/history 5");

        Assert.False(result.IsError);
        Assert.Equal(5, captured);
        Assert.Contains("only", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_HistoryCommand_RejectsInvalidArgument()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(
            runner,
            historySnapshotProvider: _ => Array.Empty<string>());

        var result = await router.TryHandleAsync("/history not-a-number");

        Assert.True(result.IsError);
        Assert.Contains("Usage:", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_HistoryCommand_StatusOnEmpty()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(
            runner,
            historySnapshotProvider: _ => Array.Empty<string>());

        var result = await router.TryHandleAsync("/history");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Contains("empty", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryHandleAsync_ModelCommand_SelectsAndPersistsDefaultModel()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("google");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var result = await router.TryHandleAsync("/model google gemini-2.5-pro");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("model: google/gemini-2.5-pro", result.Message);
            Assert.Empty(runner.Inputs);

            var settings = settingsStore.Load();
            Assert.Equal("google", settings.DefaultProvider);
            Assert.Equal("gemini-2.5-pro", settings.DefaultModel);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ModelSelectCommand_UsesSelectorAndPersistsDefaultModel()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-model-select-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("openai", "google");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        CodingAgentModelSelectorState? capturedState = null;
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            modelSelector: (state, _) =>
            {
                capturedState = state;
                return Task.FromResult<string?>("google/gemini-2.5-pro");
            });

        try
        {
            var result = await router.TryHandleAsync("/model select gemini");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("model: google/gemini-2.5-pro", result.Message);
            Assert.Equal("gemini", capturedState?.InitialFilter);
            Assert.Equal(2, capturedState?.AvailableModels.Count);
            Assert.Null(capturedState?.ScopedModels);
            Assert.Equal("google", runner.Model.Provider);
            Assert.Equal("gemini-2.5-pro", runner.Model.Id);

            var settings = settingsStore.Load();
            Assert.Equal("google", settings.DefaultProvider);
            Assert.Equal("gemini-2.5-pro", settings.DefaultModel);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ModelSelectCommand_FiltersModelsWithoutConfiguredAuth()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-model-auth-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("openai");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            null,
            null,
            EnabledModels: ["openai/gpt-5.4", "google/gemini-2.5-pro"]));
        CodingAgentModelSelectorState? capturedState = null;
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            modelSelector: (state, _) =>
            {
                capturedState = state;
                return Task.FromResult<string?>("openai/gpt-5.4");
            });

        try
        {
            var result = await router.TryHandleAsync("/model select");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("model: openai/gpt-5.4", result.Message);
            Assert.NotNull(capturedState);
            var state = capturedState!;
            Assert.Equal(["openai/gpt-5.4"], state.AvailableModels.Select(model => $"{model.Provider}/{model.Id}").ToArray());
            Assert.NotNull(state.ScopedModels);
            Assert.Equal(["openai/gpt-5.4"], state.ScopedModels.Select(model => $"{model.Provider}/{model.Id}").ToArray());
            Assert.Equal("openai", runner.Model.Provider);
            Assert.Equal("gpt-5.4", runner.Model.Id);

            var settings = settingsStore.Load();
            Assert.Equal("openai", settings.DefaultProvider);
            Assert.Equal("gpt-5.4", settings.DefaultModel);
            Assert.Equal(["openai/gpt-5.4", "google/gemini-2.5-pro"], settings.EnabledModels);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ModelSelectCommand_RejectsSelectorModelWithoutConfiguredAuth()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-model-auth-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("openai");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            modelSelector: (_, _) => Task.FromResult<string?>("google/gemini-2.5-pro"));

        try
        {
            var result = await router.TryHandleAsync("/model select");

            Assert.True(result.Handled);
            Assert.True(result.IsError);
            Assert.Equal("model 'google/gemini-2.5-pro' does not have configured auth", result.Message);
            Assert.Equal("openai", runner.Model.Provider);
            Assert.Equal("gpt-5.4", runner.Model.Id);
            Assert.Null(settingsStore.Load().DefaultProvider);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_BareModelCommand_UsesSelectorWhenAvailable()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-model-select-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("openai", "google");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            modelSelector: (_, _) => Task.FromResult<string?>("google/gemini-2.5-pro"));

        try
        {
            var result = await router.TryHandleAsync("/model");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("model: google/gemini-2.5-pro", result.Message);
            Assert.Equal("google", runner.Model.Provider);
            Assert.Equal("gemini-2.5-pro", runner.Model.Id);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_BareModelCommand_WithoutSelectorShowsCurrentModel()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/model");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal("model: openai/gpt-5.4", result.Message);
        Assert.Equal("openai", runner.Model.Provider);
        Assert.Equal("gpt-5.4", runner.Model.Id);
    }

    [Fact]
    public async Task TryHandleAsync_ModelSelectCommand_WithoutSelectorReturnsError()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/model select");

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("model selector is not available in this session", result.Message);
        Assert.Equal("openai", runner.Model.Provider);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task TryHandleAsync_ModelSelectCommand_CancelDoesNotChangeModel()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-model-select-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("openai");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            modelSelector: (_, _) => Task.FromResult<string?>(null));

        try
        {
            var result = await router.TryHandleAsync("/model select");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("model selection cancelled", result.Message);
            Assert.Equal("openai", runner.Model.Provider);
            Assert.Null(settingsStore.Load().DefaultProvider);
            Assert.Empty(runner.Inputs);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ModelCommand_RejectsModelWithoutConfiguredAuth()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-model-auth-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("openai");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var result = await router.TryHandleAsync("/model google gemini-2.5-pro");

            Assert.True(result.Handled);
            Assert.True(result.IsError);
            Assert.Equal("model 'google/gemini-2.5-pro' does not have configured auth", result.Message);
            Assert.Equal("openai", runner.Model.Provider);
            Assert.Equal("gpt-5.4", runner.Model.Id);
            Assert.Null(settingsStore.Load().DefaultProvider);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_ProviderCommand_RejectsProviderWithoutConfiguredAuth()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-provider-auth-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("openai");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var result = await router.TryHandleAsync("/provider google");

            Assert.True(result.Handled);
            Assert.True(result.IsError);
            Assert.Equal("provider 'google' does not have configured auth", result.Message);
            Assert.Equal("openai", runner.Model.Provider);
            Assert.Equal("gpt-5.4", runner.Model.Id);
            Assert.Null(settingsStore.Load().DefaultProvider);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public void CycleModel_UsesScopedModelsAndPersistsDefaultModel()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-cycle-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("openai", "google");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            EnabledModels: ["openai/gpt-5.4", "google/gemini-2.5-pro"]));
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var result = router.CycleModel();

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("model: google/gemini-2.5-pro (scoped)", result.Message);
            Assert.Equal("google", runner.Model.Provider);
            Assert.Equal("gemini-2.5-pro", runner.Model.Id);

            var settings = settingsStore.Load();
            Assert.Equal("google", settings.DefaultProvider);
            Assert.Equal("gemini-2.5-pro", settings.DefaultModel);
            Assert.Equal(["openai/gpt-5.4", "google/gemini-2.5-pro"], settings.EnabledModels);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public void CycleModel_AppliesScopedModelThinkingLevelOverride()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-cycle-thinking-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            ThinkingLevel = ThinkingLevel.Low
        };
        runner.ConfigureAuth("openai", "google");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            DefaultThinkingLevel: "low",
            EnabledModels: ["openai/gpt-5.4", "google/gemini-2.5-pro:high"]));
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var result = router.CycleModel();

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("model: google/gemini-2.5-pro (scoped, thinking: high)", result.Message);
            Assert.Equal("google", runner.Model.Provider);
            Assert.Equal("gemini-2.5-pro", runner.Model.Id);
            Assert.Equal(ThinkingLevel.High, runner.ThinkingLevel);

            var settings = settingsStore.Load();
            Assert.Equal("google", settings.DefaultProvider);
            Assert.Equal("gemini-2.5-pro", settings.DefaultModel);
            Assert.Equal("high", settings.DefaultThinkingLevel);
            Assert.Equal(["openai/gpt-5.4", "google/gemini-2.5-pro:high"], settings.EnabledModels);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public void CycleModel_ClampsScopedThinkingOverrideToTargetModel()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-cycle-thinking-clamp-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            ThinkingLevel = ThinkingLevel.Low
        };
        runner.ConfigureAuth("openai", "google");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            DefaultThinkingLevel: "low",
            EnabledModels: ["openai/gpt-5.4", "google/gemini-2.5-pro:xhigh"]));
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var result = router.CycleModel();

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("model: google/gemini-2.5-pro (scoped, thinking: high)", result.Message);
            Assert.Equal("google", runner.Model.Provider);
            Assert.Equal("gemini-2.5-pro", runner.Model.Id);
            Assert.Equal(ThinkingLevel.High, runner.ThinkingLevel);

            var settings = settingsStore.Load();
            Assert.Equal("google", settings.DefaultProvider);
            Assert.Equal("gemini-2.5-pro", settings.DefaultModel);
            Assert.Equal("high", settings.DefaultThinkingLevel);
            Assert.Equal(["openai/gpt-5.4", "google/gemini-2.5-pro:xhigh"], settings.EnabledModels);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public void CycleModel_ClampsCurrentThinkingWhenTargetModelDoesNotReason()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-cycle-thinking-off-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            ThinkingLevel = ThinkingLevel.High
        };
        runner.SetModelReasoning("google", "gemini-2.5-pro", false);
        runner.ConfigureAuth("openai", "google");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            DefaultThinkingLevel: "high",
            EnabledModels: ["openai/gpt-5.4", "google/gemini-2.5-pro"]));
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var result = router.CycleModel();

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("model: google/gemini-2.5-pro (scoped)", result.Message);
            Assert.Equal("google", runner.Model.Provider);
            Assert.Equal("gemini-2.5-pro", runner.Model.Id);
            Assert.Null(runner.ThinkingLevel);

            var settings = settingsStore.Load();
            Assert.Equal("google", settings.DefaultProvider);
            Assert.Equal("gemini-2.5-pro", settings.DefaultModel);
            Assert.Null(settings.DefaultThinkingLevel);
            Assert.Equal(["openai/gpt-5.4", "google/gemini-2.5-pro"], settings.EnabledModels);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public void CycleModel_SkipsScopedModelsWithoutConfiguredAuth()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-cycle-auth-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("openai");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            null,
            null,
            EnabledModels: ["openai/gpt-5.4", "google/gemini-2.5-pro"]));
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var result = router.CycleModel();

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("Only one model in scope", result.Message);
            Assert.Equal("openai", runner.Model.Provider);
            Assert.Equal("gpt-5.4", runner.Model.Id);

            var settings = settingsStore.Load();
            Assert.Null(settings.DefaultProvider);
            Assert.Equal(["openai/gpt-5.4", "google/gemini-2.5-pro"], settings.EnabledModels);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public void CycleModel_ReturnsErrorWhenNoModelHasConfiguredAuth()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner);

        var result = router.CycleModel();

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("No models with configured auth are available. Use /login or configure provider credentials.", result.Message);
        Assert.Equal("openai", runner.Model.Provider);
        Assert.Equal("gpt-5.4", runner.Model.Id);
    }

    [Fact]
    public void CycleModel_BackwardWrapsAcrossAllAvailableModels()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-cycle-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("openai", "google");
        runner.SelectModel("google", "gemini-2.5-pro");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var result = router.CycleModel("backward");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("model: openai/gpt-5.4", result.Message);
            Assert.Equal("openai", runner.Model.Provider);
            Assert.Equal("gpt-5.4", runner.Model.Id);

            var settings = settingsStore.Load();
            Assert.Equal("openai", settings.DefaultProvider);
            Assert.Equal("gpt-5.4", settings.DefaultModel);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public void CycleModel_ReturnsScopedStatusWhenOnlyOneCandidateIsAvailable()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-router-cycle-{Guid.NewGuid():N}.json");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("openai");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            EnabledModels: ["openai/gpt-5.4"]));
        var router = new CodingAgentCommandRouter(runner, settingsStore);

        try
        {
            var result = router.CycleModel();

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("Only one model in scope", result.Message);
            Assert.Equal("openai", runner.Model.Provider);
            Assert.Equal("gpt-5.4", runner.Model.Id);

            var settings = settingsStore.Load();
            Assert.Equal("openai", settings.DefaultProvider);
            Assert.Equal("gpt-5.4", settings.DefaultModel);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_CompactCommand_UsesOptionalInstructions()
    {
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            CompactHandler = (_, _) => Task.FromResult(new CodingAgentCompactionResult("summary", 6, 1))
        };
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/compact keep decisions and blockers");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal("compacted session: 6 -> 1 messages", result.Message);
        Assert.Equal("keep decisions and blockers", runner.LastCompactInstructions);
    }

    [Fact]
    public async Task TryHandleAsync_CompactCommand_AppendsTreeCompactionEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("before"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("answer")]));
        runner.CompactHandler = (_, _) =>
        {
            runner.MutableMessages.Clear();
            runner.MutableMessages.Add(CodingAgentCompactionMessages.CreateSummaryMessage("summary"));
            return Task.FromResult(new CodingAgentCompactionResult("summary", 2, 1, 42));
        };

        try
        {
            var tree = CodingAgentTreeSessionController.OpenOrCreate(
                treePath,
                new CodingAgentCompactionRetentionOptions(20_000, 4));
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var result = await router.TryHandleAsync("/compact");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("compacted session: 2 -> 1 messages", result.Message);

            var jsonl = File.ReadAllText(treePath);
            Assert.Contains("\"type\":\"message\"", jsonl, StringComparison.Ordinal);
            Assert.Contains("\"type\":\"compaction\"", jsonl, StringComparison.Ordinal);
            Assert.Contains("\"summary\":\"summary\"", jsonl, StringComparison.Ordinal);
            Assert.Contains("\"firstKeptEntryId\":\"\"", jsonl, StringComparison.Ordinal);
            Assert.Contains("\"tokensBefore\":42", jsonl, StringComparison.Ordinal);

            var snapshot = tree.LoadSnapshot();
            var summary = Assert.Single(snapshot.Messages);
            var summaryText = Assert.IsType<TextContent>(Assert.IsType<UserMessage>(summary).Content[0]).Text;
            Assert.Contains("summary", summaryText, StringComparison.Ordinal);
            Assert.Contains("compaction 42 tokens summary", tree.FormatTree(), StringComparison.Ordinal);

            runner.MutableMessages.Add(new UserMessage("after"));
            tree.SyncFromRunner(runner);

            var resumed = tree.LoadSnapshot();
            Assert.Equal(2, resumed.Messages.Count);
            var after = Assert.IsType<UserMessage>(resumed.Messages[1]);
            Assert.Equal("after", Assert.IsType<TextContent>(after.Content[0]).Text);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_CompactCommand_RetainsRecentTreeMessages()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-retain-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var exportPath = Path.Combine(directory, "export.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("one"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("two")]));
        runner.MutableMessages.Add(new UserMessage("three"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("four")]));
        runner.MutableMessages.Add(new UserMessage("five"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("six")]));
        runner.CompactHandler = (_, _) =>
        {
            runner.MutableMessages.Clear();
            runner.MutableMessages.Add(CodingAgentCompactionMessages.CreateSummaryMessage("summary"));
            return Task.FromResult(new CodingAgentCompactionResult("summary", 6, 1, 120));
        };

        try
        {
            var tree = CodingAgentTreeSessionController.OpenOrCreate(
                treePath,
                new CodingAgentCompactionRetentionOptions(20_000, 4));
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var result = await router.TryHandleAsync("/compact");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("compacted session: 6 -> 5 messages", result.Message);

            var firstKeptEntryId = ReadCompactionFirstKeptEntryId(treePath);
            var retainedStartEntryId = ReadMessageEntryId(treePath, "user", "three");
            Assert.Equal(retainedStartEntryId, firstKeptEntryId);

            var snapshot = tree.LoadSnapshot();
            Assert.Equal(5, snapshot.Messages.Count);
            Assert.Contains("summary", ReadText(snapshot.Messages[0]), StringComparison.Ordinal);
            Assert.Equal("three", ReadText(snapshot.Messages[1]));
            Assert.Equal("four", ReadText(snapshot.Messages[2]));
            Assert.Equal("five", ReadText(snapshot.Messages[3]));
            Assert.Equal("six", ReadText(snapshot.Messages[4]));
            Assert.Equal(5, runner.Messages.Count);

            tree.ExportCurrentBranch(exportPath);
            var exportedSnapshot = new CodingAgentTreeSessionStore(exportPath).LoadCurrentBranchSnapshot();
            Assert.Equal(5, exportedSnapshot.Messages.Count);
            Assert.Equal("three", ReadText(exportedSnapshot.Messages[1]));

            var exportedFirstKeptEntryId = ReadCompactionFirstKeptEntryId(exportPath);
            Assert.NotEqual(firstKeptEntryId, exportedFirstKeptEntryId);
            Assert.Equal(ReadMessageEntryId(exportPath, "user", "three"), exportedFirstKeptEntryId);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_CompactCommand_UsesTokenRetentionCutPointBeforeMessageFallback()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-retain-tokens-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("one-abcdefghijklmnop"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("two-abcdefghijklmnop")]));
        runner.MutableMessages.Add(new UserMessage("three-abcdefghijklmnop"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("four-abcdefghijklmnop")]));
        runner.MutableMessages.Add(new UserMessage("five-abcdefghijklmnop"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("six-abcdefghijklmnop")]));
        runner.CompactHandler = (_, _) =>
        {
            runner.MutableMessages.Clear();
            runner.MutableMessages.Add(CodingAgentCompactionMessages.CreateSummaryMessage("summary"));
            return Task.FromResult(new CodingAgentCompactionResult("summary", 6, 1, 120));
        };

        try
        {
            var tree = CodingAgentTreeSessionController.OpenOrCreate(
                treePath,
                new CodingAgentCompactionRetentionOptions(8, 10));
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var result = await router.TryHandleAsync("/compact");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("compacted session: 6 -> 3 messages", result.Message);

            var firstKeptEntryId = ReadCompactionFirstKeptEntryId(treePath);
            var retainedStartEntryId = ReadMessageEntryId(treePath, "user", "five-");
            Assert.Equal(retainedStartEntryId, firstKeptEntryId);

            var snapshot = tree.LoadSnapshot();
            Assert.Equal(3, snapshot.Messages.Count);
            Assert.Contains("summary", ReadText(snapshot.Messages[0]), StringComparison.Ordinal);
            Assert.Equal("five-abcdefghijklmnop", ReadText(snapshot.Messages[1]));
            Assert.Equal("six-abcdefghijklmnop", ReadText(snapshot.Messages[2]));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_CompactCommand_AddsSplitTurnPrefixSummaryWhenRetentionStartsMidTurn()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-split-turn-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("implement split turn support"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("early analysis and plan")]));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("retained suffix work")]));
        runner.CompactHandler = (_, _) =>
        {
            runner.MutableMessages.Clear();
            runner.MutableMessages.Add(CodingAgentCompactionMessages.CreateSummaryMessage("history summary"));
            return Task.FromResult(new CodingAgentCompactionResult("history summary", 3, 1, 90));
        };

        try
        {
            var tree = CodingAgentTreeSessionController.OpenOrCreate(
                treePath,
                new CodingAgentCompactionRetentionOptions(0, 1));
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var result = await router.TryHandleAsync("/compact");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("compacted session: 3 -> 2 messages", result.Message);

            var jsonl = File.ReadAllText(treePath);
            Assert.Contains("\"isSplitTurn\":true", jsonl, StringComparison.Ordinal);
            Assert.Contains("\"turnPrefixSummary\":", jsonl, StringComparison.Ordinal);
            Assert.Contains("Original Request", jsonl, StringComparison.Ordinal);
            Assert.Contains("implement split turn support", jsonl, StringComparison.Ordinal);
            Assert.Contains("assistant: early analysis and plan", jsonl, StringComparison.Ordinal);

            var retainedStartEntryId = ReadMessageEntryId(treePath, "assistant", "retained suffix work");
            Assert.Equal(retainedStartEntryId, ReadCompactionFirstKeptEntryId(treePath));

            var snapshot = tree.LoadSnapshot();
            Assert.Equal(2, snapshot.Messages.Count);
            var summaryText = ReadText(snapshot.Messages[0]);
            Assert.Contains("history summary", summaryText, StringComparison.Ordinal);
            Assert.Contains("Turn Context (split turn)", summaryText, StringComparison.Ordinal);
            Assert.Contains("## Original Request", summaryText, StringComparison.Ordinal);
            Assert.Contains("implement split turn support", summaryText, StringComparison.Ordinal);
            Assert.Equal("retained suffix work", ReadText(snapshot.Messages[1]));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void TreeSnapshot_WithInvalidFirstKeptEntryId_DoesNotFailRestore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-invalid-kept-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");

        try
        {
            var store = new CodingAgentTreeSessionStore(treePath);
            store.AppendMessages(
                [
                    new UserMessage("before"),
                    new AssistantMessage([new TextContent("old answer")])
                ],
                0);
            store.AppendCompaction("summary", "missing-entry", 90);
            store.AppendMessages([new UserMessage("after")], 0);

            var snapshot = store.LoadCurrentBranchSnapshot();

            Assert.Equal(2, snapshot.Messages.Count);
            Assert.Contains("summary", ReadText(snapshot.Messages[0]), StringComparison.Ordinal);
            Assert.Equal("after", ReadText(snapshot.Messages[1]));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class FakeShareClient : ICodingAgentShareClient
    {
        public string? SharedPath { get; private set; }
        public string? Html { get; private set; }

        public Task<CodingAgentShareResult> ShareAsync(
            string htmlPath,
            CancellationToken cancellationToken = default)
        {
            SharedPath = htmlPath;
            Html = File.ReadAllText(htmlPath);
            return Task.FromResult(new CodingAgentShareResult(
                "https://gist.github.com/user/abc123",
                "abc123",
                "https://pi.dev/session/#abc123"));
        }
    }

    private static string ReadMessageEntryId(string path, string role, string? textContains = null)
    {
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) ||
                !string.Equals(type.GetString(), "message", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("role", out var messageRole) ||
                !string.Equals(messageRole.GetString(), role, StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("id", out var id))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(textContains) && !MessageContains(message, textContains))
            {
                continue;
            }

            return id.GetString() ?? throw new InvalidOperationException("message entry id is empty");
        }

        throw new InvalidOperationException($"message entry with role '{role}' not found");
    }

    private static string ReadCompactionFirstKeptEntryId(string path)
    {
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var type) &&
                string.Equals(type.GetString(), "compaction", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("firstKeptEntryId", out var firstKeptEntryId))
            {
                return firstKeptEntryId.GetString() ?? string.Empty;
            }
        }

        throw new InvalidOperationException("compaction entry not found");
    }

    private static JsonElement ReadBranchSummaryEntry(string path)
    {
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var type) &&
                string.Equals(type.GetString(), "branch_summary", StringComparison.OrdinalIgnoreCase))
            {
                return root.Clone();
            }
        }

        throw new InvalidOperationException("branch summary entry not found");
    }

    private static string ReadText(ChatMessage message)
    {
        IReadOnlyList<ContentBlock> content = message switch
        {
            UserMessage user => user.Content,
            AssistantMessage assistant => assistant.Content,
            ToolResultMessage toolResult => toolResult.Content,
            _ => []
        };
        return string.Join(
            "\n",
            content
                .OfType<TextContent>()
                .Select(static text => text.Text));
    }

    [Fact]
    public async Task TryHandleAsync_TreeInteractiveCommand_InvokesNavigatorAndReturnsSelection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-interactive-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("first task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("ack")]));
        runner.MutableMessages.Add(new UserMessage("follow up"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("done")]));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);

            IReadOnlyList<CodingAgentTreeViewItem>? capturedItems = null;
            Func<IReadOnlyList<CodingAgentTreeViewItem>, string?, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>> navigator =
                (items, _, _) =>
                {
                    capturedItems = items;
                    var selected = items.Single(item => item.DisplayLine.Contains("ack", StringComparison.Ordinal));
                    return Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(selected.EntryId, 0, 1));
                };

            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree, treeNavigator: navigator);
            var result = await router.TryHandleAsync("/tree --interactive");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.NotNull(capturedItems);
            Assert.Contains("navigated tree to", result.Message, StringComparison.Ordinal);
            Assert.Equal("first task", ReadText(runner.Messages[0]));
            Assert.Equal("ack", ReadText(runner.Messages[1]));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_TreeInteractiveCommand_SelectingCurrentLeafIsNoOp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-interactive-current-leaf-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("current leaf request"));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);
            var before = tree.GetSummary();
            string? loadedDraft = null;
            var promptCalls = 0;

            Func<IReadOnlyList<CodingAgentTreeViewItem>, string?, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>> navigator =
                (items, _, _) =>
                {
                    var selected = Assert.Single(items);
                    Assert.True(selected.IsCurrentLeaf);
                    Assert.Equal("user", selected.MessageRole);
                    return Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(selected.EntryId, 0, 1));
                };

            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: tree,
                treeNavigator: navigator,
                inputDraftSetter: draft => loadedDraft = draft,
                treeNavigationPrompt: (_, _) =>
                {
                    promptCalls++;
                    return Task.FromResult(CodingAgentTreeNavigationDecision.NoSummary);
                });
            var result = await router.TryHandleAsync("/tree --interactive");
            var after = tree.GetSummary();

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("Already at this point", result.Message);
            Assert.Equal(0, promptCalls);
            Assert.Null(loadedDraft);
            Assert.Equal(before.EntryCount, after.EntryCount);
            Assert.Equal(before.LeafId, after.LeafId);
            Assert.Equal("current leaf request", ReadText(Assert.Single(runner.Messages)));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_TreeInteractiveCommand_UsesFullFilteredTreeInsteadOfDefaultWindow()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-interactive-full-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        for (var i = 1; i <= 30; i++)
        {
            runner.MutableMessages.Add(new UserMessage($"message {i:00}"));
        }

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);

            IReadOnlyList<CodingAgentTreeViewItem>? capturedItems = null;
            Func<IReadOnlyList<CodingAgentTreeViewItem>, string?, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>> navigator =
                (items, _, _) =>
                {
                    capturedItems = items;
                    return Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(items[^1].EntryId, items.Count - 1, 1));
                };

            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree, treeNavigator: navigator);
            var result = await router.TryHandleAsync("/tree --interactive");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.NotNull(capturedItems);
            Assert.Equal(30, capturedItems!.Count);
            Assert.Equal("Already at this point", result.Message);
            Assert.Equal(30, runner.Messages.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_TreeInteractiveCommand_SelectingUserMessageRewindsAndLoadsDraft()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-interactive-user-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("root request"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("root answer")]));
        runner.MutableMessages.Add(new UserMessage("follow up request"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("follow up answer")]));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);
            string? loadedDraft = null;

            Func<IReadOnlyList<CodingAgentTreeViewItem>, string?, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>> navigator =
                (items, _, _) =>
                {
                    var selected = items.Single(item =>
                        string.Equals(item.MessageRole, "user", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.NavigationDraftText, "follow up request", StringComparison.Ordinal));
                    return Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(selected.EntryId, 0, 1));
                };

            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: tree,
                treeNavigator: navigator,
                inputDraftSetter: draft => loadedDraft = draft);
            var result = await router.TryHandleAsync("/tree --interactive");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("loaded draft", result.Message, StringComparison.Ordinal);
            Assert.Equal("follow up request", loadedDraft);
            Assert.Equal(2, runner.Messages.Count);
            Assert.Equal("root request", ReadText(runner.Messages[0]));
            Assert.Equal("root answer", ReadText(runner.Messages[1]));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_TreeInteractiveCommand_CanSummarizeAbandonedBranchBeforeNavigation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-interactive-summary-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("root task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("root answer")]));
        runner.MutableMessages.Add(new UserMessage("abandoned branch request"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("abandoned branch progress")]));
        runner.BranchSummaryHandler = (messages, instructions, replaceInstructions, _) =>
        {
            Assert.Equal("focus tree switch", instructions);
            Assert.False(replaceInstructions);
            Assert.Equal(2, messages.Count);
            Assert.Equal("abandoned branch request", ReadText(messages[0]));
            Assert.Equal("abandoned branch progress", ReadText(messages[1]));
            return Task.FromResult(new CodingAgentBranchSummaryResult("tree summary body", messages.Count, 77));
        };

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);

            Func<IReadOnlyList<CodingAgentTreeViewItem>, string?, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>> navigator =
                (items, _, _) =>
                {
                    var selected = items.Single(item => item.DisplayLine.Contains("root answer", StringComparison.Ordinal));
                    return Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(selected.EntryId, 0, 1));
                };

            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: tree,
                treeNavigator: navigator,
                treeNavigationPrompt: (_, _) => Task.FromResult(CodingAgentTreeNavigationDecision.SummarizeWith("focus tree switch")));
            var result = await router.TryHandleAsync("/tree --interactive");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("navigated tree to", result.Message, StringComparison.Ordinal);
            Assert.Contains("branch summary 2 entries, tokens ~77", result.Message, StringComparison.Ordinal);
            Assert.Equal("focus tree switch", runner.LastBranchSummaryInstructions);
            Assert.Equal(3, runner.Messages.Count);
            Assert.Equal("root task", ReadText(runner.Messages[0]));
            Assert.Equal("root answer", ReadText(runner.Messages[1]));
            Assert.Contains("tree summary body", ReadText(runner.Messages[2]), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_TreeInteractiveCommand_CanAttachLabelToBranchSummaryEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-interactive-summary-label-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("root task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("root answer")]));
        runner.MutableMessages.Add(new UserMessage("abandoned branch request"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("abandoned branch progress")]));
        runner.BranchSummaryHandler = (messages, instructions, replaceInstructions, _) =>
        {
            Assert.Equal("focus tree switch", instructions);
            Assert.False(replaceInstructions);
            return Task.FromResult(new CodingAgentBranchSummaryResult("tree summary body", messages.Count, 77));
        };

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);

            Func<IReadOnlyList<CodingAgentTreeViewItem>, string?, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>> navigator =
                (items, _, _) =>
                {
                    var selected = items.Single(item => item.DisplayLine.Contains("root answer", StringComparison.Ordinal));
                    return Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(selected.EntryId, 0, 1));
                };

            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: tree,
                treeNavigator: navigator,
                treeNavigationPrompt: (_, _) => Task.FromResult(new CodingAgentTreeNavigationDecision(
                    Cancelled: false,
                    Summarize: true,
                    CustomInstructions: "focus tree switch",
                    Label: "review-checkpoint")));
            var result = await router.TryHandleAsync("/tree --interactive");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("branch summary 2 entries, tokens ~77", result.Message, StringComparison.Ordinal);
            var branchSummary = ReadBranchSummaryEntry(treePath);
            var summaryEntryId = branchSummary.GetProperty("id").GetString();
            Assert.Equal("review-checkpoint", tree.GetLabel(summaryEntryId!));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_TreeInteractiveCommand_WithoutSummaryLabelTargetsSelectedEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-interactive-target-label-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("root task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("root answer")]));
        runner.MutableMessages.Add(new UserMessage("abandoned branch request"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("abandoned branch progress")]));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);
            string selectedEntryId = string.Empty;

            Func<IReadOnlyList<CodingAgentTreeViewItem>, string?, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>> navigator =
                (items, _, _) =>
                {
                    var selected = items.Single(item => item.DisplayLine.Contains("root answer", StringComparison.Ordinal));
                    selectedEntryId = selected.EntryId;
                    return Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(selected.EntryId, 0, 1));
                };

            var promptCalls = 0;
            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: tree,
                treeNavigator: navigator,
                treeNavigationPrompt: (_, _) =>
                {
                    promptCalls++;
                    return Task.FromResult(new CodingAgentTreeNavigationDecision(
                        Cancelled: false,
                        Summarize: false,
                        Label: "return-point"));
                });
            var result = await router.TryHandleAsync("/tree --interactive");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("navigated tree to", result.Message, StringComparison.Ordinal);
            Assert.Equal(1, promptCalls);
            Assert.Equal("return-point", tree.GetLabel(selectedEntryId));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_TreeInteractiveCommand_PassesReplaceInstructionsToBranchSummarizer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-interactive-replace-instructions-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("root task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("root answer")]));
        runner.MutableMessages.Add(new UserMessage("abandoned branch request"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("abandoned branch progress")]));
        runner.BranchSummaryHandler = (messages, instructions, replaceInstructions, _) =>
        {
            Assert.Equal("summarize only blockers", instructions);
            Assert.True(replaceInstructions);
            return Task.FromResult(new CodingAgentBranchSummaryResult("tree summary body", messages.Count, 77));
        };

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);

            Func<IReadOnlyList<CodingAgentTreeViewItem>, string?, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>> navigator =
                (items, _, _) =>
                {
                    var selected = items.Single(item => item.DisplayLine.Contains("root answer", StringComparison.Ordinal));
                    return Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(selected.EntryId, 0, 1));
                };

            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: tree,
                treeNavigator: navigator,
                treeNavigationPrompt: (_, _) => Task.FromResult(new CodingAgentTreeNavigationDecision(
                    Cancelled: false,
                    Summarize: true,
                    CustomInstructions: "summarize only blockers",
                    ReplaceInstructions: true)));

            var result = await router.TryHandleAsync("/tree --interactive");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.True(runner.LastBranchSummaryReplaceInstructions);
            Assert.Contains("branch summary 2 entries, tokens ~77", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_TreeInteractiveCommand_CancelledSummaryPromptReopensTreeAtSameSelection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-interactive-summary-cancel-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("root task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("root answer")]));
        runner.MutableMessages.Add(new UserMessage("abandoned branch request"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("abandoned branch progress")]));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);

            var preferredEntryIds = new List<string?>();
            var selectedEntryId = string.Empty;
            var callCount = 0;
            Func<IReadOnlyList<CodingAgentTreeViewItem>, string?, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>> navigator =
                (items, preferredEntryId, _) =>
                {
                    preferredEntryIds.Add(preferredEntryId);
                    var selected = items.Single(item => item.DisplayLine.Contains("root answer", StringComparison.Ordinal));
                    selectedEntryId = selected.EntryId;
                    callCount++;
                    return Task.FromResult(callCount == 1
                        ? new CodingAgentTreeInteractiveNavigator.Result(selected.EntryId, 0, 1)
                        : new CodingAgentTreeInteractiveNavigator.Result(null, 0, 1));
                };

            var promptCalls = 0;
            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: tree,
                treeNavigator: navigator,
                treeNavigationPrompt: (_, _) =>
                {
                    promptCalls++;
                    return Task.FromResult(CodingAgentTreeNavigationDecision.CancelledDecision);
                });
            var result = await router.TryHandleAsync("/tree --interactive");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("tree navigator cancelled", result.Message);
            Assert.Equal(1, promptCalls);
            Assert.Equal([null, selectedEntryId], preferredEntryIds);
            Assert.Equal(4, runner.Messages.Count);
            Assert.Equal("abandoned branch request", ReadText(runner.Messages[2]));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_TreeInteractiveCommand_LabelEditReopensTreeAtSameEntryAndPersistsLabel()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-interactive-label-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("root task"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("root answer")]));
        runner.MutableMessages.Add(new UserMessage("follow up request"));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);

            var preferredEntryIds = new List<string?>();
            var seenDisplayLines = new List<string[]>();
            var callCount = 0;
            string? labelTargetEntryId = null;
            Func<IReadOnlyList<CodingAgentTreeViewItem>, string?, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>> navigator =
                (items, preferredEntryId, _) =>
                {
                    preferredEntryIds.Add(preferredEntryId);
                    seenDisplayLines.Add(items.Select(item => item.DisplayLine).ToArray());
                    callCount++;
                    labelTargetEntryId ??= items.Single(item =>
                        item.DisplayLine.Contains("root answer", StringComparison.Ordinal)).EntryId;
                    return Task.FromResult(callCount == 1
                        ? new CodingAgentTreeInteractiveNavigator.Result(null, 0, 1, LabelEditEntryId: labelTargetEntryId)
                        : new CodingAgentTreeInteractiveNavigator.Result(null, 0, 1));
                };

            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: tree,
                treeNavigator: navigator,
                treeLabelPrompt: (_, _) => Task.FromResult(CodingAgentTreeLabelPromptResult.Saved("checkpoint")));
            var result = await router.TryHandleAsync("/tree --interactive");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("tree navigator cancelled", result.Message);
            Assert.Equal([null, labelTargetEntryId], preferredEntryIds);
            Assert.Equal("checkpoint", tree.GetLabel(labelTargetEntryId!));
            Assert.DoesNotContain(seenDisplayLines[0], line => line.Contains("[checkpoint]", StringComparison.Ordinal));
            Assert.Contains(seenDisplayLines[1], line => line.Contains("[checkpoint]", StringComparison.Ordinal));
            Assert.Equal(3, runner.Messages.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_TreeInteractiveCommand_WithNavigatorCancelled_ReturnsCancelledStatus()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-cancel-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("just a message"));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);

            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: tree,
                treeNavigator: (_, _, _) => Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(null, 0, 1)));

            var result = await router.TryHandleAsync("/tree -i");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("cancelled", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TryHandleAsync_TreeInteractiveCommand_WithoutNavigator_ReturnsError()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-coding-agent-tree-no-nav-" + Guid.NewGuid().ToString("N"));
        var treePath = Path.Combine(directory, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var result = await router.TryHandleAsync("/tree --interactive");

            Assert.True(result.Handled);
            Assert.True(result.IsError);
            Assert.Contains("interactive tree navigator is not available", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static bool MessageContains(JsonElement message, string text)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var textProperty) &&
                textProperty.GetString()?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }
}
