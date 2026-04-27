# OpenAI Responses Port Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Tau.Ai's `openai-responses` and `openai-codex-responses` chat-completions fallback with native Responses-protocol providers that can replay message history, stream reasoning/text/tool-call events, and preserve Codex-specific request semantics over SSE.

**Architecture:** Add a focused `OpenAiResponses` provider folder with one shared helper for message conversion, SSE event translation, tool-call id handling, JWT payload decoding, and stop-reason mapping. Build two thin providers on top of that shared layer: one for standard OpenAI `/responses`, one for ChatGPT Codex `/codex/responses` with Codex-only headers and retry behavior; then switch builtin registration, add tests first, and update docs/history only after code is green.

**Tech Stack:** C# / .NET 10, `HttpClient`, `System.Text.Json` source generation, existing `AssistantMessageStream` + `SseParser`, xUnit.

---

## File map

### Create
- `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesShared.cs` — shared conversion, SSE event parsing/translation, helper option types, JWT decoding, Codex event normalization, stop-reason mapping.
- `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesProvider.cs` — `/responses` provider.
- `src/Tau.Ai/Providers/OpenAiResponses/OpenAiCodexResponsesProvider.cs` — `/codex/responses` provider with retry + headers.
- `tests/Tau.Ai.Tests/OpenAiResponsesSharedTests.cs` — pure helper tests for id encoding/decoding, JWT decoding, stop-reason mapping, request-shape conversion.
- `tests/Tau.Ai.Tests/OpenAiResponsesProviderTests.cs` — standard responses SSE integration tests with stub handler.
- `tests/Tau.Ai.Tests/OpenAiCodexResponsesProviderTests.cs` — Codex SSE integration tests with stub handler, header assertions, retry behavior.
- `docs/histories/2026-04/20260427-openai-responses-port.md` — change history for this implementation round.

### Modify
- `src/Tau.Ai/Providers/BuiltInProviders.cs` — swap builtin registrations for `openai-responses` and `openai-codex-responses`.
- `docs/ARCHITECTURE.md` — reflect new native Responses providers.
- `docs/QUALITY_SCORE.md` — mark this fidelity gap closed and record remaining ones.
- `next.md` — move remaining follow-up items (Codex websocket, pricing, Copilot headers) into the backlog.
- `tests/Tau.Ai.Tests/BuiltInProvidersTests.cs` — strengthen assertions from API presence to provider type.

### Existing reference files to read while implementing
- `src/Tau.Ai/Providers/OpenAi/OpenAiProvider.cs`
- `src/Tau.Ai/Providers/OpenAi/OpenAiMessageConverter.cs`
- `src/Tau.Ai/Providers/OpenAi/OpenAiStreamParser.cs`
- `src/Tau.Ai/Providers/OpenAiCompat/OpenAiCompatibleProvider.cs`
- `src/Tau.Ai/Abstractions/Messages.cs`
- `src/Tau.Ai/Abstractions/ContentBlocks.cs`
- `src/Tau.Ai/Abstractions/StreamEvents.cs`
- `src/Tau.Ai/Abstractions/Options.cs`
- `pi-mono-main/packages/ai/src/providers/openai-responses.ts`
- `pi-mono-main/packages/ai/src/providers/openai-codex-responses.ts`
- `pi-mono-main/packages/ai/src/providers/openai-responses-shared.ts`
- `pi-mono-main/packages/ai/src/providers/transform-messages.ts`

---

### Task 1: Add failing helper tests for shared Responses behavior

**Files:**
- Create: `tests/Tau.Ai.Tests/OpenAiResponsesSharedTests.cs`
- Reference: `src/Tau.Ai/Abstractions/Messages.cs`
- Reference: `src/Tau.Ai/Abstractions/ContentBlocks.cs`
- Reference: `src/Tau.Ai/Abstractions/Options.cs`

- [ ] **Step 1: Write the failing test file for tool-call id round-trip, JWT decode, and stop-reason mapping**

```csharp
using Tau.Ai;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

public sealed class OpenAiResponsesSharedTests
{
    [Fact]
    public void BuildFunctionCallItem_SplitsCompoundToolCallId()
    {
        var item = OpenAiResponsesShared.BuildFunctionCallItem(
            new ToolCallContent("call_123|fc_456", "read_file", "{\"path\":\"README.md\"}"));

        Assert.Equal("call_123", item.CallId);
        Assert.Equal("fc_456", item.ItemId);
        Assert.Equal("read_file", item.Name);
        Assert.Equal("{\"path\":\"README.md\"}", item.ArgumentsJson);
    }

    [Fact]
    public void DecodeCodexAccountId_ReadsUrlSafeJwtPayload()
    {
        const string token = "eyJhbGciOiJub25lIn0.eyJodHRwczovL2FwaS5vcGVuYWkuY29tL2F1dGgiOnsiY2hhdGdwdF9hY2NvdW50X2lkIjoiYWNjdF90ZXN0In19.";

        var accountId = OpenAiResponsesShared.DecodeCodexAccountId(token);

        Assert.Equal("acct_test", accountId);
    }

    [Theory]
    [InlineData("completed", false, StopReason.EndTurn)]
    [InlineData("completed", true, StopReason.ToolUse)]
    [InlineData("incomplete", false, StopReason.MaxTokens)]
    [InlineData("failed", false, StopReason.Error)]
    [InlineData("queued", true, StopReason.ToolUse)]
    public void MapStopReason_UsesStatusAndToolCallPresence(string status, bool hasToolCall, StopReason expected)
    {
        var blocks = hasToolCall
            ? new ContentBlock[] { new ToolCallContent("call_1|fc_1", "read_file", "{}") }
            : new ContentBlock[] { new TextContent("done") };

        var actual = OpenAiResponsesShared.MapStopReason(status, blocks);

        Assert.Equal(expected, actual);
    }
}
```

- [ ] **Step 2: Run the helper tests to verify they fail because the shared helper does not exist yet**

Run: `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --filter OpenAiResponsesSharedTests`

Expected: FAIL with compile errors that `Tau.Ai.Providers.OpenAiResponses` / `OpenAiResponsesShared` do not exist.

- [ ] **Step 3: Commit the failing helper tests**

```bash
git add tests/Tau.Ai.Tests/OpenAiResponsesSharedTests.cs
git commit -m "test: add openai responses shared helper specs"
```

---

### Task 2: Implement the shared helper layer

**Files:**
- Create: `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesShared.cs`
- Test: `tests/Tau.Ai.Tests/OpenAiResponsesSharedTests.cs`
- Reference: `src/Tau.Ai/Providers/OpenAi/OpenAiMessageConverter.cs`
- Reference: `src/Tau.Ai/Providers/OpenAi/OpenAiStreamParser.cs`

- [ ] **Step 1: Add the minimal shared helper skeleton required by the tests**

```csharp
namespace Tau.Ai.Providers.OpenAiResponses;

internal static class OpenAiResponsesShared
{
    internal readonly record struct FunctionCallItem(string CallId, string ItemId, string Name, string ArgumentsJson);

    public static FunctionCallItem BuildFunctionCallItem(ToolCallContent toolCall)
    {
        var separator = toolCall.Id.IndexOf('|');
        if (separator <= 0 || separator == toolCall.Id.Length - 1)
        {
            throw new InvalidOperationException($"Tool call id '{toolCall.Id}' must be callId|itemId.");
        }

        return new FunctionCallItem(
            toolCall.Id[..separator],
            toolCall.Id[(separator + 1)..],
            toolCall.Name,
            toolCall.Arguments);
    }

    public static string DecodeCodexAccountId(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("Codex token is not a JWT.");
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("https://api.openai.com/auth")
            .GetProperty("chatgpt_account_id")
            .GetString()!;
    }

    public static StopReason MapStopReason(string? status, IReadOnlyList<ContentBlock> blocks)
    {
        var baseReason = status switch
        {
            "incomplete" => StopReason.MaxTokens,
            "failed" or "cancelled" => StopReason.Error,
            _ => StopReason.EndTurn
        };

        return baseReason == StopReason.EndTurn && blocks.Any(static b => b is ToolCallContent)
            ? StopReason.ToolUse
            : baseReason;
    }
}
```

- [ ] **Step 2: Run the helper tests to verify the minimal helper passes**

Run: `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --filter OpenAiResponsesSharedTests`

Expected: PASS with 3 tests passing.

- [ ] **Step 3: Expand the shared helper to cover request/response conversion and streaming translation**

Add these units into `OpenAiResponsesShared.cs`:

```csharp
internal sealed record OpenAiResponsesOptions : StreamOptions
{
    public ThinkingLevel? ReasoningEffort { get; init; }
    public string? ReasoningSummary { get; init; }
    public string? ServiceTier { get; init; }
}

internal sealed record OpenAiCodexResponsesOptions : StreamOptions
{
    public ThinkingLevel? ReasoningEffort { get; init; }
    public string? ReasoningSummary { get; init; }
    public string? ServiceTier { get; init; }
    public string? TextVerbosity { get; init; }
}

internal static JsonElement ConvertResponsesMessages(Model model, LlmContext context)
internal static JsonElement ConvertResponsesTools(IReadOnlyList<Tool> tools, bool? strict = false)
internal static async Task ProcessResponsesStreamAsync(
    Stream responseStream,
    AssistantMessageStream stream,
    AssistantMessage initial,
    Func<string, string> normalizeEventJson,
    CancellationToken cancellationToken = default)
```

Implementation requirements for this step:
- Support `UserMessage`, `AssistantMessage`, and `ToolResultMessage`.
- For `ToolCallContent.Id == "call|item"`, emit `call_id` and `id` separately.
- Write `ThinkingContent.ThinkingSignature` as the raw completed reasoning item JSON string.
- Write `TextContent.TextSignature` as a JSON string shaped like `{"v":1,"id":"msg_123","phase":"final_answer"}` when phase exists.
- Consume `response.created`, `response.output_item.added`, `response.reasoning_summary_text.delta`, `response.output_text.delta`, `response.refusal.delta`, `response.function_call_arguments.delta`, `response.function_call_arguments.done`, `response.output_item.done`, `response.completed`, `response.failed`, and `error`.
- Reuse `Tau.Ai.Streaming.SseParser.ParseAsync` to read SSE frames.
- Do not add new public stream event types; translate into existing Tau events only.

- [ ] **Step 4: Add one more helper test for message conversion with tool results and reasoning replay**

Append this test to `tests/Tau.Ai.Tests/OpenAiResponsesSharedTests.cs`:

```csharp
[Fact]
public void ConvertResponsesMessages_ReplaysAssistantToolCallAndToolResult()
{
    var model = new Model
    {
        Id = "gpt-5.4",
        Name = "GPT-5.4",
        Api = "openai-responses",
        Provider = "openai",
        Reasoning = true
    };

    var assistant = new AssistantMessage([
        new ThinkingContent("hidden")
        {
            ThinkingSignature = "{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"thinking\"}]}"
        },
        new ToolCallContent("call_1|fc_1", "read_file", "{\"path\":\"README.md\"}")
    ])
    {
        Api = "openai-responses",
        Provider = "openai",
        Model = "gpt-5.4"
    };

    var context = new LlmContext
    {
        SystemPrompt = "system",
        Messages =
        [
            new UserMessage("hello"),
            assistant,
            new ToolResultMessage("call_1|fc_1", [new TextContent("file body")])
        ]
    };

    var payload = OpenAiResponsesShared.ConvertResponsesMessages(model, context);
    var json = payload.GetRawText();

    Assert.Contains("\"role\":\"developer\"", json, StringComparison.Ordinal);
    Assert.Contains("\"type\":\"reasoning\"", json, StringComparison.Ordinal);
    Assert.Contains("\"type\":\"function_call\"", json, StringComparison.Ordinal);
    Assert.Contains("\"call_id\":\"call_1\"", json, StringComparison.Ordinal);
    Assert.Contains("\"type\":\"function_call_output\"", json, StringComparison.Ordinal);
}
```

- [ ] **Step 5: Run the helper tests again to verify shared conversion behavior passes**

Run: `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --filter OpenAiResponsesSharedTests`

Expected: PASS with all helper tests green.

- [ ] **Step 6: Commit the shared helper implementation**

```bash
git add src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesShared.cs tests/Tau.Ai.Tests/OpenAiResponsesSharedTests.cs
git commit -m "feat: add openai responses shared streaming helpers"
```

---

### Task 3: Add failing tests for the standard OpenAI Responses provider

**Files:**
- Create: `tests/Tau.Ai.Tests/OpenAiResponsesProviderTests.cs`
- Test: `tests/Tau.Ai.Tests/OpenAiResponsesSharedTests.cs`
- Reference: `tests/Tau.Ai.Tests/OpenAiProviderSerializationTests.cs`

- [ ] **Step 1: Write the failing provider integration tests with a stub SSE response**

```csharp
using System.Net;
using System.Text;
using Tau.Ai;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

public sealed class OpenAiResponsesProviderTests
{
    [Fact]
    public async Task Stream_TranslatesResponsesSseIntoTauEvents()
    {
        const string sse = """
        data: {"type":"response.created","response":{"id":"resp_123"}}

        data: {"type":"response.output_item.added","item":{"type":"message","id":"msg_1","role":"assistant","content":[]}}

        data: {"type":"response.content_part.added","part":{"type":"output_text","text":""}}

        data: {"type":"response.output_text.delta","delta":"Hello"}

        data: {"type":"response.output_item.done","item":{"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"Hello"}],"status":"completed","phase":"final_answer"}}

        data: {"type":"response.completed","response":{"id":"resp_123","status":"completed","usage":{"input_tokens":11,"output_tokens":7,"total_tokens":18,"input_tokens_details":{"cached_tokens":3}}}}

        """;

        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(client);

        var model = new Model
        {
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-responses",
            Provider = "openai",
            BaseUrl = "https://example.invalid/v1",
            Reasoning = true
        };

        var stream = provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("hello")] },
            new OpenAiResponsesOptions { ApiKey = "test-key", SessionId = "sess-123" });

        var events = new List<StreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        Assert.Contains(events, e => e is StartEvent);
        Assert.Contains(events, e => e is TextStartEvent);
        Assert.Contains(events, e => e is TextDeltaEvent delta && delta.Delta == "Hello");
        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal("resp_123", done.Message.ResponseId);
        Assert.Equal(StopReason.EndTurn, done.Message.StopReason);
        Assert.Equal(8, done.Message.Usage?.InputTokens);
        Assert.Equal(7, done.Message.Usage?.OutputTokens);
        Assert.Equal(3, done.Message.Usage?.CacheReadTokens);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
```

- [ ] **Step 2: Run the provider test to verify it fails because `OpenAiResponsesProvider` does not exist yet**

Run: `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --filter OpenAiResponsesProviderTests`

Expected: FAIL with compile errors that `OpenAiResponsesProvider` does not exist.

- [ ] **Step 3: Commit the failing provider tests**

```bash
git add tests/Tau.Ai.Tests/OpenAiResponsesProviderTests.cs
git commit -m "test: add openai responses provider specs"
```

---

### Task 4: Implement the standard OpenAI Responses provider

**Files:**
- Create: `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesProvider.cs`
- Test: `tests/Tau.Ai.Tests/OpenAiResponsesProviderTests.cs`
- Reference: `src/Tau.Ai/Providers/OpenAi/OpenAiProvider.cs`

- [ ] **Step 1: Add the minimal provider shell that satisfies the compiler**

```csharp
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.OpenAiResponses;

public sealed class OpenAiResponsesProvider(HttpClient? httpClient = null) : IStreamProvider
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();

    public string Api => "openai-responses";

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();
        _ = Task.Run(() =>
        {
            stream.Push(new ErrorEvent("not implemented"));
            return Task.CompletedTask;
        });
        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
        => Stream(model, context, new OpenAiResponsesOptions
        {
            ApiKey = options.ApiKey,
            Headers = options.Headers,
            SessionId = options.SessionId,
            CacheRetention = options.CacheRetention,
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            TopP = options.TopP,
            Metadata = options.Metadata,
            MaxRetryDelay = options.MaxRetryDelay,
            ReasoningEffort = options.Reasoning
        });
}
```

- [ ] **Step 2: Run the provider test to verify it now fails on behavior instead of missing types**

Run: `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --filter OpenAiResponsesProviderTests`

Expected: FAIL because the stream emits `ErrorEvent("not implemented")` instead of the expected events.

- [ ] **Step 3: Replace the stub with the real `/responses` implementation**

Key implementation code for `OpenAiResponsesProvider.cs`:

```csharp
private async Task StreamInternalAsync(
    Model model,
    LlmContext context,
    OpenAiResponsesOptions options,
    AssistantMessageStream stream,
    CancellationToken cancellationToken = default)
{
    var baseUrl = string.IsNullOrWhiteSpace(model.BaseUrl) ? "https://api.openai.com/v1" : model.BaseUrl!.TrimEnd('/');
    var url = $"{baseUrl}/responses";

    var body = new Dictionary<string, object?>
    {
        ["model"] = model.Id,
        ["input"] = OpenAiResponsesShared.ConvertResponsesMessages(model, context),
        ["stream"] = true,
        ["store"] = false,
        ["prompt_cache_key"] = options.CacheRetention == CacheRetention.None ? null : options.SessionId
    };

    if (context.Tools is { Count: > 0 })
    {
        body["tools"] = OpenAiResponsesShared.ConvertResponsesTools(context.Tools);
    }

    if (!string.IsNullOrEmpty(options.ServiceTier))
    {
        body["service_tier"] = options.ServiceTier;
    }

    if (options.MaxTokens.HasValue)
    {
        body["max_output_tokens"] = options.MaxTokens.Value;
    }

    if (options.Temperature.HasValue)
    {
        body["temperature"] = options.Temperature.Value;
    }

    if (model.Reasoning)
    {
        body["reasoning"] = new Dictionary<string, object?>
        {
            ["effort"] = OpenAiResponsesShared.ToOpenAiReasoning(options.ReasoningEffort),
            ["summary"] = options.ReasoningSummary ?? "auto"
        };
        body["include"] = new[] { "reasoning.encrypted_content" };
    }

    var json = JsonSerializer.Serialize(body, OpenAiResponsesRequestJsonContext.Default.DictionaryStringObject);
    using var request = new HttpRequestMessage(HttpMethod.Post, url)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
    ApplyHeaders(request, model.Headers);
    ApplyHeaders(request, options.Headers);

    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    if (!response.IsSuccessStatusCode)
    {
        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        stream.Push(new ErrorEvent($"OpenAI Responses API error {(int)response.StatusCode}: {errorBody}"));
        return;
    }

    await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    var initial = new AssistantMessage
    {
        Api = Api,
        Provider = model.Provider,
        Model = model.Id,
        Content = []
    };

    await OpenAiResponsesShared.ProcessResponsesStreamAsync(
        responseStream,
        stream,
        initial,
        normalizeEventJson: static json => json,
        cancellationToken);
}
```

Also add a small source-gen context in this file:

```csharp
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(JsonElement))]
internal partial class OpenAiResponsesRequestJsonContext : JsonSerializerContext;
```

- [ ] **Step 4: Run the provider test to verify it passes**

Run: `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --filter OpenAiResponsesProviderTests`

Expected: PASS with the SSE integration test green.

- [ ] **Step 5: Commit the standard provider implementation**

```bash
git add src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesProvider.cs tests/Tau.Ai.Tests/OpenAiResponsesProviderTests.cs
git commit -m "feat: add native openai responses provider"
```

---

### Task 5: Add failing tests for the Codex Responses provider

**Files:**
- Create: `tests/Tau.Ai.Tests/OpenAiCodexResponsesProviderTests.cs`
- Test: `tests/Tau.Ai.Tests/OpenAiResponsesSharedTests.cs`
- Reference: `pi-mono-main/packages/ai/src/providers/openai-codex-responses.ts`

- [ ] **Step 1: Write the failing Codex tests covering headers, event normalization, and retry**

```csharp
using System.Net;
using System.Text;
using Tau.Ai;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

public sealed class OpenAiCodexResponsesProviderTests
{
    [Fact]
    public async Task Stream_SendsCodexHeaders_AndNormalizesResponseDone()
    {
        const string token = "eyJhbGciOiJub25lIn0.eyJodHRwczovL2FwaS5vcGVuYWkuY29tL2F1dGgiOnsiY2hhdGdwdF9hY2NvdW50X2lkIjoiYWNjdF90ZXN0In19.";
        const string sse = """
        data: {"type":"response.created","response":{"id":"resp_codex"}}

        data: {"type":"response.output_item.added","item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"read_file","arguments":""}}

        data: {"type":"response.function_call_arguments.delta","delta":"{\"path\":\"README.md\"}"}

        data: {"type":"response.output_item.done","item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"read_file","arguments":"{\"path\":\"README.md\"}"}}

        data: {"type":"response.done","response":{"id":"resp_codex","status":"completed","usage":{"input_tokens":9,"output_tokens":5,"total_tokens":14}}}

        """;

        HttpRequestMessage? capturedRequest = null;
        using var handler = new SequencedHandler([
            _ =>
            {
                capturedRequest = _;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            }
        ]);
        using var client = new HttpClient(handler);
        var provider = new OpenAiCodexResponsesProvider(client);

        var model = new Model
        {
            Id = "gpt-5.1-codex-mini",
            Name = "GPT-5.1 Codex Mini",
            Api = "openai-codex-responses",
            Provider = "openai-codex",
            BaseUrl = "https://chatgpt.example.invalid/backend-api"
        };

        var stream = provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("inspect repo")] },
            new OpenAiCodexResponsesOptions { ApiKey = token, SessionId = "session-123" });

        var events = new List<StreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        Assert.NotNull(capturedRequest);
        Assert.Equal("acct_test", Assert.Single(capturedRequest!.Headers.GetValues("chatgpt-account-id")));
        Assert.Equal("tau", Assert.Single(capturedRequest.Headers.GetValues("originator")));
        Assert.Equal("responses=experimental", Assert.Single(capturedRequest.Headers.GetValues("OpenAI-Beta")));
        Assert.Equal("session-123", Assert.Single(capturedRequest.Headers.GetValues("x-client-request-id")));

        Assert.Contains(events, e => e is ToolCallStartEvent);
        Assert.Contains(events, e => e is ToolCallDeltaEvent delta && delta.Delta.Contains("README.md", StringComparison.Ordinal));
        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal(StopReason.ToolUse, done.Message.StopReason);
        Assert.Equal("resp_codex", done.Message.ResponseId);
    }

    [Fact]
    public async Task Stream_RetriesOnce_OnRetryableFailure()
    {
        const string token = "eyJhbGciOiJub25lIn0.eyJodHRwczovL2FwaS5vcGVuYWkuY29tL2F1dGgiOnsiY2hhdGdwdF9hY2NvdW50X2lkIjoiYWNjdF90ZXN0In19.";
        var attempts = 0;
        using var handler = new SequencedHandler([
            _ =>
            {
                attempts++;
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("rate limit", Encoding.UTF8, "text/plain")
                };
            },
            _ =>
            {
                attempts++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: {\"type\":\"response.done\",\"response\":{\"id\":\"resp_2\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}\n\n", Encoding.UTF8, "text/event-stream")
                };
            }
        ]);
        using var client = new HttpClient(handler);
        var provider = new OpenAiCodexResponsesProvider(client);

        var model = new Model
        {
            Id = "gpt-5.1-codex-mini",
            Name = "GPT-5.1 Codex Mini",
            Api = "openai-codex-responses",
            Provider = "openai-codex",
            BaseUrl = "https://chatgpt.example.invalid/backend-api"
        };

        var stream = provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("retry") ] },
            new OpenAiCodexResponsesOptions { ApiKey = token });

        await foreach (var _ in stream) { }

        Assert.Equal(2, attempts);
    }

    private sealed class SequencedHandler(IReadOnlyList<Func<HttpRequestMessage, HttpResponseMessage>> responders) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var responder = responders[_index++];
            return Task.FromResult(responder(request));
        }
    }
}
```

- [ ] **Step 2: Run the Codex tests to verify they fail because `OpenAiCodexResponsesProvider` does not exist yet**

Run: `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --filter OpenAiCodexResponsesProviderTests`

Expected: FAIL with compile errors that `OpenAiCodexResponsesProvider` does not exist.

- [ ] **Step 3: Commit the failing Codex tests**

```bash
git add tests/Tau.Ai.Tests/OpenAiCodexResponsesProviderTests.cs
git commit -m "test: add codex responses provider specs"
```

---

### Task 6: Implement the Codex Responses provider

**Files:**
- Create: `src/Tau.Ai/Providers/OpenAiResponses/OpenAiCodexResponsesProvider.cs`
- Test: `tests/Tau.Ai.Tests/OpenAiCodexResponsesProviderTests.cs`
- Reference: `src/Tau.Ai/Providers/Google/GoogleGeminiCliProvider.cs`

- [ ] **Step 1: Add the minimal provider shell that satisfies the compiler**

```csharp
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.OpenAiResponses;

public sealed class OpenAiCodexResponsesProvider(HttpClient? httpClient = null) : IStreamProvider
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();

    public string Api => "openai-codex-responses";

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();
        _ = Task.Run(() =>
        {
            stream.Push(new ErrorEvent("not implemented"));
            return Task.CompletedTask;
        });
        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
        => Stream(model, context, new OpenAiCodexResponsesOptions
        {
            ApiKey = options.ApiKey,
            Headers = options.Headers,
            SessionId = options.SessionId,
            CacheRetention = options.CacheRetention,
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            TopP = options.TopP,
            Metadata = options.Metadata,
            MaxRetryDelay = options.MaxRetryDelay,
            ReasoningEffort = options.Reasoning,
            TextVerbosity = "medium"
        });
}
```

- [ ] **Step 2: Run the Codex tests to verify they now fail on behavior instead of missing types**

Run: `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --filter OpenAiCodexResponsesProviderTests`

Expected: FAIL because the provider still emits `ErrorEvent("not implemented")`.

- [ ] **Step 3: Replace the stub with the real Codex SSE implementation and retry loop**

Key implementation code for `OpenAiCodexResponsesProvider.cs`:

```csharp
private const string DefaultBaseUrl = "https://chatgpt.com/backend-api";

private async Task StreamInternalAsync(
    Model model,
    LlmContext context,
    OpenAiCodexResponsesOptions options,
    AssistantMessageStream stream,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(options.ApiKey))
    {
        stream.Push(new ErrorEvent($"No API key for provider: {model.Provider}"));
        return;
    }

    var url = ResolveCodexUrl(model.BaseUrl);
    var accountId = OpenAiResponsesShared.DecodeCodexAccountId(options.ApiKey);
    var body = BuildCodexRequestBody(model, context, options);
    var json = JsonSerializer.Serialize(body, OpenAiCodexRequestJsonContext.Default.DictionaryStringObject);

    for (var attempt = 0; attempt < 4; attempt++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        request.Headers.TryAddWithoutValidation("originator", "tau");
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
        request.Headers.TryAddWithoutValidation("x-client-request-id", options.SessionId ?? $"codex-{Guid.NewGuid():N}");
        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            request.Headers.TryAddWithoutValidation("session_id", options.SessionId);
        }
        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var initial = new AssistantMessage
            {
                Api = Api,
                Provider = model.Provider,
                Model = model.Id,
                Content = []
            };
            await OpenAiResponsesShared.ProcessResponsesStreamAsync(
                responseStream,
                stream,
                initial,
                normalizeEventJson: OpenAiResponsesShared.NormalizeCodexEventJson,
                cancellationToken);
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
            || System.Text.RegularExpressions.Regex.IsMatch(errorBody, "rate.?limit|overloaded|service.?unavailable|upstream.?connect|connection.?refused", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!retryable || attempt == 3)
        {
            stream.Push(new ErrorEvent($"OpenAI Codex Responses API error {(int)response.StatusCode}: {errorBody}"));
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
    }
}
```

Also implement the small request builder used above:

```csharp
private static Dictionary<string, object?> BuildCodexRequestBody(Model model, LlmContext context, OpenAiCodexResponsesOptions options)
{
    var body = new Dictionary<string, object?>
    {
        ["model"] = model.Id,
        ["store"] = false,
        ["stream"] = true,
        ["instructions"] = context.SystemPrompt,
        ["input"] = OpenAiResponsesShared.ConvertResponsesMessages(model, context, includeSystemPrompt: false),
        ["text"] = new Dictionary<string, object?> { ["verbosity"] = options.TextVerbosity ?? "medium" },
        ["include"] = new[] { "reasoning.encrypted_content" },
        ["prompt_cache_key"] = options.SessionId,
        ["tool_choice"] = "auto",
        ["parallel_tool_calls"] = true
    };

    if (context.Tools is { Count: > 0 })
    {
        body["tools"] = OpenAiResponsesShared.ConvertResponsesTools(context.Tools, strict: null);
    }

    if (options.ReasoningEffort.HasValue)
    {
        body["reasoning"] = new Dictionary<string, object?>
        {
            ["effort"] = OpenAiResponsesShared.ToCodexReasoning(model.Id, options.ReasoningEffort.Value),
            ["summary"] = options.ReasoningSummary ?? "auto"
        };
    }

    return body;
}
```

- [ ] **Step 4: Run the Codex tests to verify they pass**

Run: `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --filter OpenAiCodexResponsesProviderTests`

Expected: PASS with header, normalization, and retry tests green.

- [ ] **Step 5: Commit the Codex provider implementation**

```bash
git add src/Tau.Ai/Providers/OpenAiResponses/OpenAiCodexResponsesProvider.cs tests/Tau.Ai.Tests/OpenAiCodexResponsesProviderTests.cs
git commit -m "feat: add native codex responses provider"
```

---

### Task 7: Switch builtin provider registration and lock it with tests

**Files:**
- Modify: `src/Tau.Ai/Providers/BuiltInProviders.cs:14-25`
- Modify: `tests/Tau.Ai.Tests/BuiltInProvidersTests.cs:7-22`
- Reference: `src/Tau.Ai/Providers/OpenAiCompat/OpenAiCompatibleProvider.cs`

- [ ] **Step 1: Strengthen the builtin provider test to assert concrete provider types**

Replace the test body in `tests/Tau.Ai.Tests/BuiltInProvidersTests.cs` with:

```csharp
[Fact]
public void RegisterAll_UsesNativeResponsesProvidersForOpenAiApis()
{
    var registry = new ProviderRegistry();

    BuiltInProviders.RegisterAll(registry);

    Assert.IsType<OpenAiResponsesProvider>(registry.Get("openai-responses"));
    Assert.IsType<OpenAiCodexResponsesProvider>(registry.Get("openai-codex-responses"));
    Assert.IsType<OpenAiCompatibleProvider>(registry.Get("azure-openai-responses"));
    Assert.IsType<OpenAiCompatibleProvider>(registry.Get("mistral-conversations"));
}
```

Also add the required using statements:

```csharp
using Tau.Ai.Providers.OpenAiCompat;
using Tau.Ai.Providers.OpenAiResponses;
```

- [ ] **Step 2: Run the builtin provider test to verify it fails before registration is updated**

Run: `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --filter RegisterAll_UsesNativeResponsesProvidersForOpenAiApis`

Expected: FAIL because `BuiltInProviders` still registers `OpenAiCompatibleProvider` for both OpenAI Responses APIs.

- [ ] **Step 3: Update builtin registration to use the native providers**

Edit `src/Tau.Ai/Providers/BuiltInProviders.cs` so these lines change to:

```csharp
registry.Register("openai-chat-completions", () => new OpenAiProvider(), sourceId: "builtin");
registry.Register("openai-responses", () => new OpenAiResponsesProvider(), sourceId: "builtin");
registry.Register("openai-codex-responses", () => new OpenAiCodexResponsesProvider(), sourceId: "builtin");
registry.Register("azure-openai-responses", () => new OpenAiCompatibleProvider("azure-openai-responses", ResolveAzureBaseUrl(), authHeaderName: "api-key", authHeaderPrefix: null), sourceId: "builtin");
registry.Register("mistral-conversations", () => new OpenAiCompatibleProvider("mistral-conversations", "https://api.mistral.ai/v1"), sourceId: "builtin");
```

And add the namespace import at the top:

```csharp
using Tau.Ai.Providers.OpenAiResponses;
```

- [ ] **Step 4: Run the builtin provider test to verify it passes**

Run: `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --filter RegisterAll_UsesNativeResponsesProvidersForOpenAiApis`

Expected: PASS.

- [ ] **Step 5: Commit the registration switch**

```bash
git add src/Tau.Ai/Providers/BuiltInProviders.cs tests/Tau.Ai.Tests/BuiltInProvidersTests.cs
git commit -m "feat: register native openai responses providers"
```

---

### Task 8: Run the focused Tau.Ai test suite and fix regressions

**Files:**
- Test: `tests/Tau.Ai.Tests/OpenAiResponsesSharedTests.cs`
- Test: `tests/Tau.Ai.Tests/OpenAiResponsesProviderTests.cs`
- Test: `tests/Tau.Ai.Tests/OpenAiCodexResponsesProviderTests.cs`
- Test: `tests/Tau.Ai.Tests/BuiltInProvidersTests.cs`
- Modify if needed: whichever file the failing assertion points to

- [ ] **Step 1: Run the focused Responses-related tests together**

Run: `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --filter "OpenAiResponsesSharedTests|OpenAiResponsesProviderTests|OpenAiCodexResponsesProviderTests|BuiltInProvidersTests"`

Expected: PASS. If it fails, do not continue until the failure is fixed.

- [ ] **Step 2: If a regression appears, fix only the minimal file the failure points to**

Examples of allowed minimal fixes:

```csharp
// If a text signature is missing phase, fix only the message-finalization branch.
currentTextBlock = currentTextBlock with
{
    TextSignature = phase is null
        ? JsonSerializer.Serialize(new { v = 1, id = messageId })
        : JsonSerializer.Serialize(new { v = 1, id = messageId, phase })
};
```

```csharp
// If function_call_output only uses callId, fix only that conversion branch.
body.Add(new Dictionary<string, object?>
{
    ["type"] = "function_call_output",
    ["call_id"] = callId,
    ["output"] = outputText
});
```

- [ ] **Step 3: Re-run the same focused test command until it passes**

Run: `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --filter "OpenAiResponsesSharedTests|OpenAiResponsesProviderTests|OpenAiCodexResponsesProviderTests|BuiltInProvidersTests"`

Expected: PASS.

- [ ] **Step 4: Commit the regression fixes if any were needed**

```bash
git add src/Tau.Ai/Providers/OpenAiResponses/*.cs tests/Tau.Ai.Tests/*.cs
git commit -m "fix: stabilize openai responses provider tests"
```

Skip this commit if Step 1 already passed with no code changes.

---

### Task 9: Update docs and backlog entries after code is green

**Files:**
- Modify: `docs/ARCHITECTURE.md`
- Modify: `docs/QUALITY_SCORE.md`
- Modify: `next.md`
- Create: `docs/histories/2026-04/20260427-openai-responses-port.md`
- Reference: `docs/HISTORY_GUIDE.md`
- Reference: `docs/exec-plans/active/2026-04-27-openai-responses-port.md`

- [ ] **Step 1: Update `next.md` to mark the native Responses port complete and leave the remaining follow-ups explicit**

Replace the OpenAI Responses backlog line with content shaped like this:

```md
- [x] `openai-responses` 与 `openai-codex-responses` 提高协议保真度，补齐和上游更一致的 payload / stream 语义
- [ ] `openai-codex-responses` WebSocket transport / session socket cache
- [ ] `openai-responses` 的 Copilot 动态头与 vision 输入保真度
- [ ] service-tier pricing / cache-retention `24h` 行为补齐
```

- [ ] **Step 2: Update `docs/ARCHITECTURE.md` so the provider section describes native Responses providers instead of chat-completions fallback**

Insert or update a paragraph with content equivalent to:

```md
- `Tau.Ai/Providers/OpenAiResponses/` now hosts the native OpenAI Responses implementations.
  - `OpenAiResponsesProvider` targets `/responses` and translates OpenAI reasoning/text/tool-call SSE events into Tau's existing stream event model.
  - `OpenAiCodexResponsesProvider` targets ChatGPT Codex `/codex/responses` over SSE, including JWT-derived account headers and retry semantics.
  - `OpenAiCompatibleProvider` remains the fallback only for APIs that still speak chat-completions-compatible wire formats (`azure-openai-responses`, current `mistral-conversations`).
```

- [ ] **Step 3: Update `docs/QUALITY_SCORE.md` to move this gap from active P0 to remaining follow-up debt**

Add or edit text equivalent to:

```md
- `openai-responses` / `openai-codex-responses` 已从 OpenAI-compatible fallback 切到 native Responses SSE provider，基本 payload / stream fidelity 已补齐。
- 剩余缺口：Codex websocket transport、Copilot 动态头、service-tier pricing / prompt cache retention 细节。
```

- [ ] **Step 4: Write the history entry**

Create `docs/histories/2026-04/20260427-openai-responses-port.md` with this structure:

```md
## [2026-04-27] | Task: openai-responses-port

### Changes Overview
- Added native `OpenAiResponsesProvider` and `OpenAiCodexResponsesProvider` under `src/Tau.Ai/Providers/OpenAiResponses/`.
- Added shared Responses conversion/stream translation helpers and focused Tau.Ai tests.
- Switched builtin registration away from `OpenAiCompatibleProvider` for OpenAI Responses APIs.
- Synced architecture / quality / next docs to the new state.

### Verification
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --filter "OpenAiResponsesSharedTests|OpenAiResponsesProviderTests|OpenAiCodexResponsesProviderTests|BuiltInProvidersTests"`
- `dotnet build src/Tau.Ai/Tau.Ai.csproj --no-restore`
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore`

### Notes
- Codex websocket transport is still intentionally out of scope.
- `originator` defaults to `tau` and can be overridden via `Model.Headers` if future e2e validation requires parity with upstream `pi`.
```

- [ ] **Step 5: Commit the docs/history sync**

```bash
git add next.md docs/ARCHITECTURE.md docs/QUALITY_SCORE.md docs/histories/2026-04/20260427-openai-responses-port.md
git commit -m "docs: record openai responses port"
```

---

### Task 10: Run the final Tau.Ai verification commands

**Files:**
- Verify: `src/Tau.Ai/`
- Verify: `tests/Tau.Ai.Tests/`
- Verify: `docs/`

- [ ] **Step 1: Build Tau.Ai**

Run: `dotnet build src/Tau.Ai/Tau.Ai.csproj --no-restore`

Expected: `Build succeeded.`

- [ ] **Step 2: Build Tau.Ai.Tests**

Run: `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore`

Expected: `Build succeeded.`

- [ ] **Step 3: Run the Tau.Ai test project without rebuild**

Run: `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore`

Expected: PASS.

- [ ] **Step 4: If any final failure appears, fix only the exact failing file and re-run the same command**

Example minimal fix pattern:

```csharp
if (responseUsage.TryGetProperty("input_tokens_details", out var details)
    && details.TryGetProperty("cached_tokens", out var cached))
{
    cacheReadTokens = cached.GetInt32();
}
```

- [ ] **Step 5: Re-run all three verification commands**

Run:
- `dotnet build src/Tau.Ai/Tau.Ai.csproj --no-restore`
- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore`
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore`

Expected: all PASS.

- [ ] **Step 6: Commit the final verification fixes if any were needed**

```bash
git add src/Tau.Ai/Providers/OpenAiResponses/*.cs tests/Tau.Ai.Tests/*.cs next.md docs/ARCHITECTURE.md docs/QUALITY_SCORE.md docs/histories/2026-04/20260427-openai-responses-port.md
git commit -m "fix: finish openai responses port verification"
```

Skip this commit if Step 3 and Step 5 already passed with no code changes.

---

## Self-review checklist

- Spec coverage: covered shared helper, standard provider, Codex provider, builtin registration switch, focused tests, docs sync, and final verification.
- Placeholder scan: no `TODO`, `TBD`, or implicit “write tests later” steps remain.
- Type consistency: all provider class names, option names, helper names, and test names are consistent across tasks.
