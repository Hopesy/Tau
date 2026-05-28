using Tau.Agent;
using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
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
        Assert.Contains($"status> {CodingAgentCommandCatalog.HelpLine}", terminal.FlattenedText());
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
    public async Task RunAsync_RunningTurnForwardsSteeringInputToRunner()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("start turn");
        terminal.QueueInput("exit");

        var steered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeCodingAgentRunner((_, _) => RunUntilInputAsync(steered.Task));
        runner.SteeringObserver = input => steered.TrySetResult(input);

        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            turnInputSource: new ScriptedTurnInputSource(
                new CodingAgentTurnInput(CodingAgentTurnInputKind.Steering, "adjust the current answer")));

        await host.RunAsync();

        Assert.Equal(["start turn"], runner.Inputs);
        Assert.Equal(["adjust the current answer"], runner.SteeringInputs);
        Assert.Empty(runner.FollowUpInputs);
    }

    [Fact]
    public async Task RunAsync_RunningTurnForwardsFollowUpInputToRunner()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("start turn");
        terminal.QueueInput("exit");

        var followedUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeCodingAgentRunner((_, _) => RunUntilInputAsync(followedUp.Task));
        runner.FollowUpObserver = input => followedUp.TrySetResult(input);

        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            turnInputSource: new ScriptedTurnInputSource(
                new CodingAgentTurnInput(CodingAgentTurnInputKind.FollowUp, "run another pass after this")));

        await host.RunAsync();

        Assert.Equal(["start turn"], runner.Inputs);
        Assert.Empty(runner.SteeringInputs);
        Assert.Equal(["run another pass after this"], runner.FollowUpInputs);
    }

    [Fact]
    public async Task RunAsync_RunnerThrows_RollsBackFailedTurnAndContinuesToExit()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-rollback-throw-{Guid.NewGuid():N}.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("boom");
        terminal.QueueInput("exit");

        FakeCodingAgentRunner? runner = null;
        runner = new FakeCodingAgentRunner((input, _) =>
        {
            runner!.MutableMessages.Add(new UserMessage(input));
            runner.MutableMessages.Add(new AssistantMessage([new TextContent("partial response")]));
            throw new InvalidOperationException("runner failed");
        });
        runner.MutableMessages.Add(new UserMessage("before failure"));
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner, new CodingAgentSessionStore(path));

        try
        {
            await host.RunAsync();

            var output = terminal.FlattenedText();

            Assert.Contains("error> runner failed\n", output);
            Assert.Contains("status> rolled back failed turn\n", output);
            Assert.Contains("Goodbye!\n", output);

            Assert.Single(runner.Messages);
            var current = Assert.IsType<UserMessage>(runner.Messages[0]);
            Assert.Equal("before failure", Assert.IsType<TextContent>(Assert.Single(current.Content)).Text);

            var loaded = new CodingAgentSessionStore(path).Load();
            var saved = Assert.Single(loaded.Messages);
            Assert.Equal("before failure", Assert.IsType<TextContent>(Assert.Single(Assert.IsType<UserMessage>(saved).Content)).Text);
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
    public async Task RunAsync_AgentEndError_RollsBackFlatAndTreeSessions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-rollback-agent-end-{Guid.NewGuid():N}");
        var sessionPath = Path.Combine(directory, "session.json");
        var treePath = Path.Combine(directory, "session.jsonl");
        var terminal = new FakeTerminal();
        terminal.QueueInput("bad turn");
        terminal.QueueInput("exit");

        FakeCodingAgentRunner? runner = null;
        runner = new FakeCodingAgentRunner((input, _) => RunAndFail(runner!, input));
        runner.MutableMessages.Add(new UserMessage("stable context"));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.ReplaceWithRunnerSession(runner);
            var host = new CodingAgentHost(
                new InteractiveConsoleSession(terminal),
                runner,
                new CodingAgentSessionStore(sessionPath),
                treeSessionController: tree);

            await host.RunAsync();

            var output = terminal.FlattenedText();
            Assert.Contains("error> provider unavailable\n", output);
            Assert.Contains("status> rolled back failed turn\n", output);

            Assert.Single(runner.Messages);
            Assert.Equal(
                "stable context",
                Assert.IsType<TextContent>(Assert.Single(Assert.IsType<UserMessage>(runner.Messages[0]).Content)).Text);

            var loaded = new CodingAgentSessionStore(sessionPath).Load();
            var saved = Assert.Single(loaded.Messages);
            Assert.Equal("stable context", Assert.IsType<TextContent>(Assert.Single(Assert.IsType<UserMessage>(saved).Content)).Text);

            var treeJsonl = await File.ReadAllTextAsync(treePath);
            Assert.Contains("stable context", treeJsonl, StringComparison.Ordinal);
            Assert.DoesNotContain("bad turn", treeJsonl, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        static async IAsyncEnumerable<AgentEvent> RunAndFail(FakeCodingAgentRunner runner, string input)
        {
            runner.MutableMessages.Add(new UserMessage(input));
            yield return new AgentStartEvent();
            yield return new AgentEndEvent("provider unavailable");
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_RetryableAgentEndError_RetriesAndPersistsSuccessfulAttempt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-retry-agent-end-{Guid.NewGuid():N}");
        var sessionPath = Path.Combine(directory, "session.json");
        var treePath = Path.Combine(directory, "session.jsonl");
        var terminal = new FakeTerminal();
        terminal.QueueInput("retry turn");
        terminal.QueueInput("exit");

        var attempts = 0;
        FakeCodingAgentRunner? runner = null;
        runner = new FakeCodingAgentRunner((input, _) => RunAttempt(runner!, input, ++attempts));
        runner.MutableMessages.Add(new UserMessage("stable context"));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.ReplaceWithRunnerSession(runner);
            var host = new CodingAgentHost(
                new InteractiveConsoleSession(terminal),
                runner,
                new CodingAgentSessionStore(sessionPath),
                treeSessionController: tree,
                retryOptions: new CodingAgentRetryOptions(2, 0));

            await host.RunAsync();

            Assert.Equal(["retry turn", "retry turn"], runner.Inputs);
            Assert.Equal(3, runner.Messages.Count);
            Assert.Equal("stable context", MessageText(Assert.IsType<UserMessage>(runner.Messages[0])));
            Assert.Equal("retry turn", MessageText(Assert.IsType<UserMessage>(runner.Messages[1])));
            Assert.Equal("ok", MessageText(Assert.IsType<AssistantMessage>(runner.Messages[2])));

            var output = terminal.FlattenedText();
            Assert.Contains("error> 429 rate limit\n", output);
            Assert.Contains("status> auto-retry 1/2 after 0ms: 429 rate limit\n", output);
            Assert.Contains("status> auto-retry recovered after 1 attempt\n", output);

            var loaded = new CodingAgentSessionStore(sessionPath).Load();
            Assert.Equal(3, loaded.Messages.Count);
            Assert.Equal("ok", MessageText(Assert.IsType<AssistantMessage>(loaded.Messages[2])));

            var treeJsonl = await File.ReadAllTextAsync(treePath);
            Assert.Contains("stable context", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("retry turn", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("ok", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("\"type\":\"auto_retry_start\"", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("\"attempt\":1", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("\"maxAttempts\":2", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("\"delayMs\":0", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("\"errorMessage\":\"429 rate limit\"", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("\"type\":\"auto_retry_end\"", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("\"success\":true", treeJsonl, StringComparison.Ordinal);
            Assert.DoesNotContain("failed transient", treeJsonl, StringComparison.Ordinal);

            var retryStartIndex = treeJsonl.IndexOf("\"type\":\"auto_retry_start\"", StringComparison.Ordinal);
            var retryUserIndex = treeJsonl.IndexOf("retry turn", StringComparison.Ordinal);
            var retryAssistantIndex = treeJsonl.IndexOf("ok", StringComparison.Ordinal);
            var retryEndIndex = treeJsonl.IndexOf("\"type\":\"auto_retry_end\"", StringComparison.Ordinal);
            Assert.True(
                retryStartIndex < retryUserIndex &&
                retryUserIndex < retryAssistantIndex &&
                retryAssistantIndex < retryEndIndex,
                "retry audit should wrap the successful retry attempt in JSONL order");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        static async IAsyncEnumerable<AgentEvent> RunAttempt(FakeCodingAgentRunner runner, string input, int attempt)
        {
            runner.MutableMessages.Add(new UserMessage(input));
            if (attempt == 1)
            {
                runner.MutableMessages.Add(new AssistantMessage([new TextContent("failed transient")]));
                yield return new AgentEndEvent("429 rate limit");
                await Task.CompletedTask;
                yield break;
            }

            runner.MutableMessages.Add(new AssistantMessage([new TextContent("ok")]));
            yield return new AgentEndEvent();
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_RetryableTurnPassesStableTreeSessionLogContextToRunner()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-log-context-{Guid.NewGuid():N}");
        var sessionPath = Path.Combine(directory, "session.json");
        var treePath = Path.Combine(directory, "session.jsonl");
        var terminal = new FakeTerminal();
        terminal.QueueInput("retry turn");
        terminal.QueueInput("exit");

        var attempts = 0;
        var runner = new FakeCodingAgentRunner((_, _) => RunAttempt(++attempts));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            var sessionId = tree.GetSummary().SessionId;
            var host = new CodingAgentHost(
                new InteractiveConsoleSession(terminal),
                runner,
                new CodingAgentSessionStore(sessionPath),
                treeSessionController: tree,
                retryOptions: new CodingAgentRetryOptions(2, 0));

            await host.RunAsync();

            Assert.Equal(2, runner.RunLogContexts.Count);
            var first = runner.RunLogContexts[0];
            var second = runner.RunLogContexts[1];
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.False(string.IsNullOrWhiteSpace(first.CorrelationId));
            Assert.False(string.IsNullOrWhiteSpace(first.MessageId));
            Assert.Equal(sessionId, first.SessionId);
            Assert.Equal(first.CorrelationId, second.CorrelationId);
            Assert.Equal(first.MessageId, second.MessageId);
            Assert.Equal(first.SessionId, second.SessionId);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        static async IAsyncEnumerable<AgentEvent> RunAttempt(int attempt)
        {
            yield return attempt == 1
                ? new AgentEndEvent("429 rate limit")
                : new AgentEndEvent();
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_NonRetryableAgentEndError_DoesNotRetryAndRollsBack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-nonretry-{Guid.NewGuid():N}.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("bad request");
        terminal.QueueInput("exit");

        FakeCodingAgentRunner? runner = null;
        runner = new FakeCodingAgentRunner((input, _) => RunAndFail(runner!, input));
        runner.MutableMessages.Add(new UserMessage("stable context"));
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            new CodingAgentSessionStore(path),
            retryOptions: new CodingAgentRetryOptions(2, 0));

        try
        {
            await host.RunAsync();

            Assert.Equal(["bad request"], runner.Inputs);
            Assert.Single(runner.Messages);
            Assert.Equal("stable context", MessageText(Assert.IsType<UserMessage>(runner.Messages[0])));

            var output = terminal.FlattenedText();
            Assert.Contains("error> validation failed\n", output);
            Assert.Contains("status> rolled back failed turn\n", output);
            Assert.DoesNotContain("auto-retry", output);

            var loaded = new CodingAgentSessionStore(path).Load();
            var saved = Assert.Single(loaded.Messages);
            Assert.Equal("stable context", MessageText(Assert.IsType<UserMessage>(saved)));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        static async IAsyncEnumerable<AgentEvent> RunAndFail(FakeCodingAgentRunner runner, string input)
        {
            runner.MutableMessages.Add(new UserMessage(input));
            yield return new AgentEndEvent("validation failed");
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_RetryableException_StopsAfterMaxAttemptsAndRollsBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-retry-exhausted-{Guid.NewGuid():N}");
        var sessionPath = Path.Combine(directory, "session.json");
        var treePath = Path.Combine(directory, "session.jsonl");
        var terminal = new FakeTerminal();
        terminal.QueueInput("unstable");
        terminal.QueueInput("exit");

        FakeCodingAgentRunner? runner = null;
        runner = new FakeCodingAgentRunner((input, _) =>
        {
            runner!.MutableMessages.Add(new UserMessage(input));
            runner.MutableMessages.Add(new AssistantMessage([new TextContent("partial")]));
            throw new TimeoutException("timeout");
        });
        runner.MutableMessages.Add(new UserMessage("stable context"));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.ReplaceWithRunnerSession(runner);
            var host = new CodingAgentHost(
                new InteractiveConsoleSession(terminal),
                runner,
                new CodingAgentSessionStore(sessionPath),
                treeSessionController: tree,
                retryOptions: new CodingAgentRetryOptions(2, 0));

            await host.RunAsync();

            Assert.Equal(["unstable", "unstable", "unstable"], runner.Inputs);
            Assert.Single(runner.Messages);
            Assert.Equal("stable context", MessageText(Assert.IsType<UserMessage>(runner.Messages[0])));

            var output = terminal.FlattenedText();
            Assert.Contains("status> auto-retry 1/2 after 0ms: timeout\n", output);
            Assert.Contains("status> auto-retry 2/2 after 0ms: timeout\n", output);
            Assert.Contains("error> timeout\n", output);
            Assert.Contains("status> rolled back failed turn\n", output);

            var loaded = new CodingAgentSessionStore(sessionPath).Load();
            var saved = Assert.Single(loaded.Messages);
            Assert.Equal("stable context", MessageText(Assert.IsType<UserMessage>(saved)));

            var treeJsonl = await File.ReadAllTextAsync(treePath);
            Assert.Contains("stable context", treeJsonl, StringComparison.Ordinal);
            Assert.Equal(2, CountOccurrences(treeJsonl, "\"type\":\"auto_retry_start\""));
            Assert.Equal(1, CountOccurrences(treeJsonl, "\"type\":\"auto_retry_end\""));
            Assert.Contains("\"success\":false", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("\"finalError\":\"timeout\"", treeJsonl, StringComparison.Ordinal);
            Assert.DoesNotContain("unstable", treeJsonl, StringComparison.Ordinal);
            Assert.DoesNotContain("partial", treeJsonl, StringComparison.Ordinal);
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
    public async Task RunAsync_RetryDelayCancellationRecordsRetryEndAndRendersSpecificStatus()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-retry-cancel-{Guid.NewGuid():N}");
        var sessionPath = Path.Combine(directory, "session.json");
        var treePath = Path.Combine(directory, "session.jsonl");
        var terminal = new FakeTerminal();
        terminal.QueueInput("retry cancel");

        FakeCodingAgentRunner? runner = null;
        runner = new FakeCodingAgentRunner((input, _) => RunAttempt(runner!, input));

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(100));
            var host = new CodingAgentHost(
                new InteractiveConsoleSession(terminal),
                runner,
                new CodingAgentSessionStore(sessionPath),
                treeSessionController: tree,
                retryOptions: new CodingAgentRetryOptions(2, 10_000));

            await host.RunAsync(cts.Token);

            Assert.Equal(["retry cancel"], runner.Inputs);
            Assert.Empty(runner.Messages);

            var output = terminal.FlattenedText();
            Assert.Contains("status> auto-retry 1/2 after 10000ms: timeout\n", output);
            Assert.Contains("[Cancelled]\n", output);
            Assert.Contains("status> auto-retry cancelled during delay\n", output);
            Assert.Contains("status> rolled back cancelled turn\n", output);

            var treeJsonl = await File.ReadAllTextAsync(treePath);
            Assert.Contains("\"type\":\"auto_retry_start\"", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("\"type\":\"auto_retry_end\"", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("\"success\":false", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("\"finalError\":\"Retry cancelled\"", treeJsonl, StringComparison.Ordinal);
            Assert.DoesNotContain("retry cancel", treeJsonl, StringComparison.Ordinal);

            var loaded = new CodingAgentSessionStore(sessionPath).Load();
            Assert.Empty(loaded.Messages);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        static async IAsyncEnumerable<AgentEvent> RunAttempt(FakeCodingAgentRunner runner, string input)
        {
            runner.MutableMessages.Add(new UserMessage(input));
            runner.MutableMessages.Add(new AssistantMessage([new TextContent("timeout partial")]));
            yield return new AgentEndEvent("timeout");
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_RetryOffCommand_DisablesRetryForNextTurnAndPersistsSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-retry-off-{Guid.NewGuid():N}");
        var sessionPath = Path.Combine(directory, "session.json");
        var settingsPath = Path.Combine(directory, "settings.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("/retry off");
        terminal.QueueInput("unstable");
        terminal.QueueInput("exit");

        FakeCodingAgentRunner? runner = null;
        runner = new FakeCodingAgentRunner((input, _) =>
        {
            runner!.MutableMessages.Add(new UserMessage(input));
            runner.MutableMessages.Add(new AssistantMessage([new TextContent("partial")]));
            throw new TimeoutException("timeout");
        });
        runner.MutableMessages.Add(new UserMessage("stable context"));

        try
        {
            Directory.CreateDirectory(directory);
            var settingsStore = new CodingAgentSettingsStore(settingsPath);
            var host = new CodingAgentHost(
                new InteractiveConsoleSession(terminal),
                runner,
                new CodingAgentSessionStore(sessionPath),
                settingsStore: settingsStore,
                retryOptions: new CodingAgentRetryOptions(2, 0));

            await host.RunAsync();

            Assert.Equal(["unstable"], runner.Inputs);
            Assert.Single(runner.Messages);
            Assert.Equal("stable context", MessageText(Assert.IsType<UserMessage>(runner.Messages[0])));

            var output = terminal.FlattenedText();
            Assert.Contains("status> retry: off\n", output);
            Assert.Contains("error> timeout\n", output);
            Assert.Contains("status> rolled back failed turn\n", output);
            Assert.DoesNotContain("auto-retry", output);

            var settings = settingsStore.Load();
            Assert.Equal(0, settings.RetryMaxAttempts);
            Assert.Equal(0, settings.RetryBaseDelayMilliseconds);

            var loaded = new CodingAgentSessionStore(sessionPath).Load();
            var saved = Assert.Single(loaded.Messages);
            Assert.Equal("stable context", MessageText(Assert.IsType<UserMessage>(saved)));
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
    public async Task RunAsync_ContextOverflowAgentEndError_CompactsAndRetriesSuccessfulAttempt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-overflow-retry-{Guid.NewGuid():N}");
        var sessionPath = Path.Combine(directory, "session.json");
        var treePath = Path.Combine(directory, "session.jsonl");
        var terminal = new FakeTerminal();
        terminal.QueueInput("large next turn");
        terminal.QueueInput("exit");

        var attempts = 0;
        FakeCodingAgentRunner? runner = null;
        runner = new FakeCodingAgentRunner((input, _) => RunAttempt(runner!, input, ++attempts));
        runner.MutableMessages.Add(new UserMessage("old prompt"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("old answer")]));
        runner.CompactHandler = (instructions, _) =>
        {
            Assert.Contains("context overflow", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, runner.Messages.Count);
            Assert.Equal("old prompt", MessageText(Assert.IsType<UserMessage>(runner.Messages[0])));
            runner.MutableMessages.Clear();
            runner.MutableMessages.Add(CodingAgentCompactionMessages.CreateSummaryMessage("overflow summary"));
            return Task.FromResult(new CodingAgentCompactionResult("overflow summary", 2, 1, 300));
        };

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(
                treePath,
                new CodingAgentCompactionRetentionOptions(0, 0));
            tree.ReplaceWithRunnerSession(runner);
            var host = new CodingAgentHost(
                new InteractiveConsoleSession(terminal),
                runner,
                new CodingAgentSessionStore(sessionPath),
                treeSessionController: tree,
                retryOptions: CodingAgentRetryOptions.Disabled);

            await host.RunAsync();

            Assert.Equal(["large next turn", "large next turn"], runner.Inputs);
            Assert.Equal(3, runner.Messages.Count);
            Assert.Contains("overflow summary", MessageText(Assert.IsType<UserMessage>(runner.Messages[0])), StringComparison.Ordinal);
            Assert.Equal("large next turn", MessageText(Assert.IsType<UserMessage>(runner.Messages[1])));
            Assert.Equal("ok after compact", MessageText(Assert.IsType<AssistantMessage>(runner.Messages[2])));

            var output = terminal.FlattenedText();
            Assert.Contains("error> maximum context length exceeded\n", output);
            Assert.Contains("status> context overflow compacted session: 2 -> 1 messages; retrying turn\n", output);

            var loaded = new CodingAgentSessionStore(sessionPath).Load();
            Assert.Equal(3, loaded.Messages.Count);
            Assert.Contains("overflow summary", MessageText(Assert.IsType<UserMessage>(loaded.Messages[0])), StringComparison.Ordinal);
            Assert.Equal("ok after compact", MessageText(Assert.IsType<AssistantMessage>(loaded.Messages[2])));

            var treeJsonl = await File.ReadAllTextAsync(treePath);
            Assert.Contains("\"type\":\"compaction\"", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("\"fromHook\":true", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("overflow summary", treeJsonl, StringComparison.Ordinal);
            Assert.Contains("ok after compact", treeJsonl, StringComparison.Ordinal);
            Assert.DoesNotContain("overflow partial", treeJsonl, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        static async IAsyncEnumerable<AgentEvent> RunAttempt(FakeCodingAgentRunner runner, string input, int attempt)
        {
            runner.MutableMessages.Add(new UserMessage(input));
            if (attempt == 1)
            {
                runner.MutableMessages.Add(new AssistantMessage([new TextContent("overflow partial")]));
                yield return new AgentEndEvent("maximum context length exceeded");
                await Task.CompletedTask;
                yield break;
            }

            runner.MutableMessages.Add(new AssistantMessage([new TextContent("ok after compact")]));
            yield return new AgentEndEvent();
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_ContextOverflowCompactionFailure_RollsBackOriginalSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-overflow-fail-{Guid.NewGuid():N}");
        var sessionPath = Path.Combine(directory, "session.json");
        var treePath = Path.Combine(directory, "session.jsonl");
        var terminal = new FakeTerminal();
        terminal.QueueInput("large next turn");
        terminal.QueueInput("exit");

        FakeCodingAgentRunner? runner = null;
        runner = new FakeCodingAgentRunner((input, _) => RunAndOverflow(runner!, input));
        runner.MutableMessages.Add(new UserMessage("stable context"));
        runner.CompactHandler = (_, _) => throw new InvalidOperationException("summarizer failed");

        try
        {
            Directory.CreateDirectory(directory);
            var tree = CodingAgentTreeSessionController.OpenOrCreate(treePath);
            tree.ReplaceWithRunnerSession(runner);
            var host = new CodingAgentHost(
                new InteractiveConsoleSession(terminal),
                runner,
                new CodingAgentSessionStore(sessionPath),
                treeSessionController: tree,
                retryOptions: CodingAgentRetryOptions.Disabled);

            await host.RunAsync();

            Assert.Equal(["large next turn"], runner.Inputs);
            var current = Assert.Single(runner.Messages);
            Assert.Equal("stable context", MessageText(Assert.IsType<UserMessage>(current)));

            var output = terminal.FlattenedText();
            Assert.Contains("error> context window exceeded\n", output);
            Assert.Contains("error> context overflow recovery failed: summarizer failed\n", output);
            Assert.Contains("status> rolled back failed turn\n", output);

            var loaded = new CodingAgentSessionStore(sessionPath).Load();
            var saved = Assert.Single(loaded.Messages);
            Assert.Equal("stable context", MessageText(Assert.IsType<UserMessage>(saved)));

            var treeJsonl = await File.ReadAllTextAsync(treePath);
            Assert.Contains("stable context", treeJsonl, StringComparison.Ordinal);
            Assert.DoesNotContain("\"type\":\"compaction\"", treeJsonl, StringComparison.Ordinal);
            Assert.DoesNotContain("large next turn", treeJsonl, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        static async IAsyncEnumerable<AgentEvent> RunAndOverflow(FakeCodingAgentRunner runner, string input)
        {
            runner.MutableMessages.Add(new UserMessage(input));
            yield return new AgentEndEvent("context window exceeded");
            await Task.CompletedTask;
        }
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
            Assert.Contains($"status> {CodingAgentCommandCatalog.HelpLine}", terminal.FlattenedText());
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
        runner.ConfigureAuth("google");
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
    public async Task RunAsync_ModelSelectCommand_UsesSelectorAndPersistsDefaultModel()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-model-select-host-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(directory, "settings.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("/model select");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("openai", "google");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            settingsStore: settingsStore,
            modelSelector: (_, _) => Task.FromResult<string?>("google/gemini-2.5-pro"));

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Equal("google", runner.Model.Provider);
            Assert.Equal("gemini-2.5-pro", runner.Model.Id);
            Assert.Equal("google", settingsStore.Load().DefaultProvider);
            Assert.Contains("status> model: google/gemini-2.5-pro", terminal.FlattenedText(), StringComparison.Ordinal);
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
    public async Task RunAsync_ModelCycleKey_UsesScopedModelsAndPersistsDefaultModel()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-cycle-host-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(directory, "settings.json");
        var terminal = new FakeTerminal();
        var keyReader = new ScriptedKeyReader();
        keyReader.EnqueueRaw(new ConsoleKeyInfo('\x10', ConsoleKey.P, shift: false, alt: false, control: true));
        keyReader.EnqueueRaw(new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: false, alt: false, control: true));
        var renderer = new CapturingRenderer();
        var editor = new InteractiveInputEditor(keyReader, renderer);

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("openai", "google");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            EnabledModels: ["openai/gpt-5.4", "google/gemini-2.5-pro"]));
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal, editor),
            runner,
            settingsStore: settingsStore);

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Equal("google", runner.Model.Provider);
            Assert.Equal("gemini-2.5-pro", runner.Model.Id);

            var settings = settingsStore.Load();
            Assert.Equal("google", settings.DefaultProvider);
            Assert.Equal("gemini-2.5-pro", settings.DefaultModel);
            Assert.Equal(["openai/gpt-5.4", "google/gemini-2.5-pro"], settings.EnabledModels);
            Assert.Contains("status> model: google/gemini-2.5-pro (scoped)", terminal.FlattenedText());
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
    public async Task RunAsync_ModelSelectKey_UsesSelectorAndPreservesDraft()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-model-select-key-host-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(directory, "settings.json");
        var terminal = new FakeTerminal();
        var keyReader = new ScriptedKeyReader();
        foreach (var ch in "draft")
        {
            keyReader.EnqueueRaw(new ConsoleKeyInfo(ch, ConsoleKey.NoName, shift: false, alt: false, control: false));
        }

        keyReader.EnqueueRaw(new ConsoleKeyInfo('\x0C', ConsoleKey.L, shift: false, alt: false, control: true));
        keyReader.EnqueueRaw(new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: false, alt: false, control: true));
        var renderer = new CapturingRenderer();
        var editor = new InteractiveInputEditor(keyReader, renderer);

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.ConfigureAuth("openai", "google");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal, editor),
            runner,
            settingsStore: settingsStore,
            modelSelector: (_, _) => Task.FromResult<string?>("google/gemini-2.5-pro"));

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Equal("google", runner.Model.Provider);
            Assert.Equal("gemini-2.5-pro", runner.Model.Id);
            Assert.Equal("draft", editor.Buffer.Draft);
            Assert.Equal("google", settingsStore.Load().DefaultProvider);
            Assert.Contains("status> model: google/gemini-2.5-pro", terminal.FlattenedText(), StringComparison.Ordinal);
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
    public async Task RunAsync_SettingsCommand_RendersCurrentSettingsWithoutInvokingRunner()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-settings-host-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(directory, "settings.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("/settings");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            ThinkingLevel = ThinkingLevel.Medium
        };
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "google",
            "gemini-2.5-pro",
            "labeled-only",
            RetryMaxAttempts: 2,
            RetryBaseDelayMilliseconds: 50,
            DefaultThinkingLevel: "high",
            EnabledModels: ["google/gemini-2.5-pro"]));
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            settingsStore: settingsStore);

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            var output = terminal.FlattenedText();
            Assert.Contains($"status> settings: {settingsPath}", output, StringComparison.Ordinal);
            Assert.Contains("current model: openai/gpt-5.4", output, StringComparison.Ordinal);
            Assert.Contains("current thinking: medium", output, StringComparison.Ordinal);
            Assert.Contains("default model: google/gemini-2.5-pro", output, StringComparison.Ordinal);
            Assert.Contains("tree filter: labeled-only", output, StringComparison.Ordinal);
            Assert.Contains("retry: enabled 2 attempts, base 50ms", output, StringComparison.Ordinal);
            Assert.Contains("default thinking: high", output, StringComparison.Ordinal);
            Assert.Contains("scoped models: 1 enabled (google/gemini-2.5-pro)", output, StringComparison.Ordinal);
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
    public async Task RunAsync_SettingsCommand_UsesInteractiveSelectorWhenAvailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-settings-selector-host-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(directory, "settings.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("/settings");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            SteeringMode: "one-at-a-time"));
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            settingsStore: settingsStore,
            settingsSelector: (_, _) => Task.FromResult<string?>(CodingAgentSettingsSelector.SteeringModeAction));

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Equal(AgentQueueMode.All, runner.SteeringMode);
            Assert.Equal("all", settingsStore.Load().SteeringMode);
            Assert.Contains("status> steering mode: all", terminal.FlattenedText(), StringComparison.Ordinal);
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
    public async Task RunAsync_ScopedModelsCommand_UsesInteractiveSelectorWhenAvailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-scoped-models-selector-host-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(directory, "settings.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("/scoped-models");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            settingsStore: settingsStore,
            scopedModelsSelector: (_, _) => Task.FromResult(
                CodingAgentScopedModelsSelection.Saved(["google/gemini-2.5-pro"])));

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Equal(["google/gemini-2.5-pro"], settingsStore.Load().EnabledModels);
            Assert.Contains("status> scoped models: 1/2 enabled", terminal.FlattenedText(), StringComparison.Ordinal);
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
    public async Task RunAsync_ThemeSelectCommand_RendersSelectedThemeAndPersistsSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-theme-select-host-{Guid.NewGuid():N}");
        var projectThemes = Path.Combine(directory, ".tau", "themes");
        Directory.CreateDirectory(projectThemes);
        await File.WriteAllTextAsync(
            Path.Combine(projectThemes, "solarized.json"),
            CodingAgentThemeStoreTests.CreateThemeJson("solarized"));
        var settingsPath = Path.Combine(directory, "settings.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("/theme select");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            settingsStore: settingsStore,
            themeStore: new CodingAgentThemeStore(
                cwd: directory,
                userThemesDirectory: Path.Combine(directory, "missing-user-themes"),
                explicitPaths: []),
            themeSelector: (_, _, _) => Task.FromResult<string?>("solarized"));

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Equal("solarized", settingsStore.Load().Theme);
            Assert.Contains("status> theme: solarized", terminal.FlattenedText(), StringComparison.Ordinal);
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
                $"status> session: name none, model openai/gpt-5.4, messages 1 (user 1, assistant 0, tool 0, toolCalls 0), tokens ~4/128000 context (127996 remaining), retry off, file {path}",
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
    public async Task RunAsync_AuthSelectCommand_UsesSelectorAndRendersSelectedStatus()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("/auth select");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("openai", false, "none", false, true, "No credentials found.")
        };
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            authSelector: (_, _) => Task.FromResult<string?>("google"));

        await host.RunAsync();

        Assert.Empty(runner.Inputs);
        Assert.Contains("status> auth google: missing via none. No credentials found.", terminal.FlattenedText());
    }

    [Fact]
    public async Task RunAsync_LoginCommand_WithoutRegisteredProvider_ReportsError()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("/login anthropic");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("anthropic", false, "none", false, false, "No OAuth provider registered.")
        };
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner);

        await host.RunAsync();

        Assert.Empty(runner.Inputs);
        Assert.Contains("error> login anthropic: No OAuth login flow is registered", terminal.FlattenedText());
    }

    [Fact]
    public async Task RunAsync_LoginCommandWithSelector_RendersOAuthSuccess()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("/login");
        terminal.QueueInput("exit");

        var oauthProvider = new FakeOAuthProvider { Id = "google", Name = "Google" };
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("openai", false, "none", false, true, "No credentials found."),
            OAuthProvider = oauthProvider
        };
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            authSelector: (_, _) => Task.FromResult<string?>("google"));

        await host.RunAsync();

        Assert.Empty(runner.Inputs);
        Assert.Equal(1, oauthProvider.LoginCalls);
        var saved = runner.SavedOAuthCredentials;
        Assert.True(saved.HasValue);
        Assert.Equal("google", saved.Value.ProviderId);
        Assert.Contains(
            "status> login google: authenticated successfully. Credentials saved to auth.json.",
            terminal.FlattenedText());
    }

    [Fact]
    public async Task RunAsync_LogoutCommand_RendersStatusAndDoesNotInvokeRunner()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("/logout anthropic");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("anthropic", true, "auth.json oauth", true, true, "OAuth credentials found in auth.json."),
            LogoutResult = true
        };
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner);

        await host.RunAsync();

        Assert.Empty(runner.Inputs);
        Assert.Equal(["anthropic"], runner.LoggedOutProviders);
        Assert.Contains(
            "status> logout anthropic: auth.json credentials removed. Environment variables and models.json credentials are unchanged.",
            terminal.FlattenedText());
    }

    [Fact]
    public async Task RunAsync_LogoutCommandWithSelector_RendersOAuthLogoutSuccess()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("/logout");
        terminal.QueueInput("exit");

        var oauthProvider = new FakeOAuthProvider { Id = "google", Name = "Google" };
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            AuthStatus = new("openai", false, "none", false, false, "No credentials found."),
            OAuthProvider = oauthProvider,
            LogoutResult = true
        };
        runner.AuthStatuses["google"] = new("google", true, "auth.json oauth", true, true, "OAuth credentials found in auth.json.");
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            authSelector: (_, _) => Task.FromResult<string?>("google"));

        await host.RunAsync();

        Assert.Empty(runner.Inputs);
        Assert.Equal(["google"], runner.LoggedOutProviders);
        Assert.Contains(
            "status> logout google: auth.json credentials removed. Environment variables and models.json credentials are unchanged.",
            terminal.FlattenedText());
    }

    [Fact]
    public async Task RunAsync_ThinkingSelectCommand_UsesSelectorAndRendersStatus()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tau-coding-agent-host-thinking-{Guid.NewGuid():N}.json");
        var terminal = new FakeTerminal();
        terminal.QueueInput("/thinking select");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
        {
            ThinkingLevel = ThinkingLevel.Low
        };
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(null, null, DefaultThinkingLevel: "low"));
        var selectorStates = new List<CodingAgentThinkingSelectorState>();
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            settingsStore: settingsStore,
            thinkingSelector: (state, _) =>
            {
                selectorStates.Add(state);
                return Task.FromResult<string?>("medium");
            });

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            Assert.Single(selectorStates);
            Assert.Equal(ThinkingLevel.Low, selectorStates[0].CurrentLevel);
            Assert.Equal(ThinkingLevel.Medium, runner.ThinkingLevel);
            Assert.Equal("medium", settingsStore.Load().DefaultThinkingLevel);
            Assert.Contains("status> thinking: medium", terminal.FlattenedText());
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
    public async Task RunAsync_ChangelogCommand_RendersReleaseNotesAndDoesNotInvokeRunner()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-changelog-host-" + Guid.NewGuid().ToString("N"));
        var changelog = Path.Combine(directory, "feature-release-notes.md");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            changelog,
            """
            | 日期 | 功能域 | 用户价值 | 变更摘要 |
            | --- | --- | --- | --- |
            | 2026-05-21 | CodingAgent | 用户可以查看最近变更。 | 新增 /changelog baseline。 |
            """);
        var terminal = new FakeTerminal();
        terminal.QueueInput("/changelog");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var host = new CodingAgentHost(
            new InteractiveConsoleSession(terminal),
            runner,
            changelogStore: new CodingAgentChangelogStore(changelog));

        try
        {
            await host.RunAsync();

            Assert.Empty(runner.Inputs);
            var output = terminal.FlattenedText();
            Assert.Contains($"status> changelog: 1/1 entries from {changelog}", output, StringComparison.Ordinal);
            Assert.Contains("[1] 2026-05-21 CodingAgent", output, StringComparison.Ordinal);
            Assert.Contains("用户价值: 用户可以查看最近变更。", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
            var tree = CodingAgentTreeSessionController.OpenOrCreate(
                treePath,
                new CodingAgentCompactionRetentionOptions(20_000, 4));
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

    private static string MessageText(UserMessage message) =>
        Assert.IsType<TextContent>(Assert.Single(message.Content)).Text;

    private static string MessageText(AssistantMessage message) =>
        Assert.IsType<TextContent>(Assert.Single(message.Content)).Text;

    private static async IAsyncEnumerable<AgentEvent> RunUntilInputAsync(Task<string> inputTask)
    {
        await inputTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        yield return new AgentEndEvent();
    }

    private sealed class ScriptedKeyReader : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new();

        public void EnqueueRaw(ConsoleKeyInfo key) => _keys.Enqueue(key);

        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            if (_keys.Count == 0)
            {
                throw new InvalidOperationException("No more queued keys.");
            }

            return ValueTask.FromResult(_keys.Dequeue());
        }
    }

    private sealed class CapturingRenderer : IInteractiveRenderer
    {
        public int WindowWidth => 80;

        public void WritePrompt(string prompt, ConsoleColor? color = null) { }

        public void Render(string buffer, int cursorIndex) { }

        public void RenderSearch(string pattern, string? match, int cursorInMatch) { }

        public void Commit() { }

        public void Cancel() { }
    }

    private sealed class ScriptedTurnInputSource(params CodingAgentTurnInput[] inputs) : ICodingAgentTurnInputSource
    {
        public async IAsyncEnumerable<CodingAgentTurnInput> ReadInputsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var input in inputs)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return input;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The host cancels the source once the active runner turn ends.
            }
        }
    }
}
