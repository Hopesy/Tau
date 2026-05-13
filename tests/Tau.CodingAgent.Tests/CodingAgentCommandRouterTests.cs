using System.Text.Json;
using Tau.Agent;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

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
        await File.WriteAllTextAsync(
            Path.Combine(extensions, "commands.json"),
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
        var extensionStore = new CodingAgentExtensionCommandStore(cwd: directory);
        var router = new CodingAgentCommandRouter(runner, extensionCommandStore: extensionStore);

        try
        {
            var result = await router.TryHandleAsync("/extensions");

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("extensions: /hello <name> - Say hello (project); /review - Review source (project, runner)", result.Message);
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
            "commands: /help, /name, /copy, /export, /import, /new, /session, /tree, /label, /fork, /clone, /resume, /quit, /model, /provider, /models, /providers, /prompts, /skills, /extensions, /auth, /login, /compact",
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
            "commands: /help, /name, /copy, /export, /import, /new, /session, /tree, /label, /fork, /clone, /resume, /quit, /model, /provider, /models, /providers, /prompts, /skills, /extensions, /auth, /login, /compact",
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
        var router = new CodingAgentCommandRouter(runner, sessionFile: sessionFile);

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
        var router = new CodingAgentCommandRouter(runner, sessionFile: sessionFile);

        var result = await router.TryHandleAsync("/session");

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Equal(
            $"session: name port slice, model openai/gpt-5.4, messages 3 (user 1, assistant 1, tool 1, toolCalls 1), tokens ~9/128000 context (127991 remaining), file {sessionFile}",
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
            "session: name none, model openai/gpt-5.4, messages 1 (user 1, assistant 0, tool 0, toolCalls 0), tokens ~10/128000 context (127990 remaining), auto-compact 32 (22 remaining), file none",
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
            var router = new CodingAgentCommandRouter(runner, treeSessionController: tree);

            var rendererResult = await router.TryHandleAsync("/tree 20 --search renderer");
            var labelResult = await router.TryHandleAsync("/tree labeled-only --search docs-checkpoint");
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

            Assert.False(noMatchResult.IsError);
            Assert.Contains("tree has no entries matching filter", noMatchResult.Message, StringComparison.Ordinal);

            Assert.True(badSearchResult.IsError);
            Assert.Equal("usage: /tree [max entries] [default|no-tools|user-only|labeled-only|all] [--label-time] [--search query]", badSearchResult.Message);
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
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
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
