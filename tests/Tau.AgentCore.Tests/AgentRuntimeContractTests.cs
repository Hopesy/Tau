using System.Text.Json;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.AgentCore.Tests;

public sealed class AgentRuntimeContractTests
{
    [Fact]
    public async Task RunAsync_InvalidSchemaProducesToolResultAndCarriesTurnAndEndPayloads()
    {
        var tool = new RecordingTool(
            "echo",
            """
            {
                "type": "object",
                "properties": {
                    "text": { "type": "string" }
                },
                "required": ["text"]
            }
            """);
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("use tool"));

        var events = await CollectAsync(runtime.RunAsync(CreateConfig(
            new ScriptedProvider(
                new AssistantMessage([new ToolCallContent("tool-1", "echo", "{}")]),
                new AssistantMessage([new TextContent("done")])),
            [tool])));

        Assert.False(tool.Executed);
        var toolResult = Assert.Single(runtime.State.Messages.OfType<ToolResultMessage>());
        Assert.True(toolResult.IsError);
        var toolResultText = ReadText(toolResult);
        Assert.Contains("Validation failed for tool \"echo\"", toolResultText);
        Assert.Contains("text", toolResultText);
        Assert.Contains("Received arguments", toolResultText);

        var firstTurn = events.OfType<TurnEndEvent>().First();
        Assert.IsType<AssistantMessage>(firstTurn.Message);
        var turnToolResult = Assert.Single(firstTurn.ToolResults);
        Assert.True(turnToolResult.IsError);
        Assert.Equal("tool-1", turnToolResult.ToolCallId);

        var end = Assert.IsType<AgentEndEvent>(events.Last());
        Assert.Null(end.ErrorMessage);
        Assert.Collection(
            end.Messages,
            message => Assert.Equal("use tool", ReadText(Assert.IsType<UserMessage>(message))),
            message => Assert.IsType<AssistantMessage>(message),
            message => Assert.True(Assert.IsType<ToolResultMessage>(message).IsError),
            message => Assert.Equal("done", ReadText(Assert.IsType<AssistantMessage>(message))));
    }

    [Fact]
    public async Task RunStream_CompletesResultFromAgentEndMessages()
    {
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("hello"));

        var stream = runtime.RunStream(CreateConfig(
            new ScriptedProvider(new AssistantMessage([new TextContent("done")])),
            []));
        var events = await CollectAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));
        var result = await stream.ResultAsync.WaitAsync(TimeSpan.FromSeconds(5));

        var end = Assert.IsType<AgentEndEvent>(events.Last());
        Assert.Same(end.Messages[0], result[0]);
        Assert.Same(end.Messages[1], result[1]);
        Assert.Collection(
            result,
            message => Assert.Equal("hello", ReadText(Assert.IsType<UserMessage>(message))),
            message => Assert.Equal("done", ReadText(Assert.IsType<AssistantMessage>(message))));
    }

    [Fact]
    public async Task RunAsync_EnrichesAssistantUsageCostAndProviderMetadata()
    {
        var provider = new ScriptedProvider(new AssistantMessage([new TextContent("priced")])
        {
            Usage = new Usage(
                InputTokens: 1_000_000,
                OutputTokens: 2_000_000,
                CacheReadTokens: 500_000,
                CacheWriteTokens: 250_000,
                ServiceTier: "priority")
        });
        var model = new Model
        {
            Provider = "priced-provider",
            Id = "priced-model",
            Name = "Priced Model",
            Api = provider.Api,
            Cost = new ModelCost(2m, 8m, 0.5m, 1m)
        };
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("hello"));

        var events = await CollectAsync(runtime.RunAsync(CreateConfig(provider, [], model: model)));

        var assistant = Assert.IsType<AssistantMessage>(runtime.State.Messages.Last());
        Assert.Equal("priced-provider", assistant.Provider);
        Assert.Equal("priced-model", assistant.Model);
        Assert.Equal(provider.Api, assistant.Api);
        Assert.NotNull(assistant.Timestamp);
        Assert.NotNull(assistant.Usage);
        Assert.Equal("priority", assistant.Usage!.Value.ServiceTier);
        Assert.NotNull(assistant.Usage.Value.Cost);
        var cost = assistant.Usage.Value.Cost!.Value;
        Assert.Equal(4.0m, cost.Input);
        Assert.Equal(32.0m, cost.Output);
        Assert.Equal(0.5m, cost.CacheRead);
        Assert.Equal(0.5m, cost.CacheWrite);
        Assert.Equal(37.0m, cost.Total);

        Assert.Same(assistant, Assert.IsType<AssistantMessage>(events.OfType<MessageEndEvent>().Single().Message));
        Assert.Same(assistant, Assert.IsType<AssistantMessage>(events.OfType<TurnEndEvent>().Single().Message));
        Assert.Same(assistant, Assert.IsType<AssistantMessage>(events.OfType<AgentEndEvent>().Single().Messages.Last()));
    }

    [Fact]
    public async Task RunAsync_CancellationDuringAssistantStreamEmitsAbortedTurnAndAgentEnd()
    {
        var provider = new BlockingProvider();
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("hello"));
        using var cancellationTokenSource = new CancellationTokenSource();

        var eventsTask = CollectAsync(runtime.RunAsync(CreateConfig(provider, []), cancellationTokenSource.Token));
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellationTokenSource.Cancel();
        var events = await eventsTask.WaitAsync(TimeSpan.FromSeconds(5));

        var turnEnd = Assert.IsType<TurnEndEvent>(events[^2]);
        var agentEnd = Assert.IsType<AgentEndEvent>(events[^1]);
        var assistant = Assert.IsType<AssistantMessage>(turnEnd.Message);
        Assert.Equal("Operation canceled.", assistant.ErrorMessage);
        Assert.Equal(StopReason.Aborted, assistant.StopReason);
        Assert.Empty(turnEnd.ToolResults);
        Assert.Equal("Operation canceled.", agentEnd.ErrorMessage);
        Assert.Same(assistant, Assert.IsType<AssistantMessage>(agentEnd.Messages.Last()));
        Assert.Equal("Operation canceled.", runtime.State.ErrorMessage);
        Assert.False(runtime.State.IsStreaming);
    }

    [Fact]
    public async Task RunStream_CancellationCompletesWithAbortedAgentEndResult()
    {
        var provider = new BlockingProvider();
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("hello"));
        using var cancellationTokenSource = new CancellationTokenSource();

        var stream = runtime.RunStream(CreateConfig(provider, []), cancellationTokenSource.Token);
        var eventsTask = CollectAsync(stream);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellationTokenSource.Cancel();
        var events = await eventsTask.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await stream.ResultAsync.WaitAsync(TimeSpan.FromSeconds(5));

        var agentEnd = Assert.IsType<AgentEndEvent>(events.Last());
        Assert.Same(agentEnd.Messages[0], result[0]);
        Assert.Same(agentEnd.Messages[1], result[1]);
        var assistant = Assert.IsType<AssistantMessage>(result.Last());
        Assert.Equal("Operation canceled.", assistant.ErrorMessage);
        Assert.Equal(StopReason.Aborted, assistant.StopReason);
    }

    [Fact]
    public async Task RunAsync_ErrorEventWithAbortedPartialPreservesAbortedStopReason()
    {
        var partial = new AssistantMessage([new TextContent("partial")])
        {
            StopReason = StopReason.Aborted
        };
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("hello"));

        var events = await CollectAsync(runtime.RunAsync(CreateConfig(
            new EventProvider(new ErrorEvent("Operation canceled.", partial)),
            [])));

        var messageEnd = events.OfType<MessageEndEvent>().Single(evt => evt.Message is AssistantMessage);
        var assistant = Assert.IsType<AssistantMessage>(messageEnd.Message);
        Assert.Equal("Operation canceled.", assistant.ErrorMessage);
        Assert.Equal(StopReason.Aborted, assistant.StopReason);
        Assert.Equal("partial", ReadText(assistant));
        Assert.Same(assistant, Assert.IsType<AssistantMessage>(events.OfType<TurnEndEvent>().Single().Message));
        Assert.Same(assistant, Assert.IsType<AssistantMessage>(events.OfType<AgentEndEvent>().Single().Messages.Last()));
    }

    [Fact]
    public async Task RunAsync_TransformContextCancellationEmitsAbortedTurnAndSkipsProvider()
    {
        var provider = new UnexpectedProvider();
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("hello"));
        using var cancellationTokenSource = new CancellationTokenSource();
        var transformStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var config = CreateConfig(provider, []) with
        {
            TransformContextAsync = async (messages, ct) =>
            {
                transformStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return messages;
            }
        };

        var eventsTask = CollectAsync(runtime.RunAsync(config, cancellationTokenSource.Token));
        await transformStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellationTokenSource.Cancel();
        var events = await eventsTask.WaitAsync(TimeSpan.FromSeconds(5));

        var turnEnd = Assert.IsType<TurnEndEvent>(events[^2]);
        var agentEnd = Assert.IsType<AgentEndEvent>(events[^1]);
        var assistant = Assert.IsType<AssistantMessage>(turnEnd.Message);
        Assert.Equal("Operation canceled.", assistant.ErrorMessage);
        Assert.Equal(StopReason.Aborted, assistant.StopReason);
        Assert.Empty(turnEnd.ToolResults);
        Assert.Equal("Operation canceled.", agentEnd.ErrorMessage);
        Assert.Same(assistant, Assert.IsType<AssistantMessage>(agentEnd.Messages.Last()));
        Assert.Equal("Operation canceled.", runtime.State.ErrorMessage);
        Assert.False(runtime.State.IsStreaming);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task RunAsync_PreparesThenCoercesArgumentsBeforeExecute()
    {
        var tool = new RecordingTool(
            "count",
            """
            {
                "type": "object",
                "properties": {
                    "count": { "type": "integer" }
                },
                "required": ["count"]
            }
            """)
        {
            Prepare = _ => JsonDocument.Parse("""{"count":"42"}""").RootElement.Clone(),
            Execute = args =>
            {
                var count = args.GetProperty("count").GetInt32();
                return new ToolResult([new TextContent(count.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
            }
        };
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("use tool"));

        await CollectAsync(runtime.RunAsync(CreateConfig(
            new ScriptedProvider(
                new AssistantMessage([new ToolCallContent("tool-1", "count", """{"raw":"ignored"}""")]),
                new AssistantMessage([new TextContent("done")])),
            [tool])));

        Assert.True(tool.Executed);
        var toolResult = Assert.Single(runtime.State.Messages.OfType<ToolResultMessage>());
        Assert.False(toolResult.IsError);
        Assert.Equal("42", ReadText(toolResult));
    }

    [Fact]
    public async Task RunAsync_ToolInterceptorMutationUpdatesParallelExecutionArguments()
    {
        var observer = new ObservingInterceptor();
        var tool = new RecordingTool(
            "count",
            """
            {
                "type": "object",
                "properties": {
                    "count": { "type": "integer" }
                },
                "required": ["count"]
            }
            """)
        {
            ExecuteAsyncOverride = async (_, args, _, onUpdate) =>
            {
                if (onUpdate is not null)
                {
                    await onUpdate(new ToolUpdate("mutated")).ConfigureAwait(false);
                }

                return new ToolResult([new TextContent(args.GetProperty("count").GetString()!)]);
            }
        };
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("use tool"));

        var events = await CollectAsync(runtime.RunAsync(CreateConfig(
            new ScriptedProvider(
                new AssistantMessage([new ToolCallContent("tool-1", "count", """{"count":"42"}""")]),
                new AssistantMessage([new TextContent("done")])),
            [tool],
            [
                new MutatingInterceptor("""{"count":"mutated"}"""),
                observer
            ])));

        Assert.True(tool.Executed);
        Assert.True(observer.SeenArguments.HasValue);
        Assert.Equal("mutated", observer.SeenArguments.Value.GetProperty("count").GetString());
        var update = Assert.Single(events.OfType<ToolExecutionUpdateEvent>());
        Assert.Equal("""{"count":"mutated"}""", update.Args);
        var toolResult = Assert.Single(runtime.State.Messages.OfType<ToolResultMessage>());
        Assert.False(toolResult.IsError);
        Assert.Equal("mutated", ReadText(toolResult));
    }

    [Fact]
    public async Task RunAsync_ToolInterceptorMutationUpdatesSequentialExecutionArguments()
    {
        var tool = new RecordingTool(
            "count",
            """
            {
                "type": "object",
                "properties": {
                    "count": { "type": "integer" }
                },
                "required": ["count"]
            }
            """)
        {
            ExecutionMode = ToolExecutionMode.Sequential,
            Execute = args => new ToolResult([new TextContent(args.GetProperty("count").GetString()!)])
        };
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("use tool"));

        await CollectAsync(runtime.RunAsync(CreateConfig(
            new ScriptedProvider(
                new AssistantMessage([new ToolCallContent("tool-1", "count", """{"count":"42"}""")]),
                new AssistantMessage([new TextContent("done")])),
            [tool],
            [new MutatingInterceptor("""{"count":"sequential"}""")])));

        Assert.True(tool.Executed);
        var toolResult = Assert.Single(runtime.State.Messages.OfType<ToolResultMessage>());
        Assert.False(toolResult.IsError);
        Assert.Equal("sequential", ReadText(toolResult));
    }

    [Fact]
    public async Task RunAsync_ToolResultTerminateStopsProviderLoop()
    {
        var provider = new ScriptedProvider(
            new AssistantMessage([new ToolCallContent("tool-1", "stop", "{}")]),
            new AssistantMessage([new TextContent("unexpected follow-up")]));
        var tool = new RecordingTool("stop", """{"type":"object"}""")
        {
            Execute = _ => new ToolResult([new TextContent("terminal tool result")])
        };
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("use tool"));

        var events = await CollectAsync(runtime.RunAsync(CreateConfig(
            provider,
            [tool],
            [new TerminatingInterceptor()])));

        Assert.Equal(1, provider.Calls);
        var end = Assert.IsType<AgentEndEvent>(events.Last());
        Assert.Collection(
            end.Messages,
            message => Assert.Equal("use tool", ReadText(Assert.IsType<UserMessage>(message))),
            message => Assert.IsType<AssistantMessage>(message),
            message => Assert.Equal("terminal tool result", ReadText(Assert.IsType<ToolResultMessage>(message))));
        var toolEnd = Assert.Single(events.OfType<ToolExecutionEndEvent>());
        Assert.True(toolEnd.Result.Terminate);
        var turn = Assert.Single(events.OfType<TurnEndEvent>());
        Assert.Single(turn.ToolResults);
        Assert.DoesNotContain(
            runtime.State.Messages.OfType<AssistantMessage>(),
            message => ReadText(message).Equals("unexpected follow-up", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ShouldStopAfterTurnStopsBeforeQueuePolling()
    {
        var provider = new ScriptedProvider(new AssistantMessage([new TextContent("done")]));
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("hello"));
        var config = CreateConfig(provider, []) with
        {
            ShouldStopAfterTurnAsync = (_, _) =>
            {
                runtime.Steer(new UserMessage("late steer"));
                runtime.FollowUp(new UserMessage("late follow"));
                return Task.FromResult(true);
            }
        };

        var events = await CollectAsync(runtime.RunAsync(config));

        Assert.Equal(1, provider.Calls);
        Assert.Equal(2, runtime.PendingMessageCount);
        var end = Assert.IsType<AgentEndEvent>(events.Last());
        Assert.Collection(
            end.Messages,
            message => Assert.Equal("hello", ReadText(Assert.IsType<UserMessage>(message))),
            message => Assert.Equal("done", ReadText(Assert.IsType<AssistantMessage>(message))));
    }

    [Fact]
    public async Task RunAsync_PrepareNextTurnUpdatesNextProviderRequestState()
    {
        var provider = new RecordingTurnProvider(
            new AssistantMessage([new ToolCallContent("tool-1", "first", "{}")]),
            new AssistantMessage([new TextContent("done")]));
        var firstTool = new RecordingTool("first", """{"type":"object"}""")
        {
            Execute = _ => new ToolResult([new TextContent("first done")])
        };
        var secondTool = new RecordingTool("second", """{"type":"object"}""");
        var nextModel = new Model
        {
            Provider = "test-next",
            Id = "next-model",
            Name = "Next Model",
            Api = provider.Api,
            Reasoning = true
        };
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("use tool"));
        var config = CreateConfig(provider, [firstTool]) with
        {
            StreamOptions = new SimpleStreamOptions { Reasoning = ThinkingLevel.Low },
            PrepareNextTurnAsync = (_, _) => Task.FromResult<AgentLoopTurnUpdate?>(new(
                Model: nextModel,
                Tools: [secondTool],
                Reasoning: ThinkingLevel.High))
        };

        await CollectAsync(runtime.RunAsync(config));

        Assert.Equal(2, provider.Calls.Count);
        Assert.Equal("test-model", provider.Calls[0].ModelId);
        Assert.Equal(["first"], provider.Calls[0].ToolNames);
        Assert.Equal(ThinkingLevel.Low, provider.Calls[0].Reasoning);
        Assert.Equal("next-model", provider.Calls[1].ModelId);
        Assert.Equal(["second"], provider.Calls[1].ToolNames);
        Assert.Equal(ThinkingLevel.High, provider.Calls[1].Reasoning);
        Assert.Equal("next-model", runtime.State.Model?.Id);
        Assert.Equal(["second"], runtime.State.Tools.Select(static tool => tool.Name));
    }

    [Fact]
    public async Task RunAsync_ToolExecuteExceptionBecomesErrorToolResult()
    {
        var tool = new RecordingTool("explode", """{"type":"object"}""")
        {
            Execute = _ => throw new InvalidOperationException("boom")
        };

        var result = await ExecuteSingleToolAsync(tool);

        Assert.True(result.IsError);
        Assert.Contains("boom", ReadText(result));
    }

    [Fact]
    public async Task RunAsync_BeforeToolExceptionBecomesErrorToolResult()
    {
        var tool = new RecordingTool("echo", """{"type":"object"}""");

        var result = await ExecuteSingleToolAsync(
            tool,
            [new ThrowingInterceptor(before: true, message: "before boom")]);

        Assert.False(tool.Executed);
        Assert.True(result.IsError);
        Assert.Contains("before boom", ReadText(result));
    }

    [Fact]
    public async Task RunAsync_AfterToolExceptionBecomesErrorToolResult()
    {
        var tool = new RecordingTool("echo", """{"type":"object"}""");

        var result = await ExecuteSingleToolAsync(
            tool,
            [new ThrowingInterceptor(after: true, message: "after boom")]);

        Assert.True(tool.Executed);
        Assert.True(result.IsError);
        Assert.Contains("after boom", ReadText(result));
    }

    [Fact]
    public async Task RunAsync_ToolUpdateCallbackEmitsUpdateEventWithPartialResult()
    {
        var tool = new RecordingTool("progress", """{"type":"object"}""")
        {
            ExecuteAsyncOverride = async (_, args, _, onUpdate) =>
            {
                if (onUpdate is not null)
                {
                    await onUpdate(new ToolUpdate("half", [new TextContent("partial")])).ConfigureAwait(false);
                }

                return new ToolResult([new TextContent(args.GetProperty("text").GetString()!)]);
            }
        };
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("use tool"));

        var events = await CollectAsync(runtime.RunAsync(CreateConfig(
            new ScriptedProvider(
                new AssistantMessage([new ToolCallContent("tool-1", "progress", """{"text":"final"}""")]),
                new AssistantMessage([new TextContent("done")])),
            [tool])));

        var update = Assert.Single(events.OfType<ToolExecutionUpdateEvent>());
        Assert.Equal("tool-1", update.ToolCallId);
        Assert.Equal("progress", update.ToolName);
        Assert.Equal("""{"text":"final"}""", update.Args);
        Assert.Equal("half", update.Update.Text);
        Assert.NotNull(update.PartialResult);
        Assert.Equal("partial", ReadContent(update.PartialResult!.Content));

        var end = Assert.Single(events.OfType<ToolExecutionEndEvent>());
        Assert.Equal("progress", end.ToolName);
        Assert.False(end.IsError);
    }

    [Fact]
    public async Task RunAsync_ParallelModeEmitsAllRunnableStartsBeforeFirstEnd()
    {
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("use tools"));

        var events = await CollectAsync(runtime.RunAsync(CreateConfig(
            new ScriptedProvider(
                new AssistantMessage([
                    new ToolCallContent("tool-1", "first", "{}"),
                    new ToolCallContent("tool-2", "second", "{}")
                ]),
                new AssistantMessage([new TextContent("done")])),
            [
                new RecordingTool("first", """{"type":"object"}"""),
                new RecordingTool("second", """{"type":"object"}""")
            ])));

        var firstStartIndex = FindEventIndex<ToolExecutionStartEvent>(events, evt => evt.ToolName == "first");
        var secondStartIndex = FindEventIndex<ToolExecutionStartEvent>(events, evt => evt.ToolName == "second");
        var firstEndIndex = FindEventIndex<ToolExecutionEndEvent>(events, evt => evt.ToolName == "first");

        Assert.True(firstStartIndex < secondStartIndex);
        Assert.True(secondStartIndex < firstEndIndex);
    }

    [Fact]
    public async Task RunAsync_ParallelModeEmitsUpdatesWhileEarlierToolStillRunning()
    {
        var releaseFirstTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondToolUpdated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTool = new RecordingTool("first", """{"type":"object"}""")
        {
            ExecuteAsyncOverride = async (_, _, ct, _) =>
            {
                await releaseFirstTool.Task.WaitAsync(ct).ConfigureAwait(false);
                return new ToolResult([new TextContent("first done")]);
            }
        };
        var secondTool = new RecordingTool("second", """{"type":"object"}""")
        {
            ExecuteAsyncOverride = async (_, _, _, onUpdate) =>
            {
                if (onUpdate is not null)
                {
                    await onUpdate(new ToolUpdate("second partial", [new TextContent("second partial")]))
                        .ConfigureAwait(false);
                }

                secondToolUpdated.SetResult();
                return new ToolResult([new TextContent("second done")]);
            }
        };
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("use tools"));

        var eventsTask = CollectAsync(runtime.RunAsync(CreateConfig(
            new ScriptedProvider(
                new AssistantMessage([
                    new ToolCallContent("tool-1", "first", "{}"),
                    new ToolCallContent("tool-2", "second", "{}")
                ]),
                new AssistantMessage([new TextContent("done")])),
            [firstTool, secondTool])));

        await secondToolUpdated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseFirstTool.SetResult();
        var events = await eventsTask.WaitAsync(TimeSpan.FromSeconds(5));

        var secondUpdateIndex = FindEventIndex<ToolExecutionUpdateEvent>(events, evt => evt.ToolName == "second");
        var firstEndIndex = FindEventIndex<ToolExecutionEndEvent>(events, evt => evt.ToolName == "first");

        Assert.True(secondUpdateIndex < firstEndIndex);
    }

    [Fact]
    public async Task RunAsync_ToolExecuteCancellationEmitsCancelledToolResultAndClearsPendingToolCalls()
    {
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new RecordingTool("cancel", """{"type":"object"}""")
        {
            ExecuteAsyncOverride = async (_, _, ct, _) =>
            {
                toolStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return new ToolResult([new TextContent("unreachable")]);
            }
        };
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("use tool"));
        using var cancellationTokenSource = new CancellationTokenSource();

        var eventsTask = CollectAsync(runtime.RunAsync(CreateConfig(
            new ScriptedProvider(new AssistantMessage([new ToolCallContent("tool-1", "cancel", "{}")])),
            [tool]), cancellationTokenSource.Token));

        await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellationTokenSource.Cancel();
        var events = await eventsTask.WaitAsync(TimeSpan.FromSeconds(5));

        var end = Assert.Single(events.OfType<ToolExecutionEndEvent>());
        Assert.Equal("tool-1", end.ToolCallId);
        Assert.Equal("cancel", end.ToolName);
        Assert.True(end.IsError);
        Assert.Contains("Operation canceled.", ReadContent(end.Result.Content));

        var toolResult = Assert.Single(runtime.State.Messages.OfType<ToolResultMessage>());
        Assert.True(toolResult.IsError);
        Assert.Equal("tool-1", toolResult.ToolCallId);
        Assert.Equal("Operation canceled.", ReadText(toolResult));

        var turnEnd = Assert.Single(events.OfType<TurnEndEvent>());
        var turnToolResult = Assert.Single(turnEnd.ToolResults);
        Assert.Equal("tool-1", turnToolResult.ToolCallId);
        Assert.True(turnToolResult.IsError);

        Assert.Empty(runtime.State.PendingToolCalls);
        Assert.False(runtime.State.IsStreaming);
        Assert.IsType<AgentEndEvent>(events.Last());
    }

    [Fact]
    public async Task RunAsync_ParallelToolCancellationPreservesSiblingResultsInAssistantOrder()
    {
        var secondToolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTool = new RecordingTool("first", """{"type":"object"}""")
        {
            ExecuteAsyncOverride = (_, _, _, _) =>
                Task.FromResult(new ToolResult([new TextContent("first done")]))
        };
        var secondTool = new RecordingTool("second", """{"type":"object"}""")
        {
            ExecuteAsyncOverride = async (_, _, ct, _) =>
            {
                secondToolStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return new ToolResult([new TextContent("unreachable")]);
            }
        };
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("use tools"));
        using var cancellationTokenSource = new CancellationTokenSource();

        var eventsTask = CollectAsync(runtime.RunAsync(CreateConfig(
            new ScriptedProvider(new AssistantMessage([
                new ToolCallContent("tool-1", "first", "{}"),
                new ToolCallContent("tool-2", "second", "{}")
            ])),
            [firstTool, secondTool]), cancellationTokenSource.Token));

        await secondToolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellationTokenSource.Cancel();
        var events = await eventsTask.WaitAsync(TimeSpan.FromSeconds(5));

        var endEvents = events.OfType<ToolExecutionEndEvent>().ToArray();
        Assert.Collection(
            endEvents,
            first =>
            {
                Assert.Equal("tool-1", first.ToolCallId);
                Assert.Equal("first", first.ToolName);
                Assert.False(first.IsError);
                Assert.Equal("first done", ReadContent(first.Result.Content));
            },
            second =>
            {
                Assert.Equal("tool-2", second.ToolCallId);
                Assert.Equal("second", second.ToolName);
                Assert.True(second.IsError);
                Assert.Contains("Operation canceled.", ReadContent(second.Result.Content));
            });

        var turnEnd = Assert.Single(events.OfType<TurnEndEvent>());
        Assert.Collection(
            turnEnd.ToolResults,
            first =>
            {
                Assert.Equal("tool-1", first.ToolCallId);
                Assert.False(first.IsError);
                Assert.Equal("first done", ReadText(first));
            },
            second =>
            {
                Assert.Equal("tool-2", second.ToolCallId);
                Assert.True(second.IsError);
                Assert.Equal("Operation canceled.", ReadText(second));
            });

        Assert.Empty(runtime.State.PendingToolCalls);
        Assert.False(runtime.State.IsStreaming);
        Assert.IsType<AgentEndEvent>(events.Last());
    }

    private static async Task<ToolResultMessage> ExecuteSingleToolAsync(
        IAgentTool tool,
        IReadOnlyList<IToolInterceptor>? interceptors = null)
    {
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("use tool"));

        await CollectAsync(runtime.RunAsync(CreateConfig(
            new ScriptedProvider(
                new AssistantMessage([new ToolCallContent("tool-1", tool.Name, "{}")]),
                new AssistantMessage([new TextContent("done")])),
            [tool],
            interceptors ?? [])));

        return Assert.Single(runtime.State.Messages.OfType<ToolResultMessage>());
    }

    private static AgentLoopConfig CreateConfig(
        IStreamProvider provider,
        IReadOnlyList<IAgentTool> tools,
        IReadOnlyList<IToolInterceptor>? interceptors = null,
        Model? model = null)
    {
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, provider);

        return new AgentLoopConfig
        {
            Model = model ?? new Model
            {
                Provider = "test",
                Id = "test-model",
                Name = "Test Model",
                Api = provider.Api
            },
            ProviderRegistry = registry,
            Tools = tools,
            Interceptors = interceptors ?? []
        };
    }

    private static async Task<List<AgentEvent>> CollectAsync(IAsyncEnumerable<AgentEvent> events)
    {
        var collected = new List<AgentEvent>();
        await foreach (var evt in events)
        {
            collected.Add(evt);
        }

        return collected;
    }

    private static int FindEventIndex<TEvent>(
        IReadOnlyList<AgentEvent> events,
        Predicate<TEvent> predicate)
        where TEvent : AgentEvent
    {
        for (var index = 0; index < events.Count; index++)
        {
            if (events[index] is TEvent typed && predicate(typed))
            {
                return index;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected event {typeof(TEvent).Name} was not emitted.");
    }

    private static string ReadText(ChatMessage message) =>
        message switch
        {
            UserMessage user => ReadContent(user.Content),
            AssistantMessage assistant => ReadContent(assistant.Content),
            ToolResultMessage tool => ReadContent(tool.Content),
            _ => string.Empty
        };

    private static string ReadContent(IReadOnlyList<ContentBlock> content) =>
        string.Join("\n", content.OfType<TextContent>().Select(static block => block.Text));

    private sealed class ScriptedProvider(params AssistantMessage[] messages) : IStreamProvider
    {
        private int _calls;

        public string Api => "test-agent-contract";
        public int Calls => _calls;

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            Complete();

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            Complete();

        private AssistantMessageStream Complete()
        {
            var stream = new AssistantMessageStream();
            var message = _calls < messages.Length
                ? messages[_calls++]
                : new AssistantMessage([new TextContent("done")]);
            stream.Push(new DoneEvent(message));
            return stream;
        }
    }

    private sealed class RecordingTurnProvider(params AssistantMessage[] messages) : IStreamProvider
    {
        private int _calls;

        public string Api => "test-agent-contract-recording-turn";
        public List<ProviderTurnCall> Calls { get; } = [];

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            Complete(model, context, options as SimpleStreamOptions);

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            Complete(model, context, options);

        private AssistantMessageStream Complete(Model model, LlmContext context, SimpleStreamOptions? options)
        {
            Calls.Add(new ProviderTurnCall(
                model.Id,
                (context.Tools ?? []).Select(static tool => tool.Name).ToArray(),
                options?.Reasoning));
            var stream = new AssistantMessageStream();
            var message = _calls < messages.Length
                ? messages[_calls++]
                : new AssistantMessage([new TextContent("done")]);
            stream.Push(new DoneEvent(message));
            return stream;
        }
    }

    private sealed record ProviderTurnCall(
        string ModelId,
        IReadOnlyList<string> ToolNames,
        ThinkingLevel? Reasoning);

    private sealed class EventProvider(params StreamEvent[] events) : IStreamProvider
    {
        public string Api => "test-agent-contract-events";

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            Complete();

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            Complete();

        private AssistantMessageStream Complete()
        {
            var stream = new AssistantMessageStream();
            foreach (var evt in events)
            {
                stream.Push(evt);
            }

            return stream;
        }
    }

    private sealed class BlockingProvider : IStreamProvider
    {
        public string Api => "test-agent-contract-blocking";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            CreateStream();

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            CreateStream();

        private AssistantMessageStream CreateStream()
        {
            var stream = new AssistantMessageStream();
            Started.TrySetResult();
            return stream;
        }
    }

    private sealed class UnexpectedProvider : IStreamProvider
    {
        public string Api => "test-agent-contract-unexpected";
        public int Calls { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            Calls++;
            throw new InvalidOperationException("Provider should not be called.");
        }

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
        {
            Calls++;
            throw new InvalidOperationException("Provider should not be called.");
        }
    }

    private sealed class RecordingTool(string name, string schemaJson) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string Description => name;
        public JsonElement ParameterSchema { get; } = JsonDocument.Parse(schemaJson).RootElement.Clone();
        public ToolExecutionMode ExecutionMode { get; init; } = ToolExecutionMode.Parallel;
        public bool Executed { get; private set; }
        public Func<JsonElement, JsonElement>? Prepare { get; init; }
        public Func<JsonElement, ToolResult>? Execute { get; init; }
        public Func<string, JsonElement, CancellationToken, Func<ToolUpdate, Task>?, Task<ToolResult>>? ExecuteAsyncOverride { get; init; }

        public ValueTask<JsonElement> PrepareArgumentsAsync(JsonElement rawArgs, CancellationToken ct = default) =>
            new(Prepare?.Invoke(rawArgs) ?? rawArgs);

        public Task<ToolResult> ExecuteAsync(
            string toolCallId,
            JsonElement args,
            CancellationToken ct = default,
            Func<ToolUpdate, Task>? onUpdate = null)
        {
            Executed = true;
            if (ExecuteAsyncOverride is not null)
            {
                return ExecuteAsyncOverride(toolCallId, args, ct, onUpdate);
            }

            return Task.FromResult(Execute?.Invoke(args) ?? new ToolResult([new TextContent("ok")]));
        }
    }

    private sealed class MutatingInterceptor(string argumentsJson) : IToolInterceptor
    {
        public Task<ToolCallDecision> BeforeToolCallAsync(ToolCallContext context, CancellationToken ct = default)
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return Task.FromResult(ToolCallDecision.AllowWithArguments(document.RootElement));
        }
    }

    private sealed class ObservingInterceptor : IToolInterceptor
    {
        public JsonElement? SeenArguments { get; private set; }

        public Task<ToolCallDecision> BeforeToolCallAsync(ToolCallContext context, CancellationToken ct = default)
        {
            SeenArguments = context.Arguments.Clone();
            return Task.FromResult(ToolCallDecision.Allow);
        }
    }

    private sealed class ThrowingInterceptor(bool before = false, bool after = false, string message = "boom") : IToolInterceptor
    {
        public Task<ToolCallDecision> BeforeToolCallAsync(ToolCallContext context, CancellationToken ct = default)
        {
            if (before)
            {
                throw new InvalidOperationException(message);
            }

            return Task.FromResult(ToolCallDecision.Allow);
        }

        public Task<ToolResult> AfterToolCallAsync(ToolCallContext context, ToolResult result, CancellationToken ct = default)
        {
            if (after)
            {
                throw new InvalidOperationException(message);
            }

            return Task.FromResult(result);
        }
    }

    private sealed class TerminatingInterceptor : IToolInterceptor
    {
        public Task<ToolResult> AfterToolCallAsync(ToolCallContext context, ToolResult result, CancellationToken ct = default) =>
            Task.FromResult(result with { Terminate = true });
    }
}
