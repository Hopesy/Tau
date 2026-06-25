using System.Net;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers.Anthropic;

namespace Tau.Ai.Tests;

public sealed class AnthropicProviderTests
{
    [Fact]
    public async Task Stream_AddsAnthropicProviderSpecificOptions()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(reasoning: true),
            new LlmContext { Messages = [new UserMessage("think")], Tools = [BuildTool()] },
            new AnthropicOptions
            {
                ApiKey = "anthropic-key",
                ThinkingEnabled = true,
                ThinkingBudgetTokens = 1234,
                ThinkingDisplay = "omitted",
                InterleavedThinking = true,
                ToolChoice = AnthropicToolChoice.Tool("read_file"),
                Temperature = 0.8f,
                Metadata = new Dictionary<string, object>
                {
                    ["user_id"] = "user-123",
                    ["ignored"] = "ignored"
                }
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("temperature", out _));

        var thinking = root.GetProperty("thinking");
        Assert.Equal("enabled", thinking.GetProperty("type").GetString());
        Assert.Equal(1234, thinking.GetProperty("budget_tokens").GetInt32());
        Assert.Equal("omitted", thinking.GetProperty("display").GetString());

        var toolChoice = root.GetProperty("tool_choice");
        Assert.Equal("tool", toolChoice.GetProperty("type").GetString());
        Assert.Equal("read_file", toolChoice.GetProperty("name").GetString());
        Assert.Equal("user-123", root.GetProperty("metadata").GetProperty("user_id").GetString());
        Assert.True(handler.Requests[0].Headers.TryGetValues("anthropic-beta", out var betaValues));
        Assert.Equal("interleaved-thinking-2025-05-14", Assert.Single(betaValues));
    }

    [Fact]
    public async Task Stream_AddsAdaptiveThinkingEffortForSupportedModel()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel("claude-opus-4-7-20260101", reasoning: true),
            new LlmContext { Messages = [new UserMessage("think")] },
            new AnthropicOptions
            {
                ApiKey = "anthropic-key",
                ThinkingEnabled = true,
                Effort = "xhigh",
                ThinkingDisplay = "summarized",
                InterleavedThinking = true
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        var thinking = root.GetProperty("thinking");
        Assert.Equal("adaptive", thinking.GetProperty("type").GetString());
        Assert.Equal("summarized", thinking.GetProperty("display").GetString());
        Assert.Equal("xhigh", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(handler.Requests[0].Headers.Contains("anthropic-beta"));
    }

    [Fact]
    public async Task Stream_WithForceAdaptiveThinkingFalse_UsesLegacyThinkingForAdaptiveModelId()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);
        var model = BuildModel("claude-opus-4-7-20260101", reasoning: true) with
        {
            Compat = new ModelCompatibility { ForceAdaptiveThinking = false }
        };

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("think")] },
            new AnthropicOptions
            {
                ApiKey = "anthropic-key",
                ThinkingEnabled = true,
                ThinkingBudgetTokens = 2048,
                Effort = "medium",
                ThinkingDisplay = "summarized"
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        var thinking = root.GetProperty("thinking");
        Assert.Equal("enabled", thinking.GetProperty("type").GetString());
        Assert.Equal(2048, thinking.GetProperty("budget_tokens").GetInt32());
        Assert.Equal("summarized", thinking.GetProperty("display").GetString());
        Assert.False(root.TryGetProperty("output_config", out _));
    }

    [Fact]
    public async Task Stream_WithMixedProviderCompatibility_AddsSessionAffinityAndForcesAdaptiveThinking()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        var model = BuildModel("accounts/fireworks/models/kimi-k2p6", reasoning: true) with
        {
            Provider = "fireworks",
            BaseUrl = "https://api.fireworks.ai/inference",
            Compat = new ModelCompatibility
            {
                SendSessionAffinityHeaders = true,
                ForceAdaptiveThinking = true,
                SupportsTemperature = false
            }
        };

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("think")] },
            new AnthropicOptions
            {
                ApiKey = "fireworks-key",
                SessionId = "session-123",
                ThinkingEnabled = true,
                Effort = "high",
                Temperature = 0.8f,
                InterleavedThinking = true
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(handler.Requests[0].Headers.Contains("anthropic-beta"));
        Assert.Equal("session-123", handler.Requests[0].Headers.GetValues("x-session-affinity").Single());
    }

    [Fact]
    public async Task Stream_WithLongCacheRetention_AddsAnthropicCacheControl()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(reasoning: true),
            new LlmContext
            {
                SystemPrompt = "system rules",
                Messages =
                [
                    new AssistantMessage([new ToolCallContent("toolu_1", "read_file", """{"path":"README.md"}""")]),
                    new ToolResultMessage("toolu_1", [new TextContent("file body")])
                ],
                Tools = [BuildTool()]
            },
            new StreamOptions
            {
                ApiKey = "anthropic-key",
                CacheRetention = CacheRetention.Long
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        var systemCache = root.GetProperty("system")[0].GetProperty("cache_control");
        Assert.Equal("ephemeral", systemCache.GetProperty("type").GetString());
        Assert.Equal("1h", systemCache.GetProperty("ttl").GetString());

        var tool = root.GetProperty("tools")[0];
        Assert.True(tool.GetProperty("eager_input_streaming").GetBoolean());
        Assert.Equal("1h", tool.GetProperty("cache_control").GetProperty("ttl").GetString());

        var toolResult = root.GetProperty("messages").EnumerateArray().Last().GetProperty("content")[0];
        Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
        Assert.Equal("1h", toolResult.GetProperty("cache_control").GetProperty("ttl").GetString());
    }

    [Fact]
    public async Task Stream_WithCacheCompatibility_DisablesLongTtlToolCacheAndEagerStreaming()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        var model = BuildModel("accounts/fireworks/models/kimi-k2p6", reasoning: true) with
        {
            Provider = "fireworks",
            BaseUrl = "https://api.fireworks.ai/inference",
            Compat = new ModelCompatibility
            {
                SupportsLongCacheRetention = false,
                SupportsCacheControlOnTools = false,
                SupportsEagerToolInputStreaming = false
            }
        };

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            new LlmContext
            {
                SystemPrompt = "system rules",
                Messages = [new UserMessage("hello")],
                Tools = [BuildTool()]
            },
            new StreamOptions
            {
                ApiKey = "fireworks-key",
                CacheRetention = CacheRetention.Long
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        var systemCache = root.GetProperty("system")[0].GetProperty("cache_control");
        Assert.Equal("ephemeral", systemCache.GetProperty("type").GetString());
        Assert.False(systemCache.TryGetProperty("ttl", out _));

        var tool = root.GetProperty("tools")[0];
        Assert.False(tool.TryGetProperty("eager_input_streaming", out _));
        Assert.False(tool.TryGetProperty("cache_control", out _));
        Assert.Equal(
            "fine-grained-tool-streaming-2025-05-14",
            handler.Requests[0].Headers.GetValues("anthropic-beta").Single());

        var userMessage = root.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("ephemeral", userMessage.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.False(userMessage.GetProperty("cache_control").TryGetProperty("ttl", out _));
    }

    [Fact]
    public async Task Stream_WithLegacyToolStreamingCompatibilityAndNoTools_DoesNotSendBetaHeader()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);
        var model = BuildModel(reasoning: true) with
        {
            Compat = new ModelCompatibility { SupportsEagerToolInputStreaming = false }
        };

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("hello")] },
            new StreamOptions { ApiKey = "anthropic-key" }));

        Assert.False(handler.Requests[0].Headers.Contains("anthropic-beta"));
    }

    [Fact]
    public async Task Stream_WithEmptyThinkingSignature_ConvertsThinkingToTextByDefault()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(reasoning: true),
            new LlmContext
            {
                Messages =
                [
                    new UserMessage("first"),
                    new AssistantMessage([new ThinkingContent("internal reasoning") { ThinkingSignature = "" }]),
                    new UserMessage("second")
                ]
            },
            new StreamOptions { ApiKey = "anthropic-key" }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var assistant = doc.RootElement.GetProperty("messages")[1];
        var content = assistant.GetProperty("content")[0];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.Equal("internal reasoning", content.GetProperty("text").GetString());
        Assert.False(content.TryGetProperty("signature", out _));
    }

    [Fact]
    public async Task Stream_WithAllowEmptySignature_PreservesEmptyThinkingSignature()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);
        var model = BuildModel(reasoning: true) with
        {
            Provider = "xiaomi-token-plan-ams",
            Compat = new ModelCompatibility { AllowEmptySignature = true }
        };

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            new LlmContext
            {
                Messages =
                [
                    new UserMessage("first"),
                    new AssistantMessage([new ThinkingContent("internal reasoning") { ThinkingSignature = " " }]),
                    new UserMessage("second")
                ]
            },
            new StreamOptions { ApiKey = "anthropic-key" }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var assistant = doc.RootElement.GetProperty("messages")[1];
        var content = assistant.GetProperty("content")[0];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("thinking", content.GetProperty("type").GetString());
        Assert.Equal("internal reasoning", content.GetProperty("thinking").GetString());
        Assert.Equal(string.Empty, content.GetProperty("signature").GetString());
    }

    [Fact]
    public async Task Stream_AddsDisabledThinkingAndAllowsTemperature()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(reasoning: true),
            new LlmContext { Messages = [new UserMessage("no think")] },
            new AnthropicOptions
            {
                ApiKey = "anthropic-key",
                ThinkingEnabled = false,
                Temperature = 0.8f,
                ToolChoice = "auto"
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(0.8f, root.GetProperty("temperature").GetSingle());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Stream_WithDisabledThinkingUnsupported_OmitsDisabledThinking()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);
        var model = BuildModel("claude-fable-5", reasoning: true) with
        {
            Compat = new ModelCompatibility
            {
                ForceAdaptiveThinking = true,
                SupportsDisabledThinking = false
            }
        };

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("no think")] },
            new AnthropicOptions
            {
                ApiKey = "anthropic-key",
                ThinkingEnabled = false,
                Temperature = 0.8f
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.Equal(0.8f, root.GetProperty("temperature").GetSingle());
        Assert.False(root.TryGetProperty("output_config", out _));
    }

    [Fact]
    public async Task StreamSimple_UsesConfiguredThinkingBudget()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.StreamSimple(
            BuildModel(reasoning: true),
            new LlmContext { Messages = [new UserMessage("think")] },
            new SimpleStreamOptions
            {
                ApiKey = "anthropic-key",
                Reasoning = ThinkingLevel.High,
                ThinkingBudgets = new ThinkingBudgets { High = 2222 }
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var thinking = doc.RootElement.GetProperty("thinking");
        Assert.Equal("enabled", thinking.GetProperty("type").GetString());
        Assert.Equal(2222, thinking.GetProperty("budget_tokens").GetInt32());
        Assert.Equal("summarized", thinking.GetProperty("display").GetString());
    }

    [Fact]
    public async Task StreamSimple_WithForcedAdaptiveThinking_AddsReasoningEffortOutputConfig()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);
        var model = BuildModel("vendor--claude-opus-latest", reasoning: true) with
        {
            Compat = new ModelCompatibility { ForceAdaptiveThinking = true }
        };

        await OpenAiResponsesProviderTests.CollectAsync(provider.StreamSimple(
            model,
            new LlmContext { Messages = [new UserMessage("think")] },
            new SimpleStreamOptions
            {
                ApiKey = "anthropic-key",
                Reasoning = ThinkingLevel.Medium
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("summarized", root.GetProperty("thinking").GetProperty("display").GetString());
        Assert.Equal("medium", root.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task StreamSimple_WithAdaptiveThinking_UsesReasoningEffortMap()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);
        var model = BuildModel("claude-fable-5", reasoning: true) with
        {
            Compat = new ModelCompatibility
            {
                ForceAdaptiveThinking = true,
                ReasoningEffortMap = new Dictionary<string, string> { ["xhigh"] = "xhigh" }
            }
        };

        await OpenAiResponsesProviderTests.CollectAsync(provider.StreamSimple(
            model,
            new LlmContext { Messages = [new UserMessage("think")] },
            new SimpleStreamOptions
            {
                ApiKey = "anthropic-key",
                Reasoning = ThinkingLevel.ExtraHigh
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("xhigh", root.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task Stream_TranslatesAnthropicStreamEventsWithContentIndexUsageAndToolArguments()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
            """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":10,"output_tokens":0,"cache_read_input_tokens":2}}}

            event: content_block_start
            data: {"type":"content_block_start","index":2,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"hello"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":2}

            event: content_block_start
            data: {"type":"content_block_start","index":5,"content_block":{"type":"thinking","thinking":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":5,"delta":{"type":"thinking_delta","thinking":"plan"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":5,"delta":{"type":"signature_delta","signature":"sig-"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":5,"delta":{"type":"signature_delta","signature":"tail"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":5}

            event: content_block_start
            data: {"type":"content_block_start","index":9,"content_block":{"type":"tool_use","id":"toolu_1","name":"read_file","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":9,"delta":{"type":"input_json_delta","partial_json":"{\"path\":\"README"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":9,"delta":{"type":"input_json_delta","partial_json":".md\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":9}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":4,"cache_creation_input_tokens":3}}

            event: message_stop
            data: {"type":"message_stop"}

            """));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(reasoning: true),
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions { ApiKey = "anthropic-key" }));

        Assert.Contains(events, evt => evt is StartEvent { Partial.ResponseId: "msg_1" });
        Assert.Contains(events, evt => evt is TextStartEvent { ContentIndex: 0 });
        Assert.Contains(events, evt => evt is ThinkingStartEvent { ContentIndex: 1 });
        Assert.Contains(events, evt => evt is ToolCallStartEvent { ContentIndex: 2 });
        Assert.Contains(events, evt => evt is ToolCallDeltaEvent { ContentIndex: 2, Delta: ".md\"}" });
        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal("msg_1", done.Message.ResponseId);
        Assert.Equal(StopReason.ToolUse, done.Message.StopReason);
        Assert.Equal(new Usage(10, 4, 2, 3), done.Message.Usage);
        Assert.Collection(
            done.Message.Content,
            block => Assert.Equal("hello", Assert.IsType<TextContent>(block).Text),
            block =>
            {
                var thinking = Assert.IsType<ThinkingContent>(block);
                Assert.Equal("plan", thinking.Thinking);
                Assert.Equal("sig-tail", thinking.ThinkingSignature);
            },
            block =>
            {
                var tool = Assert.IsType<ToolCallContent>(block);
                Assert.Equal("toolu_1", tool.Id);
                Assert.Equal("read_file", tool.Name);
                Assert.Equal("""{"path":"README.md"}""", tool.Arguments);
            });
    }

    [Fact]
    public async Task Stream_PreservesRedactedThinkingData()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
            """
            data: {"type":"message_start","message":{"id":"msg_redacted","usage":{"input_tokens":1,"output_tokens":0}}}

            data: {"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"opaque-signature"}}

            data: {"type":"content_block_stop","index":0}

            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}

            data: {"type":"message_stop"}

            """));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(reasoning: true),
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions { ApiKey = "anthropic-key" }));

        var done = Assert.Single(events.OfType<DoneEvent>());
        var thinking = Assert.IsType<ThinkingContent>(Assert.Single(done.Message.Content));
        Assert.True(thinking.Redacted);
        Assert.Equal("[Reasoning redacted]", thinking.Thinking);
        Assert.Equal("opaque-signature", thinking.ThinkingSignature);
    }

    [Theory]
    [InlineData("refusal", "Provider stop_reason: refusal")]
    [InlineData("sensitive", "Provider stop_reason: sensitive")]
    [InlineData("future_reason", "Unhandled Anthropic stop_reason: future_reason")]
    public async Task Stream_WithProviderErrorStopReasonTerminatesWithErrorEvent(string stopReason, string expectedError)
    {
        var sse =
            """
            data: {"type":"message_start","message":{"id":"msg_error","usage":{"input_tokens":1,"output_tokens":0}}}

            data: {"type":"message_delta","delta":{"stop_reason":"__STOP_REASON__"},"usage":{"output_tokens":0}}

            data: {"type":"message_stop"}

            """.Replace("__STOP_REASON__", stopReason, StringComparison.Ordinal);
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(sse));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        var stream = provider.Stream(
            BuildModel(),
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions { ApiKey = "anthropic-key" });
        var events = await OpenAiResponsesProviderTests.CollectAsync(stream);

        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Equal(expectedError, error.Error);
        Assert.Equal(StopReason.Error, error.Message?.StopReason);
        Assert.Equal(expectedError, error.Message?.ErrorMessage);
        Assert.Empty(events.OfType<DoneEvent>());
        var result = await stream.ResultAsync;
        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.Equal(expectedError, result.ErrorMessage);
    }

    [Fact]
    public async Task Stream_WithMalformedSseJsonTerminatesWithErrorEvent()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
            """
            data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":1,"output_tokens":0}}}

            data: {"type":"content_block_delta","index":0,"delta":

            data: {"type":"message_stop"}

            """));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        var stream = provider.Stream(
            BuildModel(),
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions { ApiKey = "anthropic-key" });
        var events = await OpenAiResponsesProviderTests.CollectAsync(stream);

        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.StartsWith("Malformed Anthropic SSE JSON:", error.Error, StringComparison.Ordinal);
        Assert.Equal("msg_1", error.Message?.ResponseId);
        Assert.Equal(StopReason.Error, error.Message?.StopReason);
        var result = await stream.ResultAsync;
        Assert.Equal("msg_1", result.ResponseId);
        Assert.Equal(StopReason.Error, result.StopReason);
    }

    private static Model BuildModel(string id = "claude-sonnet-4-5-20250929", bool reasoning = false) => new()
    {
        Id = id,
        Name = "Claude",
        Api = "anthropic-messages",
        Provider = "anthropic",
        BaseUrl = "https://api.anthropic.com",
        Reasoning = reasoning,
        MaxOutputTokens = 4096
    };

    private static Tool BuildTool() => new(
        "read_file",
        "Read a file.",
        JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""").RootElement);
}
