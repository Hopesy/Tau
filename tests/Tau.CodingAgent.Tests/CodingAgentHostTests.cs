using Tau.Agent;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentHostTests
{
    [Fact]
    public async Task RunAsync_ExitInput_ShowsWelcomeAndGoodbye_WithoutInvokingRunner()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner);

        var exitCode = await host.RunAsync();

        Assert.Equal(0, exitCode);
        Assert.Empty(runner.Inputs);

        var output = terminal.FlattenedText();
        Assert.Contains("Tau — Coding Agent\n", output);
        Assert.Contains("> ", output);
        Assert.Contains("Goodbye!\n", output);
    }

    [Fact]
    public async Task RunAsync_HelpCommand_RendersCommandListWithoutInvokingRunner()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("/help");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner);

        await host.RunAsync();

        Assert.Empty(runner.Inputs);
        Assert.Contains(
            "status> commands: /help, /name, /copy, /export, /import, /new, /session, /tree, /label, /fork, /resume, /quit, /model, /provider, /models, /providers, /prompts, /skills, /extensions, /auth, /login, /compact",
            terminal.FlattenedText());
    }

    [Fact]
    public async Task RunAsync_QuitCommand_ExitsWithoutReadingFurtherInputOrInvokingRunner()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("/quit");
        terminal.QueueInput("should not run");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner);

        var exitCode = await host.RunAsync();

        Assert.Equal(0, exitCode);
        Assert.Empty(runner.Inputs);

        var output = terminal.FlattenedText();
        Assert.Contains("Goodbye!\n", output);
        Assert.DoesNotContain("you> should not run", output);
        Assert.Equal(1, CountOccurrences(output, "Goodbye!"));
    }

    [Fact]
    public async Task RunAsync_RendersAssistantAndToolLifecycle()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("hello");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => GetEvents());
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner);

        await host.RunAsync();

        var output = terminal.FlattenedText();

        Assert.Single(runner.Inputs);
        Assert.Contains("you> hello\n", output);
        Assert.Contains("thinking", output);
        Assert.Contains("response", output);
        Assert.Contains("tool> [read_file] (done)", output);
        Assert.Contains("Goodbye!\n", output);

        static async IAsyncEnumerable<AgentEvent> GetEvents()
        {
            var partial = new AssistantMessage();
            yield return new MessageUpdateEvent(new ThinkingDeltaEvent(0, "thinking", partial));
            yield return new MessageUpdateEvent(new TextDeltaEvent(1, "response", partial));
            yield return new ToolExecutionStartEvent("tool-1", "read_file");
            yield return new ToolExecutionEndEvent("tool-1", new ToolResult([new TextContent("done")]));
            yield return new AgentEndEvent();
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_RunnerThrows_WritesRuntimeError_AndContinuesToExit()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("boom");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => throw new InvalidOperationException("runner failed"));
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner);

        await host.RunAsync();

        var output = terminal.FlattenedText();

        Assert.Contains("error> runner failed\n", output);
        Assert.Contains("Goodbye!\n", output);
    }

    [Fact]
    public async Task RunAsync_ExpandsPromptTemplateBeforeInvokingRunner()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-prompts-host-" + Guid.NewGuid().ToString("N"));
        var prompts = Path.Combine(directory, ".tau", "prompts");
        Directory.CreateDirectory(prompts);
        await File.WriteAllTextAsync(
            Path.Combine(prompts, "review.md"),
            """
            ---
            description: Review a file
            ---
            Review $1 with $ARGUMENTS
            """);

        var terminal = new FakeTerminal();
        terminal.QueueInput("/review \"src/app.cs\" carefully");
        terminal.QueueInput("exit");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            promptTemplateStore: new CodingAgentPromptTemplateStore(cwd: directory));

        try
        {
            await host.RunAsync();

            var input = Assert.Single(runner.Inputs);
            Assert.Equal("Review src/app.cs with src/app.cs carefully", input.Trim());
            Assert.Contains("you> Review src/app.cs with src/app.cs carefully\n", terminal.FlattenedText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ExpandsSkillCommandBeforeInvokingRunner()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-skills-host-" + Guid.NewGuid().ToString("N"));
        var skillDirectory = Path.Combine(directory, ".tau", "skills", "reviewer");
        Directory.CreateDirectory(skillDirectory);
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        await File.WriteAllTextAsync(
            skillPath,
            """
            ---
            name: reviewer
            description: Review source changes
            ---
            Check the diff and explain risks.
            """);

        var terminal = new FakeTerminal();
        terminal.QueueInput("/skill:reviewer src/app.cs");
        terminal.QueueInput("exit");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            skillStore: new CodingAgentSkillStore(cwd: directory));

        try
        {
            await host.RunAsync();

            var input = Assert.Single(runner.Inputs);
            Assert.Contains($"""<skill name="reviewer" location="{skillPath}">""", input, StringComparison.Ordinal);
            Assert.Contains($"References are relative to {skillDirectory}.", input, StringComparison.Ordinal);
            Assert.Contains("Check the diff and explain risks.", input, StringComparison.Ordinal);
            Assert.EndsWith("src/app.cs", input, StringComparison.Ordinal);
            Assert.Contains("you> <skill name=\"reviewer\"", terminal.FlattenedText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_BuiltInSkillsCommandWinsOverSkillExpansion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-skills-host-list-" + Guid.NewGuid().ToString("N"));
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

        var terminal = new FakeTerminal();
        terminal.QueueInput("/skills");
        terminal.QueueInput("exit");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            skillStore: new CodingAgentSkillStore(cwd: directory));

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Contains("status> skills: /skill:reviewer - Review source changes", terminal.FlattenedText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ExtensionStatusCommandRendersStatusWithoutInvokingRunner()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-host-status-" + Guid.NewGuid().ToString("N"));
        var extensions = Path.Combine(directory, ".tau", "extensions");
        Directory.CreateDirectory(extensions);
        await File.WriteAllTextAsync(
            Path.Combine(extensions, "hello.json"),
            """
            {
              "name": "hello",
              "description": "Say hello",
              "response": "Hello $ARGUMENTS"
            }
            """);

        var terminal = new FakeTerminal();
        terminal.QueueInput("/hello Ada");
        terminal.QueueInput("exit");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            extensionCommandStore: new CodingAgentExtensionCommandStore(cwd: directory));

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Contains("status> Hello Ada", terminal.FlattenedText());
            Assert.DoesNotContain("you> /hello Ada", terminal.FlattenedText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ExtensionRunnerCommandInvokesRunnerWithExpandedPrompt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-host-runner-" + Guid.NewGuid().ToString("N"));
        var extensions = Path.Combine(directory, ".tau", "extensions");
        Directory.CreateDirectory(extensions);
        await File.WriteAllTextAsync(
            Path.Combine(extensions, "review.json"),
            """
            {
              "name": "review",
              "description": "Review source",
              "prompt": "Review $1 with $ARGUMENTS",
              "sendToRunner": true
            }
            """);

        var terminal = new FakeTerminal();
        terminal.QueueInput("/review src/app.cs carefully");
        terminal.QueueInput("exit");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            extensionCommandStore: new CodingAgentExtensionCommandStore(cwd: directory));

        try
        {
            await host.RunAsync();

            var input = Assert.Single(runner.Inputs);
            Assert.Equal("Review src/app.cs with src/app.cs carefully", input);
            Assert.Contains("you> Review src/app.cs with src/app.cs carefully\n", terminal.FlattenedText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_BuiltInCommandWinsOverExtensionCommand()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-host-help-" + Guid.NewGuid().ToString("N"));
        var extensions = Path.Combine(directory, ".tau", "extensions");
        Directory.CreateDirectory(extensions);
        await File.WriteAllTextAsync(
            Path.Combine(extensions, "help.json"),
            """
            {
              "name": "help",
              "description": "Override help",
              "response": "extension help"
            }
            """);

        var terminal = new FakeTerminal();
        terminal.QueueInput("/help");
        terminal.QueueInput("exit");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            extensionCommandStore: new CodingAgentExtensionCommandStore(cwd: directory));

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Contains("status> commands: /help, /name", terminal.FlattenedText());
            Assert.DoesNotContain("extension help", terminal.FlattenedText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_PersistsSessionAfterTurn()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-host-{Guid.NewGuid():N}.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("persist this");
        terminal.QueueInput("exit");

        FakeCodingAgentRunner? runner = null;
        runner = new FakeCodingAgentRunner((input, _) => RunAndCapture(runner!, input));
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner, new CodingAgentSessionStore(path));

        try
        {
            await host.RunAsync();

            var loaded = new CodingAgentSessionStore(path).Load();
            Assert.Equal("openai", loaded.Provider);
            Assert.Equal("gpt-5.4", loaded.Model);
            Assert.Equal(2, loaded.Messages.Count);
            var user = Assert.IsType<UserMessage>(loaded.Messages[0]);
            Assert.Equal("persist this", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);
            var assistant = Assert.IsType<AssistantMessage>(loaded.Messages[1]);
            Assert.Equal("saved", Assert.IsType<TextContent>(Assert.Single(assistant.Content)).Text);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        static async IAsyncEnumerable<AgentEvent> RunAndCapture(FakeCodingAgentRunner runner, string input)
        {
            runner.MutableMessages.Add(new UserMessage(input));
            runner.MutableMessages.Add(new AssistantMessage([new TextContent("saved")]));
            yield return new AgentEndEvent();
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_ModelCommand_SelectsAndPersistsDefaultModel_WithoutInvokingRunner()
    {
        var settingsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-settings-{Guid.NewGuid():N}.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("/model google gemini-2.5-pro");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner, settingsStore: settingsStore);

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Equal("google", runner.Model.Provider);
            Assert.Equal("gemini-2.5-pro", runner.Model.Id);

            var settings = settingsStore.Load();
            Assert.Equal("google", settings.DefaultProvider);
            Assert.Equal("gemini-2.5-pro", settings.DefaultModel);
            Assert.Contains("status> model: google/gemini-2.5-pro", terminal.FlattenedText());
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
    public async Task RunAsync_ModelCurrentCommand_RendersCurrentModel()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("/model");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner);

        await host.RunAsync();

        Assert.Empty(runner.Inputs);
        Assert.Contains("status> model: openai/gpt-5.4", terminal.FlattenedText());
    }

    [Fact]
    public async Task RunAsync_NewCommand_ResetsSessionAndPersistsEmptySnapshot()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-new-{Guid.NewGuid():N}.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("/new");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("old state"));
        runner.SessionName = "old name";
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner, new CodingAgentSessionStore(path));

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Equal(1, runner.ResetSessionCalls);
            Assert.Contains("status> started new session with model openai/gpt-5.4", terminal.FlattenedText());

            var loaded = new CodingAgentSessionStore(path).Load();
            Assert.Empty(loaded.Messages);
            Assert.Null(loaded.Name);
            Assert.Equal("openai", loaded.Provider);
            Assert.Equal("gpt-5.4", loaded.Model);
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
    public async Task RunAsync_CopyCommand_RendersStatusAndUsesClipboard()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("/copy");
        terminal.QueueInput("exit");

        var clipboard = new FakeCodingAgentClipboard();
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("copy me")]));
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner, clipboard: clipboard);

        await host.RunAsync();

        Assert.Empty(runner.Inputs);
        Assert.Equal("copy me", Assert.Single(clipboard.CopiedTexts));
        Assert.Contains("status> copied last assistant message to clipboard", terminal.FlattenedText());
    }

    [Fact]
    public async Task RunAsync_ExportCommand_RendersStatusAndWritesSnapshot()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-host-export-{Guid.NewGuid():N}.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput($"/export {path}");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.SessionName = "host export";
        runner.MutableMessages.Add(new UserMessage("persisted"));
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner);

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Contains($"status> exported session to {System.IO.Path.GetFullPath(path)}", terminal.FlattenedText());

            var loaded = new CodingAgentSessionStore(path).Load();
            Assert.Equal("host export", loaded.Name);
            Assert.Single(loaded.Messages);
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
    public async Task RunAsync_ImportCommand_RendersStatusAndPersistsImportedSnapshot()
    {
        var importPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-host-import-{Guid.NewGuid():N}.json");
        var sessionPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-host-import-current-{Guid.NewGuid():N}.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput($"/import {importPath}");
        terminal.QueueInput("exit");
        var model = new Model
        {
            Provider = "google",
            Id = "gemini-2.5-pro",
            Name = "Gemini 2.5 Pro",
            Api = "google-gemini"
        };
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("stale"));
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            new CodingAgentSessionStore(sessionPath));

        try
        {
            new CodingAgentSessionStore(importPath).Save(
                [
                    new UserMessage("imported"),
                    new AssistantMessage([new TextContent("snapshot")])
                ],
                model,
                "host import");

            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Contains(
                $"status> imported session from {System.IO.Path.GetFullPath(importPath)}: 2 messages, model google/gemini-2.5-pro, name host import",
                terminal.FlattenedText());

            var loaded = new CodingAgentSessionStore(sessionPath).Load();
            Assert.Equal("host import", loaded.Name);
            Assert.Equal("google", loaded.Provider);
            Assert.Equal("gemini-2.5-pro", loaded.Model);
            Assert.Equal(2, loaded.Messages.Count);
        }
        finally
        {
            if (File.Exists(importPath))
            {
                File.Delete(importPath);
            }

            if (File.Exists(sessionPath))
            {
                File.Delete(sessionPath);
            }
        }
    }

    [Fact]
    public async Task RunAsync_NameCommand_RendersAndPersistsSessionName()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-name-command-{Guid.NewGuid():N}.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("/name focused port slice");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner, new CodingAgentSessionStore(path));

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Contains("status> session name: focused port slice", terminal.FlattenedText());

            var loaded = new CodingAgentSessionStore(path).Load();
            Assert.Equal("focused port slice", loaded.Name);
            Assert.Equal("openai", loaded.Provider);
            Assert.Equal("gpt-5.4", loaded.Model);
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
    public async Task RunAsync_SessionCommand_RendersSessionStorePathWithoutInvokingRunner()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-session-command-{Guid.NewGuid():N}.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("/session");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("current state"));
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner, new CodingAgentSessionStore(path));

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Contains(
                $"status> session: name none, model openai/gpt-5.4, messages 1 (user 1, assistant 0, tool 0, toolCalls 0), tokens ~4/128000 context (127996 remaining), file {path}",
                terminal.FlattenedText());
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
    public async Task RunAsync_AuthCommand_RendersCurrentAuthStatus()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("/auth");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("openai", true, "environment", false, false, "Credentials are available.")
        };
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner);

        await host.RunAsync();

        Assert.Empty(runner.Inputs);
        Assert.Contains("status> auth openai: configured via environment. Credentials are available.", terminal.FlattenedText());
    }

    [Fact]
    public async Task RunAsync_LoginCommand_ReportsUnportedLoginWithoutInvokingRunner()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("/login anthropic");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("anthropic", false, "none", false, true, "No credentials found.")
        };
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner);

        await host.RunAsync();

        Assert.Empty(runner.Inputs);
        Assert.Contains("error> login anthropic: OAuth login flow is not yet ported in Tau", terminal.FlattenedText());
    }

    [Fact]
    public async Task RunAsync_CompactCommand_RendersStatusAndPersistsCompactedSession()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-compact-{Guid.NewGuid():N}.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("/compact keep blockers");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.CompactHandler = (_, _) =>
        {
            runner.MutableMessages.Clear();
            runner.MutableMessages.Add(new UserMessage("The conversation history before this point was compacted into the following summary:\n\n<summary>\nsummary\n</summary>"));
            return Task.FromResult(new CodingAgentCompactionResult("summary", 5, 1));
        };
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner, new CodingAgentSessionStore(path));

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Equal("keep blockers", runner.LastCompactInstructions);
            Assert.Contains("status> compacted session: 5 -> 1 messages", terminal.FlattenedText());

            var loaded = new CodingAgentSessionStore(path).Load();
            var message = Assert.Single(loaded.Messages);
            var user = Assert.IsType<UserMessage>(message);
            var text = Assert.IsType<TextContent>(Assert.Single(user.Content)).Text;
            Assert.Contains("<summary>", text);
            Assert.Contains("summary", text);
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
    public async Task RunAsync_AutoCompactsBeforeNormalTurnWhenThresholdIsExceeded()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-auto-compact-{Guid.NewGuid():N}");
        var sessionPath = Path.Combine(directory, "session.json");
        var treePath = Path.Combine(directory, "session.jsonl");
        var terminal = new FakeTerminal();
        terminal.QueueInput("next task");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage(new string('u', 120)));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent(new string('a', 120))]));
        runner.CompactHandler = (_, _) =>
        {
            runner.MutableMessages.Clear();
            runner.MutableMessages.Add(CodingAgentCompactionMessages.CreateSummaryMessage("auto summary"));
            return Task.FromResult(new CodingAgentCompactionResult("auto summary", 2, 1, 60));
        };

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            var host = new CodingAgentHost(
                new InteractiveConsoleSession(terminal),
                runner,
                new CodingAgentSessionStore(sessionPath),
                treeSessionController: tree,
                autoCompaction: new CodingAgentAutoCompactionOptions(1, "keep blockers"));

            await host.RunAsync();

            Assert.Equal("keep blockers", runner.LastCompactInstructions);
            Assert.Equal(["next task"], runner.Inputs);
            Assert.Contains("status> auto-compacted session: 2 -> 1 messages, estimated", terminal.FlattenedText());

            var jsonl = File.ReadAllText(treePath);
            Assert.Contains("\"type\":\"compaction\"", jsonl, StringComparison.Ordinal);
            Assert.Contains("\"fromHook\":true", jsonl, StringComparison.Ordinal);

            var loaded = new CodingAgentSessionStore(sessionPath).Load();
            var summary = Assert.Single(loaded.Messages);
            var text = Assert.IsType<TextContent>(Assert.IsType<UserMessage>(summary).Content[0]).Text;
            Assert.Contains("auto summary", text, StringComparison.Ordinal);
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
    public async Task RunAsync_AutoCompactionBelowThresholdDoesNotCallCompaction()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("short task");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("short"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("small")]));
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            autoCompaction: new CodingAgentAutoCompactionOptions(10_000));

        await host.RunAsync();

        Assert.Null(runner.LastCompactInstructions);
        Assert.Equal(["short task"], runner.Inputs);
        Assert.DoesNotContain("auto-compacted session", terminal.FlattenedText());
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

}
