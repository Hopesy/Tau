using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAi;

namespace Tau.Ai.Tests;

public sealed class OpenAiProviderSerializationTests
{
    [Fact]
    public async Task Stream_WithToolSchema_DoesNotFailOnListObjectSerialization()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-chat-completions",
            Provider = "openai",
            BaseUrl = "https://example.invalid/v1"
        };

        var toolSchema = """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string" }
          },
          "required": ["path"]
        }
        """;

        var context = new LlmContext
        {
            Messages =
            [
                new UserMessage([new TextContent("hello")])
            ],
            Tools =
            [
                new Tool("read_file", "Read a file", JsonDocument.Parse(toolSchema).RootElement.Clone())
            ]
        };

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });

        var events = new List<StreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Contains("stubbed-openai-error", error.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Unable to cast object of type", error.Error, StringComparison.Ordinal);
        Assert.NotNull(handler.CapturedBody);
        Assert.Contains("\"tools\"", handler.CapturedBody, StringComparison.Ordinal);
        Assert.Contains("\"function\"", handler.CapturedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_WhenSignalAlreadyCancelledTerminatesWithAbortedAssistant()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var stream = provider.Stream(
            new Model
            {
                Id = "gpt-5.4",
                Name = "GPT-5.4",
                Api = "openai-chat-completions",
                Provider = "openai",
                BaseUrl = "https://example.invalid/v1"
            },
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions { ApiKey = "test-key", Signal = cts.Token });
        var events = await DrainEventsAsync(stream);

        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Equal(StreamOptionHelpers.AbortedErrorMessage, error.Error);
        var response = await stream.ResultAsync;
        Assert.Equal(StopReason.Aborted, response.StopReason);
        Assert.Equal(StreamOptionHelpers.AbortedErrorMessage, response.ErrorMessage);
        Assert.Empty(handler.SawRequests);
    }

    [Fact]
    public async Task StreamSimple_WithOpenAiCompatibility_AdjustsRequestBody()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "glm-4.6",
            Name = "GLM-4.6",
            Api = "openai-chat-completions",
            Provider = "zai",
            BaseUrl = "https://api.z.ai/api/coding/paas/v4",
            Reasoning = true,
            Compat = new ModelCompatibility
            {
                SupportsStore = true,
                SupportsDeveloperRole = false,
                SupportsUsageInStreaming = false,
                MaxTokensField = "max_tokens",
                ThinkingFormat = "zai",
                ZaiToolStream = true,
                SupportsStrictMode = true
            }
        };

        var context = new LlmContext
        {
            SystemPrompt = "follow instructions",
            Messages =
            [
                new UserMessage(
                [
                    new TextContent("hello"),
                    new ImageContent("aGVsbG8=", "image/png")
                ])
            ],
            Tools =
            [
                new Tool("read_file", "Read a file", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone())
            ]
        };

        await DrainAsync(provider.StreamSimple(
            model,
            context,
            new SimpleStreamOptions
            {
                ApiKey = "test-key",
                MaxTokens = 42,
                Reasoning = ThinkingLevel.High
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("stream_options", out _));
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal(42, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("enable_thinking").GetBoolean());
        Assert.True(root.GetProperty("tool_stream").GetBoolean());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal(
            "hello(image omitted: model does not support images)",
            root.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.False(root.GetProperty("tools")[0].GetProperty("function").GetProperty("strict").GetBoolean());
    }

    [Fact]
    public async Task StreamSimple_WithRoutingCompatibility_AddsProviderRouting()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "openrouter/openai/gpt-5.4",
            Name = "GPT-5.4 via OpenRouter",
            Api = "openai-chat-completions",
            Provider = "openrouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            Reasoning = true,
            Compat = new ModelCompatibility
            {
                ThinkingFormat = "openrouter",
                MaxTokensField = "max_completion_tokens",
                ReasoningEffortMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["xhigh"] = "max"
                },
                OpenRouterRouting = new Dictionary<string, object>
                {
                    ["allow_fallbacks"] = false,
                    ["order"] = new[] { "anthropic", "amazon-bedrock" }
                }
            }
        };

        await DrainAsync(provider.StreamSimple(
            model,
            new LlmContext { Messages = [new UserMessage("route this")] },
            new SimpleStreamOptions
            {
                ApiKey = "test-key",
                MaxTokens = 100,
                Reasoning = ThinkingLevel.ExtraHigh
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.Equal(100, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("max", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(root.GetProperty("provider").GetProperty("allow_fallbacks").GetBoolean());
        Assert.Equal("anthropic", root.GetProperty("provider").GetProperty("order")[0].GetString());
    }

    [Fact]
    public async Task StreamSimple_WithDeepSeekThinkingFormat_AddsThinkingObject()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "deepseek-v4-pro",
            Name = "DeepSeek V4 Pro",
            Api = "openai-chat-completions",
            Provider = "deepseek",
            BaseUrl = "https://api.deepseek.com",
            Reasoning = true,
            Compat = new ModelCompatibility
            {
                SupportsStore = false,
                SupportsDeveloperRole = false,
                ThinkingFormat = "deepseek"
            }
        };

        await DrainAsync(provider.StreamSimple(
            model,
            new LlmContext { SystemPrompt = "be concise", Messages = [new UserMessage("think")] },
            new SimpleStreamOptions
            {
                ApiKey = "test-key",
                Reasoning = ThinkingLevel.High
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task StreamSimple_WithOpenAiCompatProviderCompatibility_AddsSessionAffinityAndSkipsTemperature()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "workers-ai/@cf/moonshotai/kimi-k2.6",
            Name = "Kimi K2.6",
            Api = "openai-chat-completions",
            Provider = "cloudflare-ai-gateway",
            BaseUrl = "https://gateway.ai.cloudflare.com/v1/account/gateway/compat",
            Reasoning = true,
            Compat = new ModelCompatibility
            {
                SendSessionAffinityHeaders = true,
                SupportsTemperature = false,
                MaxTokensField = "max_tokens"
            }
        };

        await DrainAsync(provider.StreamSimple(
            model,
            new LlmContext { Messages = [new UserMessage("hello")] },
            new SimpleStreamOptions
            {
                ApiKey = "test-key",
                SessionId = "session-456",
                Temperature = 0.7f,
                MaxTokens = 42
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.Equal(42, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.Equal("session-456", handler.SawRequests[0].Headers.GetValues("x-session-affinity").Single());
    }

    [Fact]
    public async Task StreamSimple_WithOpenAiBaseUrlAndShortCacheRetention_AddsPromptCacheKey()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        await DrainAsync(provider.StreamSimple(
            new Model
            {
                Id = "gpt-5.4",
                Name = "GPT-5.4",
                Api = "openai-chat-completions",
                Provider = "openai",
                BaseUrl = "https://api.openai.com/v1"
            },
            new LlmContext { Messages = [new UserMessage("hello")] },
            new SimpleStreamOptions
            {
                ApiKey = "test-key",
                CacheRetention = CacheRetention.Short,
                SessionId = $"{new string('a', 63)}😀extra"
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.Equal($"{new string('a', 63)}😀", root.GetProperty("prompt_cache_key").GetString());
        Assert.False(root.TryGetProperty("prompt_cache_retention", out _));
    }

    [Fact]
    public async Task StreamSimple_WithAnthropicCacheControlFormat_AddsCacheControl()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "anthropic/claude-sonnet-4.6",
            Name = "Claude Sonnet 4.6 via OpenRouter",
            Api = "openai-chat-completions",
            Provider = "openrouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            InputModalities = ["text", "image"],
            Compat = new ModelCompatibility
            {
                CacheControlFormat = "anthropic",
                ThinkingFormat = "openrouter"
            }
        };

        await DrainAsync(provider.StreamSimple(
            model,
            new LlmContext
            {
                SystemPrompt = "follow instructions",
                Messages =
                [
                    new UserMessage(
                    [
                        new TextContent("hello"),
                        new ImageContent("aGVsbG8=", "image/png")
                    ])
                ],
                Tools =
                [
                    new Tool("read_file", "Read a file", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone())
                ]
            },
            new SimpleStreamOptions
            {
                ApiKey = "test-key",
                CacheRetention = CacheRetention.Long,
                SessionId = "openrouter-cache-session"
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.Equal("openrouter-cache-session", root.GetProperty("prompt_cache_key").GetString());
        Assert.Equal("24h", root.GetProperty("prompt_cache_retention").GetString());

        var systemText = root.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("text", systemText.GetProperty("type").GetString());
        Assert.Equal("ephemeral", systemText.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("1h", systemText.GetProperty("cache_control").GetProperty("ttl").GetString());

        var userText = root.GetProperty("messages")[1].GetProperty("content")[0];
        Assert.Equal("text", userText.GetProperty("type").GetString());
        Assert.Equal("1h", userText.GetProperty("cache_control").GetProperty("ttl").GetString());

        var tool = root.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("1h", tool.GetProperty("cache_control").GetProperty("ttl").GetString());
    }

    [Fact]
    public async Task StreamSimple_WithCacheCompatibility_DisablesLongCacheTtl()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "compat-model",
            Name = "Compat Model",
            Api = "openai-chat-completions",
            Provider = "custom-openai",
            BaseUrl = "https://example.invalid/v1",
            Compat = new ModelCompatibility
            {
                CacheControlFormat = "anthropic",
                SupportsLongCacheRetention = false
            }
        };

        await DrainAsync(provider.StreamSimple(
            model,
            new LlmContext { Messages = [new UserMessage("hello")] },
            new SimpleStreamOptions
            {
                ApiKey = "test-key",
                CacheRetention = CacheRetention.Long,
                SessionId = "unsupported-cache-session"
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("prompt_cache_key", out _));
        Assert.False(root.TryGetProperty("prompt_cache_retention", out _));

        var userText = doc.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        var cacheControl = userText.GetProperty("cache_control");
        Assert.Equal("ephemeral", cacheControl.GetProperty("type").GetString());
        Assert.False(cacheControl.TryGetProperty("ttl", out _));
    }

    [Fact]
    public async Task StreamSimple_WithReasoningContentCompatibility_AddsAssistantReasoningContent()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "deepseek-v4-pro",
            Name = "DeepSeek V4 Pro",
            Api = "openai-chat-completions",
            Provider = "deepseek",
            BaseUrl = "https://api.deepseek.com",
            Reasoning = true,
            Compat = new ModelCompatibility
            {
                RequiresReasoningContentOnAssistantMessages = true,
                ThinkingFormat = "deepseek"
            }
        };

        await DrainAsync(provider.StreamSimple(
            model,
            new LlmContext
            {
                Messages =
                [
                    new AssistantMessage([new ToolCallContent("call_1", "lookup", "{}")])
                ]
            },
            new SimpleStreamOptions
            {
                ApiKey = "test-key",
                Reasoning = ThinkingLevel.High
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var assistant = doc.RootElement.GetProperty("messages")[0];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal(string.Empty, assistant.GetProperty("reasoning_content").GetString());
        Assert.Equal("lookup", assistant.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Stream_WithOpenAiOptions_WritesToolChoiceAndReasoningEffort()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-chat-completions",
            Provider = "openai",
            BaseUrl = "https://example.invalid/v1",
            Reasoning = true,
            Compat = new ModelCompatibility
            {
                SupportsReasoningEffort = true
            }
        };

        await DrainAsync(provider.Stream(
            model,
            new LlmContext
            {
                Messages = [new UserMessage("use tool")],
                Tools =
                [
                    new Tool("read_file", "Read a file", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone())
                ]
            },
            new OpenAiOptions
            {
                ApiKey = "test-key",
                ToolChoice = OpenAiToolChoice.Function("read_file"),
                ReasoningEffort = "low"
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.Equal("low", root.GetProperty("reasoning_effort").GetString());
        var toolChoice = root.GetProperty("tool_choice");
        Assert.Equal("function", toolChoice.GetProperty("type").GetString());
        Assert.Equal("read_file", toolChoice.GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Stream_AppliesPayloadCallbackAndResponseCallback()
    {
        using var handler = new StubHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("stubbed-openai-error", Encoding.UTF8, "text/plain"),
                Headers =
                {
                    { "x-request-id", "req_123" }
                }
            }
        };
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var callbacks = new List<(ProviderResponse Response, string ModelId)>();
        await DrainAsync(provider.Stream(
            new Model
            {
                Id = "gpt-5.4",
                Name = "GPT-5.4",
                Api = "openai-chat-completions",
                Provider = "openai",
                BaseUrl = "https://example.invalid/v1"
            },
            new LlmContext { Messages = [new UserMessage("original")] },
            new StreamOptions
            {
                ApiKey = "test-key",
                OnPayload = (payload, model) =>
                {
                    var body = Assert.IsType<Dictionary<string, object>>(payload);
                    Assert.Equal("gpt-5.4", body["model"]);
                    return ValueTask.FromResult<object?>(new Dictionary<string, object>
                    {
                        ["model"] = $"{model.Id}-payload",
                        ["stream"] = true,
                        ["messages"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["role"] = "user",
                                ["content"] = "replaced"
                            }
                        }
                    });
                },
                OnResponse = (response, model) =>
                {
                    callbacks.Add((response, model.Id));
                    return ValueTask.CompletedTask;
                }
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.Equal("gpt-5.4-payload", root.GetProperty("model").GetString());
        Assert.Equal("replaced", root.GetProperty("messages")[0].GetProperty("content").GetString());
        var callback = Assert.Single(callbacks);
        Assert.Equal(400, callback.Response.Status);
        Assert.Equal("req_123", callback.Response.Headers["x-request-id"]);
        Assert.Equal("gpt-5.4", callback.ModelId);
    }

    [Fact]
    public async Task Stream_WithVercelGatewayRouting_AddsProviderOptions()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "moonshotai/kimi-k2.5",
            Name = "Kimi K2.5",
            Api = "openai-chat-completions",
            Provider = "vercel-ai-gateway",
            BaseUrl = "https://ai-gateway.vercel.sh/v1",
            Compat = new ModelCompatibility
            {
                VercelGatewayRouting = new VercelGatewayRouting
                {
                    Only = ["fireworks"],
                    Order = ["fireworks", "novita"]
                }
            }
        };

        await DrainAsync(provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("route this")] },
            new StreamOptions { ApiKey = "test-key" }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var gateway = doc.RootElement.GetProperty("providerOptions").GetProperty("gateway");
        Assert.Equal("fireworks", gateway.GetProperty("only")[0].GetString());
        Assert.Equal("novita", gateway.GetProperty("order")[1].GetString());
    }

    [Fact]
    public async Task Stream_RemovesUnpairedSurrogatesFromChatCompletionsRequestText()
    {
        const char high = (char)0xD83D;
        const char low = (char)0xDE48;
        const string emoji = "🙈";
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);
        var model = new Model
        {
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-chat-completions",
            Provider = "openai",
            BaseUrl = "https://example.invalid/v1",
            Compat = new ModelCompatibility
            {
                RequiresThinkingAsText = true
            }
        };
        var context = new LlmContext
        {
            SystemPrompt = $"system {high} {emoji}",
            Messages =
            [
                new UserMessage($"user {high} {emoji}"),
                new AssistantMessage(
                [
                    new ThinkingContent($"thinking {low} {emoji}"),
                    new TextContent($"assistant {high} {emoji}"),
                    new ToolCallContent("call_1", "read_file", "{}")
                ]),
                new ToolResultMessage("call_1", [new TextContent($"tool {low} {emoji}")])
            ]
        };

        await DrainAsync(provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var allText = CollectJsonStrings(doc.RootElement);
        Assert.False(ContainsUnpairedSurrogate(allText));
        Assert.Contains("system  🙈", allText, StringComparison.Ordinal);
        Assert.Contains("user  🙈", allText, StringComparison.Ordinal);
        Assert.Contains("thinking  🙈", allText, StringComparison.Ordinal);
        Assert.Contains("assistant  🙈", allText, StringComparison.Ordinal);
        Assert.Contains("tool  🙈", allText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_WithToolResultNameCompatibility_AddsToolResultName()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);
        var model = new Model
        {
            Id = "compat-model",
            Name = "Compat Model",
            Api = "openai-chat-completions",
            Provider = "custom-openai",
            BaseUrl = "https://example.invalid/v1",
            Compat = new ModelCompatibility
            {
                RequiresToolResultName = true
            }
        };
        var context = new LlmContext(
            null,
            [
                new AssistantMessage([new ToolCallContent("call_1", "read_file", "{}")]),
                new ToolResultMessage("call_1", [new TextContent("done")]) { ToolName = "read_file" }
            ],
            null);

        await DrainAsync(provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var toolMessage = doc.RootElement.GetProperty("messages")[1];
        Assert.Equal("tool", toolMessage.GetProperty("role").GetString());
        Assert.Equal("read_file", toolMessage.GetProperty("name").GetString());
        Assert.Equal("call_1", toolMessage.GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task Stream_WithAssistantAfterToolResultCompatibility_InsertsBridgeAssistantBeforeUser()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);
        var model = new Model
        {
            Id = "compat-model",
            Name = "Compat Model",
            Api = "openai-chat-completions",
            Provider = "custom-openai",
            BaseUrl = "https://example.invalid/v1",
            Compat = new ModelCompatibility
            {
                RequiresAssistantAfterToolResult = true
            }
        };
        var context = new LlmContext(
            null,
            [
                new AssistantMessage([new ToolCallContent("call_1", "read_file", "{}")]),
                new ToolResultMessage("call_1", [new TextContent("done")]) { ToolName = "read_file" },
                new UserMessage("next question")
            ],
            null);

        await DrainAsync(provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal("I have processed the tool results.", messages[2].GetProperty("content").GetString());
        Assert.Equal("user", messages[3].GetProperty("role").GetString());
        Assert.Equal("next question", messages[3].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Stream_WithToolHistoryAndNoCurrentTools_EmitsEmptyToolsArray()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);
        var model = new Model
        {
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-chat-completions",
            Provider = "openai",
            BaseUrl = "https://example.invalid/v1"
        };
        var context = new LlmContext(
            null,
            [
                new UserMessage("use the tool"),
                new AssistantMessage([new ToolCallContent("call_1", "noop", "{}")]),
                new ToolResultMessage("call_1", [new TextContent("done")]) { ToolName = "noop" }
            ],
            []);

        await DrainAsync(provider.StreamSimple(model, context, new SimpleStreamOptions { ApiKey = "test-key" }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var tools = doc.RootElement.GetProperty("tools");
        Assert.Equal(JsonValueKind.Array, tools.ValueKind);
        Assert.Equal(0, tools.GetArrayLength());
    }

    [Fact]
    public async Task Stream_ParsesToolArgumentDeltasUsageAndContentIndexes()
    {
        using var handler = new StubHandler
        {
            Response = OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"id":"cmpl_1","choices":[{"delta":{"content":"checking"},"finish_reason":null}]}

                data: {"id":"cmpl_1","choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"README"}}]},"finish_reason":null}]}

                data: {"id":"cmpl_1","choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":".md\"}"}}]},"finish_reason":null}]}

                data: {"id":"cmpl_1","choices":[{"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":2}}

                data: [DONE]

                """)
        };
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var events = await DrainEventsAsync(provider.Stream(
            BuildOpenAiModel(),
            new LlmContext { Messages = [new UserMessage("read")] },
            new StreamOptions { ApiKey = "test-key" }));

        Assert.Equal(0, Assert.Single(events.OfType<TextStartEvent>()).ContentIndex);
        Assert.Equal(0, Assert.Single(events.OfType<TextDeltaEvent>()).ContentIndex);
        Assert.Equal(0, Assert.Single(events.OfType<TextEndEvent>()).ContentIndex);
        Assert.Equal(1, Assert.Single(events.OfType<ToolCallStartEvent>()).ContentIndex);
        Assert.All(events.OfType<ToolCallDeltaEvent>(), evt => Assert.Equal(1, evt.ContentIndex));
        Assert.Equal(1, Assert.Single(events.OfType<ToolCallEndEvent>()).ContentIndex);

        var firstDelta = Assert.IsType<ToolCallDeltaEvent>(events.First(evt => evt is ToolCallDeltaEvent));
        var partialToolCall = Assert.IsType<ToolCallContent>(firstDelta.Partial.Content[1]);
        Assert.Equal("""{"path":"README"}""", partialToolCall.Arguments);

        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal("cmpl_1", done.Message.ResponseId);
        Assert.Equal(new Usage(1, 2), done.Message.Usage);
        Assert.Equal(StopReason.ToolUse, done.Message.StopReason);
        Assert.Equal("checking", Assert.IsType<TextContent>(done.Message.Content[0]).Text);
        var toolCall = Assert.IsType<ToolCallContent>(done.Message.Content[1]);
        Assert.Equal("call_1", toolCall.Id);
        Assert.Equal("read_file", toolCall.Name);
        Assert.Equal("""{"path":"README.md"}""", toolCall.Arguments);
    }

    [Fact]
    public async Task Stream_UsesChoiceUsageFallback()
    {
        using var handler = new StubHandler
        {
            Response = OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"id":"cmpl_1","choices":[{"delta":{"content":"ok"},"finish_reason":null}]}

                data: {"id":"cmpl_1","choices":[{"delta":{},"finish_reason":"stop","usage":{"prompt_tokens":5,"completion_tokens":6}}]}

                """)
        };
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var events = await DrainEventsAsync(provider.Stream(
            BuildOpenAiModel(),
            new LlmContext { Messages = [new UserMessage("hello")] },
            new StreamOptions { ApiKey = "test-key" }));

        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal(new Usage(5, 6), done.Message.Usage);
        Assert.Equal(StopReason.EndTurn, done.Message.StopReason);
    }

    [Theory]
    [InlineData("content_filter", "Provider finish_reason: content_filter")]
    [InlineData("network_error", "Provider finish_reason: network_error")]
    [InlineData("unexpected_stop", "Provider finish_reason: unexpected_stop")]
    public async Task Stream_WithProviderErrorFinishReasonTerminatesWithErrorEvent(
        string finishReason,
        string expectedError)
    {
        using var handler = new StubHandler
        {
            Response = OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"id":"cmpl_1","choices":[{"delta":{"content":"blocked"},"finish_reason":null}]}

                data: {"id":"cmpl_1","choices":[{"delta":{},"finish_reason":"__FINISH_REASON__"}],"usage":{"prompt_tokens":1,"completion_tokens":0}}

                """.Replace("__FINISH_REASON__", finishReason, StringComparison.Ordinal))
        };
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);
        var stream = provider.Stream(
            BuildOpenAiModel(),
            new LlmContext { Messages = [new UserMessage("hello")] },
            new StreamOptions { ApiKey = "test-key" });

        var events = await DrainEventsAsync(stream);

        Assert.Empty(events.OfType<DoneEvent>());
        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Equal(expectedError, error.Error);
        Assert.Equal(StopReason.Error, error.Message?.StopReason);
        Assert.Equal(expectedError, error.Message?.ErrorMessage);
        Assert.Equal("blocked", Assert.IsType<TextContent>(Assert.Single(error.Message!.Content)).Text);
        var result = await stream.ResultAsync;
        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.Equal(expectedError, result.ErrorMessage);
        Assert.Equal(new Usage(1, 0), result.Usage);
    }

    [Fact]
    public async Task Stream_WithMalformedSseJsonTerminatesWithErrorEvent()
    {
        using var handler = new StubHandler
        {
            Response = OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"choices":[{"delta":{"content":"ok"},"finish_reason":null}]}

                data: {not-json}

                """)
        };
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);
        var stream = provider.Stream(
            BuildOpenAiModel(),
            new LlmContext { Messages = [new UserMessage("hello")] },
            new StreamOptions { ApiKey = "test-key" });

        var events = await DrainEventsAsync(stream);

        Assert.Empty(events.OfType<DoneEvent>());
        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.False(string.IsNullOrWhiteSpace(error.Error));
        var result = await stream.ResultAsync;
        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }
        public HttpResponseMessage? Response { get; set; }
        public List<HttpRequestMessage> SawRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SawRequests.Add(request);
            CapturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return Response ?? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("stubbed-openai-error", Encoding.UTF8, "text/plain")
            };
        }
    }

    private static async Task DrainAsync(IAsyncEnumerable<StreamEvent> stream)
    {
        await foreach (var _ in stream)
        {
        }
    }

    private static async Task<List<StreamEvent>> DrainEventsAsync(IAsyncEnumerable<StreamEvent> stream)
    {
        var events = new List<StreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        return events;
    }

    private static Model BuildOpenAiModel() => new()
    {
        Id = "gpt-5.4",
        Name = "GPT-5.4",
        Api = "openai-chat-completions",
        Provider = "openai",
        BaseUrl = "https://example.invalid/v1"
    };

    private static string CollectJsonStrings(JsonElement element)
    {
        var builder = new StringBuilder();
        AppendJsonStrings(element, builder);
        return builder.ToString();
    }

    private static void AppendJsonStrings(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                builder.AppendLine(element.GetString());
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AppendJsonStrings(property.Value, builder);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendJsonStrings(item, builder);
                }

                break;
        }
    }

    private static bool ContainsUnpairedSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (char.IsHighSurrogate(current))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }

                return true;
            }

            if (char.IsLowSurrogate(current))
            {
                return true;
            }
        }

        return false;
    }
}
