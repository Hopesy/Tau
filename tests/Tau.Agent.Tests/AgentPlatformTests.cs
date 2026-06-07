using System.Globalization;
using System.Text.Json;
using Tau.Agent.Platform;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Faux;

namespace Tau.Agent.Tests;

public sealed class AgentPlatformTests
{
    [Fact]
    public async Task PromptAsync_RunsFauxProviderDelegateToolAndReturnsRunResult()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(registry);
        registration.SetResponses([
            Faux.AssistantMessage(
                [Faux.ToolCall("echo", new Dictionary<string, object?> { ["text"] = "hello" }, "call-1")],
                stopReason: StopReason.ToolUse),
            Faux.AssistantMessage("done")
        ]);
        var sink = new CapturingTauLogSink();
        var sessions = new InMemoryAgentSessionStore();

        var app = AgentApplication.CreateBuilder()
            .UseProviderRegistry(registry)
            .UseModel(registration.GetModel())
            .UseSystemPrompt("You are a focused agent.")
            .UseSessionId("session-1")
            .UseSessionStore(sessions)
            .UseLogSink(sink)
            .UseLogContext(new TauRuntimeLogContext("corr-1"))
            .AddMetadata("tenant", "test")
            .AddTool(
                "echo",
                "Echo",
                "Echoes text.",
                Schema("""
                {
                    "type": "object",
                    "properties": {
                        "text": { "type": "string" }
                    },
                    "required": ["text"]
                }
                """),
                async (context, _ct) =>
                {
                    await context.ReportUpdateAsync(new ToolUpdate("partial", [new TextContent("partial")]));
                    return new ToolResult([new TextContent(context.Arguments.GetProperty("text").GetString() ?? string.Empty)]);
                })
            .Build();

        var result = await app.PromptAsync("hello").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess);
        Assert.False(result.IsError);
        Assert.False(result.IsCancelled);
        Assert.True(result.SavedSession);
        Assert.Equal("session-1", result.SessionId);
        Assert.Equal("done", result.AssistantText);
        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.NotNull(result.Usage);
        Assert.Equal("corr-1", result.LogContext.CorrelationId);
        Assert.Equal("session-1", result.LogContext.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(result.LogContext.MessageId));

        Assert.Equal(4, result.Messages.Count);
        Assert.Equal(["agent_start", "turn_start"], result.Events.Take(2).Select(static evt => evt.Type).ToArray());
        var toolStart = Assert.Single(result.ToolStarts);
        Assert.Equal("call-1", toolStart.ToolCallId);
        Assert.Equal("echo", toolStart.ToolName);
        var toolUpdate = Assert.Single(result.Events.OfType<ToolExecutionUpdateEvent>());
        Assert.Equal("partial", ReadText(toolUpdate.PartialResult?.Content ?? []));
        var toolEnd = Assert.Single(result.ToolEnds);
        Assert.Equal("call-1", toolEnd.ToolCallId);
        Assert.False(toolEnd.IsError);

        var snapshot = sessions.Load("session-1");
        Assert.NotNull(snapshot);
        Assert.Equal(4, snapshot.Messages.Count);
        Assert.Equal("test", snapshot.Metadata["tenant"]);
    }

    [Fact]
    public async Task PromptAsync_PopulatesToolTraceContextWithoutArgumentsOrResultFields()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(registry);
        var model = registration.GetModel();
        registration.SetResponses([
            Faux.AssistantMessage(
                [Faux.ToolCall("echo", new Dictionary<string, object?> { ["text"] = "secret-ish" }, "call-log")],
                stopReason: StopReason.ToolUse),
            Faux.AssistantMessage("done")
        ]);
        var sink = new CapturingTauLogSink();

        var app = AgentApplication.CreateBuilder()
            .UseProviderRegistry(registry)
            .UseModel(model)
            .UseSessionId("session-log")
            .UseLogSink(sink)
            .UseStreamOptions(new SimpleStreamOptions
            {
                Transport = StreamTransport.WebSocket,
                Reasoning = ThinkingLevel.Low
            })
            .AddTool(
                "echo",
                "Echo",
                "Echoes text.",
                Schema("""{"type":"object"}"""),
                (context, _ct) => new ToolResult([new TextContent(context.Arguments.GetProperty("text").GetString() ?? string.Empty)]))
            .Build();

        var result = await app.PromptAsync("hello").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess);

        var providerStarts = sink.Events
            .Where(static evt => evt.Category == "provider" && evt.Event == "run.start")
            .ToArray();
        Assert.Equal(2, providerStarts.Length);
        var firstProviderStart = providerStarts[0];
        Assert.Equal(model.Provider, firstProviderStart.Fields["provider"]);
        Assert.Equal(model.Id, firstProviderStart.Fields["model"]);
        Assert.Equal(model.Api, firstProviderStart.Fields["api"]);
        Assert.Equal("1", firstProviderStart.Fields["messageCount"]);
        Assert.Equal("1", firstProviderStart.Fields["toolCount"]);
        Assert.Equal("websocket", firstProviderStart.Fields["transport"]);
        Assert.Equal("none", firstProviderStart.Fields["cacheRetention"]);
        Assert.Equal("low", firstProviderStart.Fields["reasoning"]);
        Assert.Equal("session-log", firstProviderStart.Fields["providerSessionId"]);
        Assert.Equal("session-log", firstProviderStart.Fields["sessionId"]);
        Assert.Equal(result.LogContext.CorrelationId, firstProviderStart.Fields["correlationId"]);
        Assert.Equal(result.LogContext.MessageId, firstProviderStart.Fields["messageId"]);
        Assert.DoesNotContain("hello", firstProviderStart.Fields.Values);
        Assert.DoesNotContain("secret-ish", firstProviderStart.Fields.Values);

        var providerEnds = sink.Events
            .Where(static evt => evt.Category == "provider" && evt.Event == "run.end")
            .ToArray();
        Assert.Equal(2, providerEnds.Length);
        var firstProviderEnd = providerEnds[0];
        Assert.Equal("true", firstProviderEnd.Fields["success"]);
        Assert.Equal("none", firstProviderEnd.Fields["failureKind"]);
        Assert.Equal("tooluse", firstProviderEnd.Fields["stopReason"]);
        Assert.True(firstProviderEnd.Fields.ContainsKey("durationMs"));
        Assert.True(ReadIntField(firstProviderEnd, "inputTokens") >= 0);
        Assert.True(ReadIntField(firstProviderEnd, "outputTokens") >= 0);
        Assert.True(ReadIntField(firstProviderEnd, "cacheReadTokens") >= 0);
        Assert.True(ReadIntField(firstProviderEnd, "cacheWriteTokens") >= 0);
        Assert.Equal(0m, ReadDecimalField(firstProviderEnd, "totalCost"));
        Assert.Equal("session-log", firstProviderEnd.Fields["sessionId"]);
        Assert.Equal(result.LogContext.CorrelationId, firstProviderEnd.Fields["correlationId"]);
        Assert.False(firstProviderEnd.Fields.ContainsKey("prompt"));
        Assert.False(firstProviderEnd.Fields.ContainsKey("messages"));
        Assert.False(firstProviderEnd.Fields.ContainsKey("arguments"));
        Assert.False(firstProviderEnd.Fields.ContainsKey("result"));
        Assert.DoesNotContain("hello", firstProviderEnd.Fields.Values);
        Assert.DoesNotContain("secret-ish", firstProviderEnd.Fields.Values);

        var finalProviderEnd = providerEnds[1];
        Assert.Equal("true", finalProviderEnd.Fields["success"]);
        Assert.Equal("none", finalProviderEnd.Fields["failureKind"]);
        Assert.Equal("endturn", finalProviderEnd.Fields["stopReason"]);

        var start = Assert.Single(sink.Events, static evt => evt.Event == "execution.start");
        Assert.Equal("tool", start.Category);
        Assert.Equal("call-log", start.Fields["toolCallId"]);
        Assert.Equal("echo", start.Fields["toolName"]);
        Assert.Equal("session-log", start.Fields["sessionId"]);
        Assert.Equal(result.LogContext.CorrelationId, start.Fields["correlationId"]);
        Assert.Equal(result.LogContext.MessageId, start.Fields["messageId"]);
        Assert.True(start.Fields.ContainsKey("argumentBytes"));
        Assert.False(start.Fields.ContainsKey("arguments"));
        Assert.DoesNotContain("secret-ish", start.Fields.Values);

        var end = Assert.Single(sink.Events, static evt => evt.Event == "execution.end");
        Assert.Equal("true", end.Fields["success"]);
        Assert.Equal("none", end.Fields["failureKind"]);
        Assert.Equal(result.LogContext.CorrelationId, end.Fields["correlationId"]);
        Assert.Equal("session-log", end.Fields["sessionId"]);
        Assert.Equal(result.LogContext.MessageId, end.Fields["messageId"]);
        Assert.True(end.Fields.ContainsKey("textBytes"));
        Assert.False(end.Fields.ContainsKey("result"));
        Assert.DoesNotContain("secret-ish", end.Fields.Values);
    }

    [Fact]
    public async Task Build_RestoresMessagesFromSessionStore()
    {
        var sessions = new InMemoryAgentSessionStore();
        sessions.Save(new AgentSessionSnapshot
        {
            SessionId = "session-restore",
            Messages = [new UserMessage("previous"), Faux.AssistantMessage("old answer")],
            Metadata = new Dictionary<string, string> { ["origin"] = "seed" }
        });
        var registry = new ProviderRegistry();
        var registration = Faux.Register(registry);
        registration.SetResponses([Faux.AssistantMessage("new answer")]);

        var app = AgentApplication.CreateBuilder()
            .UseProviderRegistry(registry)
            .UseModel(registration.GetModel())
            .UseSessionId("session-restore")
            .UseSessionStore(sessions)
            .AddMessage(new UserMessage("ignored initial"))
            .Build();

        Assert.Collection(
            app.State.Messages,
            message => Assert.Equal("previous", ReadText(Assert.IsType<UserMessage>(message).Content)),
            message => Assert.Equal("old answer", ReadText(Assert.IsType<AssistantMessage>(message).Content)));

        var result = await app.PromptAsync("next").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.SavedSession);
        Assert.Collection(
            result.Messages,
            message => Assert.Equal("previous", ReadText(Assert.IsType<UserMessage>(message).Content)),
            message => Assert.Equal("old answer", ReadText(Assert.IsType<AssistantMessage>(message).Content)),
            message => Assert.Equal("next", ReadText(Assert.IsType<UserMessage>(message).Content)),
            message => Assert.Equal("new answer", ReadText(Assert.IsType<AssistantMessage>(message).Content)));
        Assert.Equal("seed", sessions.Load("session-restore")?.Metadata["origin"]);
    }

    [Fact]
    public async Task PromptAsync_CancelledRunReturnsAbortedResultAndDoesNotOverwriteSession()
    {
        var sessions = new InMemoryAgentSessionStore();
        sessions.Save(new AgentSessionSnapshot
        {
            SessionId = "session-cancel",
            Messages = [new UserMessage("kept")]
        });
        var registry = new ProviderRegistry();
        var registration = Faux.Register(
            registry,
            new FauxProviderOptions { TokensPerSecond = 0.01, TokenSize = new FauxTokenSize(1, 1) });
        registration.SetResponses([Faux.AssistantMessage("this response should be cancelled before it completes")]);
        var sink = new CapturingTauLogSink();
        using var cancellationTokenSource = new CancellationTokenSource();

        var app = AgentApplication.CreateBuilder()
            .UseProviderRegistry(registry)
            .UseModel(registration.GetModel())
            .UseSessionId("session-cancel")
            .UseSessionStore(sessions)
            .UseLogSink(sink)
            .Build();

        var promptTask = app.PromptAsync("cancel me", cancellationTokenSource.Token);
        cancellationTokenSource.CancelAfter(TimeSpan.FromMilliseconds(50));
        var result = await promptTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.SavedSession);
        Assert.True(result.IsCancelled);
        Assert.Equal(StopReason.Aborted, result.StopReason);
        Assert.Equal("Operation canceled.", result.ErrorMessage);

        var providerEnd = Assert.Single(
            sink.Events,
            static evt => evt.Category == "provider" && evt.Event == "run.end");
        Assert.Equal("false", providerEnd.Fields["success"]);
        Assert.Equal("cancelled", providerEnd.Fields["failureKind"]);
        Assert.Equal("aborted", providerEnd.Fields["stopReason"]);
        Assert.Equal("session-cancel", providerEnd.Fields["sessionId"]);
        Assert.Equal(result.LogContext.CorrelationId, providerEnd.Fields["correlationId"]);
        Assert.Equal(result.LogContext.MessageId, providerEnd.Fields["messageId"]);
        Assert.True(providerEnd.Fields.ContainsKey("durationMs"));
        Assert.False(providerEnd.Fields.ContainsKey("prompt"));
        Assert.False(providerEnd.Fields.ContainsKey("messages"));
        Assert.DoesNotContain("cancel me", providerEnd.Fields.Values);
        Assert.DoesNotContain("this response should be cancelled before it completes", providerEnd.Fields.Values);

        var stateMessage = Assert.Single(app.State.Messages);
        Assert.Equal("kept", ReadText(Assert.IsType<UserMessage>(stateMessage).Content));
        Assert.Equal("Operation canceled.", app.State.ErrorMessage);

        var snapshot = sessions.Load("session-cancel");
        Assert.NotNull(snapshot);
        var kept = Assert.Single(snapshot.Messages);
        Assert.Equal("kept", ReadText(Assert.IsType<UserMessage>(kept).Content));
    }

    private static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ReadText(IReadOnlyList<ContentBlock> content) =>
        string.Join("\n", content.OfType<TextContent>().Select(static block => block.Text));

    private static int ReadIntField(TauLogEvent evt, string name) =>
        int.Parse(evt.Fields[name]!, CultureInfo.InvariantCulture);

    private static decimal ReadDecimalField(TauLogEvent evt, string name) =>
        decimal.Parse(evt.Fields[name]!, CultureInfo.InvariantCulture);

    private sealed class CapturingTauLogSink : ITauLogSink
    {
        public List<TauLogEvent> Events { get; } = [];

        public void Log(TauLogEvent evt) => Events.Add(evt);
    }
}
