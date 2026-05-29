using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Faux;

namespace Tau.Ai.Tests;

public sealed class FauxProviderTests
{
    [Fact]
    public async Task Register_ProvidesDefaultModelAndEstimatesUsage()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(registry);
        registration.SetResponses([Faux.AssistantMessage("hello world")]);

        var response = await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            new LlmContext(
                SystemPrompt: "Be concise.",
                Messages: [new UserMessage("hi there")],
                Tools: null),
            new StreamOptions());

        Assert.Equal("hello world", ReadText(response.Content));
        Assert.True(response.Usage?.InputTokens > 0);
        Assert.True(response.Usage?.OutputTokens > 0);
        Assert.Equal(1, registration.State.CallCount);
        Assert.Equal(registration.GetModel().Api, registration.Api);
        Assert.Null(registration.GetModel("missing"));
    }

    [Fact]
    public async Task Helpers_CreateThinkingTextAndToolCallAssistantMessages()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(registry);
        registration.SetResponses([
            Faux.AssistantMessage(
                [
                    Faux.Thinking("think"),
                    Faux.ToolCall("echo", new Dictionary<string, object?> { ["text"] = "hi" }),
                    Faux.Text("done")
                ],
                stopReason: StopReason.ToolUse)
        ]);

        var response = await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            new LlmContext(null, [new UserMessage("hi")], null),
            new StreamOptions());

        Assert.Collection(
            response.Content,
            block => Assert.Equal("think", Assert.IsType<ThinkingContent>(block).Thinking),
            block =>
            {
                var toolCall = Assert.IsType<ToolCallContent>(block);
                Assert.Equal("echo", toolCall.Name);
                Assert.Equal("hi", JsonDocument.Parse(toolCall.Arguments).RootElement.GetProperty("text").GetString());
            },
            block => Assert.Equal("done", Assert.IsType<TextContent>(block).Text));
        Assert.Equal(StopReason.ToolUse, response.StopReason);
    }

    [Fact]
    public async Task Register_SupportsMultipleModelsAndModelAwareFactories()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(
            registry,
            new FauxProviderOptions
            {
                Models =
                [
                    new FauxModelDefinition { Id = "faux-fast", Name = "Faux Fast" },
                    new FauxModelDefinition { Id = "faux-thinker", Name = "Faux Thinker", Reasoning = true }
                ]
            });
        registration.SetResponses([
            FauxResponseStep.FromFactory((_context, _options, _state, model) =>
                ValueTask.FromResult(Faux.AssistantMessage($"{model.Id}:{model.Reasoning}"))),
            FauxResponseStep.FromFactory((_context, _options, _state, model) =>
                ValueTask.FromResult(Faux.AssistantMessage($"{model.Id}:{model.Reasoning}")))
        ]);

        Assert.Equal(["faux-fast", "faux-thinker"], registration.Models.Select(static model => model.Id).ToArray());
        Assert.Same(registration.Models[0], registration.GetModel());
        Assert.False(registration.GetModel("faux-fast")?.Reasoning);
        Assert.True(registration.GetModel("faux-thinker")?.Reasoning);

        var fast = await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel("faux-fast")!,
            new LlmContext(null, [new UserMessage("hi")], null),
            new StreamOptions());
        var thinker = await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel("faux-thinker")!,
            new LlmContext(null, [new UserMessage("hi")], null),
            new StreamOptions());

        Assert.Equal("faux-fast:False", ReadText(fast.Content));
        Assert.Equal("faux-thinker:True", ReadText(thinker.Content));
    }

    [Fact]
    public async Task Complete_RewritesApiProviderAndModel()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(
            registry,
            new FauxProviderOptions
            {
                Api = "faux:test",
                Provider = "faux-provider",
                Models = [new FauxModelDefinition { Id = "faux-model" }]
            });
        registration.SetResponses([Faux.AssistantMessage("hello")]);

        var response = await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            new LlmContext(null, [new UserMessage("hi")], null),
            new StreamOptions());

        Assert.Equal("faux:test", response.Api);
        Assert.Equal("faux-provider", response.Provider);
        Assert.Equal("faux-model", response.Model);
    }

    [Fact]
    public async Task Responses_AreConsumedInOrderAndExhaustionReturnsErrorAssistant()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(registry);
        registration.SetResponses([Faux.AssistantMessage("first"), Faux.AssistantMessage("second")]);
        var context = new LlmContext(null, [new UserMessage("hi")], null);

        var first = await StreamFunctions.CompleteAsync(registry, registration.GetModel(), context, new StreamOptions());
        var second = await StreamFunctions.CompleteAsync(registry, registration.GetModel(), context, new StreamOptions());
        var exhausted = await StreamFunctions.CompleteAsync(registry, registration.GetModel(), context, new StreamOptions());

        Assert.Equal("first", ReadText(first.Content));
        Assert.Equal("second", ReadText(second.Content));
        Assert.Equal(StopReason.Error, exhausted.StopReason);
        Assert.Equal("No more faux responses queued", exhausted.ErrorMessage);
        Assert.Equal("faux-1", exhausted.Model);
        Assert.Equal(0, registration.GetPendingResponseCount());
        Assert.Equal(3, registration.State.CallCount);
    }

    [Fact]
    public async Task Responses_CanBeReplacedAndAppended()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(registry);
        var context = new LlmContext(null, [new UserMessage("hi")], null);

        registration.SetResponses([Faux.AssistantMessage("first")]);
        Assert.Equal("first", ReadText((await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            context,
            new StreamOptions())).Content));
        Assert.Equal(0, registration.GetPendingResponseCount());

        registration.SetResponses([Faux.AssistantMessage("second")]);
        Assert.Equal(1, registration.GetPendingResponseCount());
        Assert.Equal("second", ReadText((await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            context,
            new StreamOptions())).Content));

        registration.AppendResponses([Faux.AssistantMessage("third"), Faux.AssistantMessage("fourth")]);
        Assert.Equal(2, registration.GetPendingResponseCount());
        Assert.Equal("third", ReadText((await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            context,
            new StreamOptions())).Content));
        Assert.Equal("fourth", ReadText((await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            context,
            new StreamOptions())).Content));
    }

    [Fact]
    public async Task Factory_CanReadContextStateAndCanReturnAsyncResponse()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(registry);
        registration.SetResponses([
            FauxResponseStep.FromFactory(async (context, _options, state, _model) =>
            {
                await Task.Yield();
                return Faux.AssistantMessage($"{context.Messages.Count}:{state.CallCount}");
            })
        ]);

        var response = await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            new LlmContext(null, [new UserMessage("hi")], null),
            new StreamOptions());

        Assert.Equal("1:1", ReadText(response.Content));
    }

    [Fact]
    public async Task FactoryThrow_EmitsErrorEventAndResult()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(registry);
        registration.SetResponses([
            FauxResponseStep.FromFactory((_context, _options, _state, _model) =>
                ValueTask.FromException<AssistantMessage>(new InvalidOperationException("boom")))
        ]);

        var stream = StreamFunctions.Stream(
            registry,
            registration.GetModel(),
            new LlmContext(null, [new UserMessage("hi")], null),
            new StreamOptions());
        var events = await CollectEventsAsync(stream);
        var response = await stream.ResultAsync;

        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Equal("boom", error.Error);
        Assert.Equal(StopReason.Error, response.StopReason);
        Assert.Equal("boom", response.ErrorMessage);
    }

    [Fact]
    public async Task Usage_SimulatesPromptCachingPerSession()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(registry);
        registration.SetResponses([Faux.AssistantMessage("first"), Faux.AssistantMessage("second"), Faux.AssistantMessage("third")]);
        var context = new LlmContext("Be concise.", [new UserMessage("hello")], null);

        var first = await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            context,
            new StreamOptions { SessionId = "session-1", CacheRetention = CacheRetention.Short });
        Assert.Equal(0, first.Usage?.CacheReadTokens);
        Assert.True(first.Usage?.CacheWriteTokens > 0);

        context = context with
        {
            Messages = [new UserMessage("hello"), first, new UserMessage("follow up")]
        };
        var second = await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            context,
            new StreamOptions { SessionId = "session-1", CacheRetention = CacheRetention.Short });
        var third = await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            context,
            new StreamOptions { SessionId = "session-1", CacheRetention = CacheRetention.None });

        Assert.True(second.Usage?.CacheReadTokens > 0);
        Assert.True(second.Usage?.CacheWriteTokens > 0);
        Assert.Equal(0, third.Usage?.CacheReadTokens);
        Assert.Equal(0, third.Usage?.CacheWriteTokens);
    }

    [Fact]
    public async Task Stream_EmitsExactNestedOrderForFixedSizeChunks()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(
            registry,
            new FauxProviderOptions { TokenSize = new FauxTokenSize(Min: 1, Max: 1) });
        registration.SetResponses([
            Faux.AssistantMessage(
                [
                    Faux.Thinking("go"),
                    Faux.Text("ok"),
                    Faux.ToolCall("echo", "{}")
                ],
                stopReason: StopReason.ToolUse)
        ]);

        var events = await CollectEventsAsync(StreamFunctions.Stream(
            registry,
            registration.GetModel(),
            new LlmContext(null, [new UserMessage("hi")], null),
            new StreamOptions()));

        Assert.Equal(
            [
                "start",
                "thinking_start",
                "thinking_delta",
                "thinking_end",
                "text_start",
                "text_delta",
                "text_end",
                "toolcall_start",
                "toolcall_delta",
                "toolcall_end",
                "done"
            ],
            events.Select(static evt => evt.Type).ToArray());
    }

    [Fact]
    public async Task Stream_SplitsToolCallArgumentsAcrossDeltas()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(
            registry,
            new FauxProviderOptions { TokenSize = new FauxTokenSize(Min: 1, Max: 1) });
        registration.SetResponses([
            Faux.AssistantMessage(
                [Faux.ToolCall("echo", new Dictionary<string, object?> { ["text"] = "hello world", ["count"] = 12 })],
                stopReason: StopReason.ToolUse)
        ]);

        var events = await CollectEventsAsync(StreamFunctions.Stream(
            registry,
            registration.GetModel(),
            new LlmContext(null, [new UserMessage("hi")], null),
            new StreamOptions()));

        var toolCallJson = string.Concat(events.OfType<ToolCallDeltaEvent>().Select(static evt => evt.Delta));
        var root = JsonDocument.Parse(toolCallJson).RootElement;
        Assert.Equal("hello world", root.GetProperty("text").GetString());
        Assert.Equal(12, root.GetProperty("count").GetInt32());
        Assert.Single(events.OfType<ToolCallStartEvent>());
        Assert.Single(events.OfType<ToolCallEndEvent>());
    }

    [Fact]
    public async Task Stream_SupportsMultipleToolCallsInOneMessage()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(registry);
        registration.SetResponses([
            Faux.AssistantMessage(
                [
                    Faux.ToolCall("echo", """{"text":"one"}""", id: "tool-1"),
                    Faux.ToolCall("echo", """{"text":"two"}""", id: "tool-2")
                ],
                stopReason: StopReason.ToolUse)
        ]);

        var events = await CollectEventsAsync(StreamFunctions.Stream(
            registry,
            registration.GetModel(),
            new LlmContext(null, [new UserMessage("hi")], null),
            new StreamOptions()));

        Assert.Equal(2, events.OfType<ToolCallStartEvent>().Count());
        Assert.Equal(2, events.OfType<ToolCallEndEvent>().Count());
    }

    [Fact]
    public async Task Usage_DoesNotShareCacheAcrossSessions()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(registry);
        registration.SetResponses([Faux.AssistantMessage("first"), Faux.AssistantMessage("second"), Faux.AssistantMessage("third")]);
        var context = new LlmContext(null, [new UserMessage("hello")], null);

        var first = await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            context,
            new StreamOptions { SessionId = "session-1", CacheRetention = CacheRetention.Short });
        Assert.True(first.Usage?.CacheWriteTokens > 0);

        context = context with
        {
            Messages = [new UserMessage("hello"), first, new UserMessage("follow up")]
        };
        var second = await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            context,
            new StreamOptions { SessionId = "session-2", CacheRetention = CacheRetention.Short });
        var third = await StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            context,
            new StreamOptions());

        Assert.Equal(0, second.Usage?.CacheReadTokens);
        Assert.True(second.Usage?.CacheWriteTokens > 0);
        Assert.Equal(0, third.Usage?.CacheReadTokens);
        Assert.Equal(0, third.Usage?.CacheWriteTokens);
    }

    [Theory]
    [InlineData(StopReason.Error, "upstream failed")]
    [InlineData(StopReason.Aborted, "Request was aborted")]
    public async Task Stream_ExplicitTerminalAssistantErrorEndsWithErrorEvent(
        StopReason stopReason,
        string errorMessage)
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(
            registry,
            new FauxProviderOptions { TokenSize = new FauxTokenSize(Min: 2, Max: 2) });
        registration.SetResponses([
            Faux.AssistantMessage("partial", stopReason: stopReason, errorMessage: errorMessage)
        ]);

        var stream = StreamFunctions.Stream(
            registry,
            registration.GetModel(),
            new LlmContext(null, [new UserMessage("hi")], null),
            new StreamOptions());
        var events = await CollectEventsAsync(stream);
        var response = await stream.ResultAsync;

        Assert.Equal(["start", "text_start", "text_delta", "text_end", "error"], events.Select(static evt => evt.Type).ToArray());
        Assert.Equal(stopReason, response.StopReason);
        Assert.Equal(errorMessage, response.ErrorMessage);
    }

    [Fact]
    public async Task Unregister_RemovesProviderFromRegistry()
    {
        var registry = new ProviderRegistry();
        var registration = Faux.Register(registry);
        registration.SetResponses([Faux.AssistantMessage("hello")]);

        registration.Unregister();

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => StreamFunctions.CompleteAsync(
            registry,
            registration.GetModel(),
            new LlmContext(null, [new UserMessage("hi")], null),
            new StreamOptions()));
        Assert.Equal($"No provider registered for API '{registration.Api}'.", ex.Message);
    }

    private static string ReadText(IEnumerable<ContentBlock> content) =>
        string.Join("\n", content.OfType<TextContent>().Select(static block => block.Text));

    private static async Task<List<StreamEvent>> CollectEventsAsync(IAsyncEnumerable<StreamEvent> stream)
    {
        var events = new List<StreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        return events;
    }
}
