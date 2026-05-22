using System.Globalization;
using System.Text.Json;
using Tau.Agent;
using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
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
                    Assert.Equal("usage: /scoped-models [set|add|remove|clear|all] [provider/model ...]", result.Message);
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

            Assert.True(unavailable.Handled);
            Assert.True(unavailable.IsError);
            Assert.Equal("settings are not available in this session", unavailable.Message);

            Assert.True(invalid.Handled);
            Assert.True(invalid.IsError);
            Assert.Equal("usage: /settings [current|path]", invalid.Message);
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

            Assert.All(
                [invalid, extra],
                result =>
                {
                    Assert.True(result.Handled);
                    Assert.True(result.IsError);
                    Assert.Equal("usage: /theme [current|list|set|clear] [name]", result.Message);
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
            "commands: /help, /reload, /hotkeys, /settings, /theme, /name, /copy, /files, /export, /share, /import, /new, /session, /tree, /label, /fork, /clone, /resume, /quit, /model, /provider, /models, /providers, /scoped-models, /prompts, /skills, /extensions, /auth, /login, /logout, /changelog, /retry, /thinking, /history, /find, /clear, /compact",
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
            "commands: /help, /reload, /hotkeys, /settings, /theme, /name, /copy, /files, /export, /share, /import, /new, /session, /tree, /label, /fork, /clone, /resume, /quit, /model, /provider, /models, /providers, /scoped-models, /prompts, /skills, /extensions, /auth, /login, /logout, /changelog, /retry, /thinking, /history, /find, /clear, /compact",
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
        var router = new CodingAgentCommandRouter(runner);

        var result = await router.TryHandleAsync("/logout anthropic");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal(
            "logout anthropic: no auth.json credentials found. Environment variables and models.json credentials are unchanged.",
            result.Message);
        Assert.Equal(["anthropic"], runner.LoggedOutProviders);
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
        Assert.Equal("usage: /logout [provider]", result.Message);
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
            // Top-level <ul> has exactly one opening (the outer list).
            var ulOpens = System.Text.RegularExpressions.Regex.Matches(html, "<ul>").Count;
            Assert.True(ulOpens >= 2, $"expected at least two <ul> opens (outer + nested), saw {ulOpens}");
            // The ordered nested list shows as <ol>.
            Assert.Contains("<ol>", html, StringComparison.Ordinal);
            // The structure should keep inner items inside their outer item.
            // Check the document contains the nested order: outer two, ordered inner, another inner, outer three.
            var indexOuterTwo = html.IndexOf("outer two", StringComparison.Ordinal);
            var indexOrderedInner = html.IndexOf("ordered inner", StringComparison.Ordinal);
            var indexAnotherInner = html.IndexOf("another inner", StringComparison.Ordinal);
            var indexOuterThree = html.IndexOf("outer three", StringComparison.Ordinal);
            Assert.True(indexOuterTwo > 0 && indexOrderedInner > indexOuterTwo, "ordered inner should follow outer two");
            Assert.True(indexAnotherInner > indexOrderedInner, "another inner should follow ordered inner");
            Assert.True(indexOuterThree > indexAnotherInner, "outer three should follow inner list");
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
            ]));
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
            $"session: name port slice, model openai/gpt-5.4, messages 3 (user 1, assistant 1, tool 1, toolCalls 1), tokens ~9/128000 context (127991 remaining), retry enabled 2 attempts, base 125ms, file {sessionFile}",
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
        runner.BranchSummaryHandler = (messages, instructions, _) =>
        {
            Assert.Equal("focus decisions", instructions);
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
                Assert.Equal("usage: /thinking [current|cycle|off|minimal|low|medium|high|xhigh]", result.Message);
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

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.SyncFromRunner(runner);

            IReadOnlyList<CodingAgentTreeViewItem>? capturedItems = null;
            Func<IReadOnlyList<CodingAgentTreeViewItem>, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>> navigator =
                (items, _) =>
                {
                    capturedItems = items;
                    return Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(items[0].EntryId, 0, 1));
                };

            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree, treeNavigator: navigator);
            var result = await router.TryHandleAsync("/tree --interactive");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.NotNull(capturedItems);
            Assert.Equal(capturedItems![0].EntryId, ExtractSelectedEntryId(result.Message));
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
                treeNavigator: (_, _) => Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(null, 0, 1)));

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

    private static string? ExtractSelectedEntryId(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        const string prefix = "selected entry ";
        var index = message.IndexOf(prefix, StringComparison.Ordinal);
        return index < 0 ? null : message[(index + prefix.Length)..].Trim();
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
