// 作者：xxx
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Registry;
using Tau.Ai.Serialization;
using Tau.Ai.Streaming;
using Tau.Ai.Utilities;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Responses 生命周期回归】覆盖终态、用量、拒答和推理重放的真实 Provider 路径。</summary>
public sealed class ResponsesLifecycleTests
{
    private static readonly string[] Protocols = ["responses", "azure", "codex", "codex-ws"];
    private const string TextAdded = """{"type":"response.output_item.added","output_index":2,"item":{"type":"message","id":"msg_1"}}""";
    private const string TextDelta = """{"type":"response.output_text.delta","output_index":2,"delta":"kept"}""";

    /// <summary>生成四条传输路径与供应商终态的组合。</summary>
    /// <returns>协议、状态、具体原因和统一终态。</returns>
    public static IEnumerable<object?[]> StatusCases()
    {
        var cases = new (string? Status, string? Reason, StopReason Expected)[]
        {
            ("completed", null, StopReason.EndTurn), (null, null, StopReason.EndTurn),
            ("incomplete", "max_output_tokens", StopReason.MaxTokens),
            ("incomplete", "content_filter", StopReason.Error), ("incomplete", null, StopReason.Error),
            ("incomplete", "unknown_limit", StopReason.Error), ("failed", null, StopReason.Error),
            ("cancelled", null, StopReason.Error), ("queued", null, StopReason.EndTurn),
            ("in_progress", null, StopReason.EndTurn), ("unexpected", null, StopReason.Error)
        };
        foreach (var protocol in Protocols)
            foreach (var item in cases) yield return [protocol, item.Status, item.Reason, item.Expected];
    }

    /// <summary>区分正常完成、长度截断和错误，保留原始原因、已有正文与供应商 end_turn。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <param name="status">供应商状态。</param>
    /// <param name="reason">不完整响应原因。</param>
    /// <param name="expected">未经过 Codex 状态规范化前的预期终态。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [MemberData(nameof(StatusCases))]
    public async Task Terminal_PreservesSpecificStatus(string protocol, string? status, string? reason, StopReason expected)
    {
        var codexUnknown = IsCodex(protocol) && status == "unexpected";
        if (codexUnknown) expected = StopReason.EndTurn;
        var capture = await SendAsync(protocol, [TextAdded, TextDelta, Terminal(status, reason, extra: "\"end_turn\":false",
            eventType: status == "incomplete" ? "response.incomplete" : "response.completed")]);
        Assert.Equal(expected, capture.Result.StopReason);
        Assert.Equal(codexUnknown ? null : reason is null ? status : status + "." + reason, capture.Result.RawStopReason);
        Assert.False(capture.Result.EndTurn);
        Assert.Equal("resp_final", capture.Result.ResponseId);
        Assert.Equal("kept", Assert.IsType<TextContent>(Assert.Single(capture.Result.Content)).Text);
        if (expected == StopReason.Error)
        {
            var error = Assert.Single(capture.Events.OfType<ErrorEvent>());
            Assert.Equal(capture.Result, error.Message);
            Assert.Empty(capture.Events.OfType<DoneEvent>());
            Assert.Equal(status == "incomplete" ? reason is null ? "Response incomplete without a provider reason" : "Response incomplete: " + reason
                : status == "unexpected" ? "Unhandled stop reason: unexpected" : "An unknown error occurred", error.Error);
        }
        else
        {
            Assert.Single(capture.Events.OfType<DoneEvent>());
            Assert.Empty(capture.Events.OfType<ErrorEvent>());
            Assert.Null(capture.Result.ErrorMessage);
        }
    }

    /// <summary>保留失败事件的错误码、具体原因与缺省回退，并覆盖 Codex 的独立错误文本约定。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    [InlineData("codex-ws")]
    public async Task Failure_RetainsDiagnosticDetails(string protocol)
    {
        var failures = new (string Event, string Shared, string Codex)[]
        {
            ("""{"type":"response.failed","response":{"status":"failed","error":{"code":"quota","message":"no credits"}}}""", "quota: no credits", "no credits"),
            ("""{"type":"response.failed","response":{"status":"failed","error":{}}}""", "unknown: no message", "Codex response failed"),
            ("""{"type":"response.failed","response":{"status":"failed","error":null,"incomplete_details":{"reason":"policy"}}}""", "incomplete: policy", "Codex response failed"),
            ("""{"type":"response.failed","response":{"error":null}}""", "Unknown error (no error details in response)", "Codex response failed"),
            ("""{"type":"error","code":"server_error","message":"retry later"}""", "Error Code server_error: retry later", "Codex error: retry later"),
            ("""{"type":"error","error":{"code":"quota","message":"nested"}}""", "Error Code quota: nested", "Codex error: nested")
        };
        foreach (var failure in failures)
        {
            var capture = await SendAsync(protocol, ["""{"type":"response.created","response":{"id":"created","usage":{"input_tokens":2,"output_tokens":1}}}""",
                TextAdded, TextDelta, failure.Event]);
            Assert.Equal(StopReason.Error, capture.Result.StopReason);
            Assert.Equal(IsCodex(protocol) ? failure.Codex : failure.Shared, capture.Result.ErrorMessage);
            Assert.Equal("created", capture.Result.ResponseId);
            Assert.Equal("lifecycle", capture.Result.Model);
            Assert.Equal(new Usage(2, 1, ServiceTier: IsCodex(protocol) ? "flex" : protocol == "responses" ? "priority" : null), capture.Result.Usage);
            Assert.Equal("kept", Assert.IsType<TextContent>(Assert.Single(capture.Result.Content)).Text);
            Assert.Single(capture.Events.OfType<ErrorEvent>());
            Assert.Empty(capture.Events.OfType<DoneEvent>());
        }
    }

    /// <summary>缓存读写从输入中扣除，推理已包含在输出内，费用及上下文估算使用同一用量口径。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    [InlineData("codex-ws")]
    public async Task Usage_SeparatesCachedInputAndPreservesTotals(string protocol)
    {
        var tier = IsCodex(protocol) ? "default" : "priority";
        var capture = await SendAsync(protocol, [Terminal(extra: "\"service_tier\":" + Quote(tier) + "," +
            """{"usage":{"input_tokens":1000000,"output_tokens":200000,"input_tokens_details":{"cached_tokens":250000,"cache_write_tokens":100000},"output_tokens_details":{"reasoning_tokens":30000},"total_tokens":1200000,"prompt_cache_hit_tokens":999,"prompt_cache_miss_tokens":999}}"""[1..^1])]);
        var usage = capture.Result.Usage!.Value;
        Assert.Equal(650000, usage.InputTokens);
        Assert.Equal(200000, usage.OutputTokens);
        Assert.Equal(250000, usage.CacheReadTokens);
        Assert.Equal(100000, usage.CacheWriteTokens);
        Assert.Equal(30000, usage.ReasoningTokens);
        Assert.Equal(1200000, usage.TotalTokens);
        Assert.Equal(IsCodex(protocol) ? "flex" : "priority", usage.ServiceTier);
        Assert.Equal(IsCodex(protocol) ? 1.5625m : 6.25m, ModelCatalog.CalculateCost(Model(protocol), usage).Total);
        Assert.Equal(1200000, TokenEstimation.CalculateContextTokens(usage));

        var clamped = await SendAsync(protocol, [Terminal(extra:
            """{"usage":{"input_tokens":10,"output_tokens":3,"input_tokens_details":{"cached_tokens":2147483647,"cache_write_tokens":2147483647}}}"""[1..^1])]);
        Assert.Equal(0, clamped.Result.Usage!.Value.InputTokens);
        var retained = await SendAsync(protocol,
            ["""{"type":"response.created","response":{"usage":{"input_tokens":10,"output_tokens":2,"cache_read_input_tokens":2,"cache_creation_input_tokens":3}}}""",
                Terminal(extra: "\"usage\":null,\"service_tier\":\"priority\"")]);
        Assert.Equal(new Usage(5, 2, 2, 3, "priority"), retained.Result.Usage);
    }

    /// <summary>推理终值优先摘要，其次原始内容；终态仅补缺失加密签名并可在下一轮真实请求中重放。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    [InlineData("codex-ws")]
    public async Task Reasoning_BackfillsSignaturesAndReplays(string protocol)
    {
        var capture = await SendAsync(protocol,
        ["""{"type":"response.output_item.added","output_index":4,"item":{"type":"reasoning","id":"rs_a"}}""",
            """{"type":"response.reasoning_summary_text.delta","output_index":4,"delta":"draft"}""",
            """{"type":"response.reasoning_summary_part.done","output_index":4}""",
            """{"type":"response.reasoning_text.delta","output_index":4,"delta":"tail"}""",
            """{"type":"response.reasoning_text.delta","output_index":404,"delta":"wrong"}""",
            """{"type":"response.output_text.delta","output_index":4,"delta":"wrong type"}""",
            """{"type":"response.output_item.done","output_index":4,"item":{"type":"reasoning","id":"rs_a","summary":[{"type":"summary_text","text":"first"},{"type":"summary_text","text":"second"}],"content":[{"type":"reasoning_text","text":"ignored"}],"encrypted_content":null,"extra":"retained"}}""",
            """{"type":"response.reasoning_text.delta","output_index":4,"delta":"late"}""",
            """{"type":"response.output_item.done","output_index":8,"item":{"type":"reasoning","id":"rs_b","summary":[],"content":[{"type":"reasoning_text","text":"raw"},{"type":"reasoning_text","text":"content"}],"encrypted_content":"original"}}""",
            Terminal(extra: """{"output":[{"type":"reasoning","id":"rs_b","encrypted_content":"replacement"},{"type":"reasoning","id":"rs_a","encrypted_content":"backfilled","summary":[]},{"type":"reasoning","id":"rs_unknown","encrypted_content":"foreign"}]}"""[1..^1])]);
        var blocks = capture.Result.Content.OfType<ThinkingContent>().ToArray();
        Assert.Equal(2, capture.Result.Content.Count);
        Assert.Equal("first\n\nsecond", blocks[0].Thinking);
        Assert.Equal("raw\n\ncontent", blocks[1].Thinking);
        Assert.Equal("draft\n\ntail", string.Concat(capture.Events.OfType<ThinkingDeltaEvent>().Select(item => item.Delta)));
        Assert.Equal(blocks.Select(item => item.Thinking), capture.Events.OfType<ThinkingEndEvent>().Select(item => item.Content));
        var first = Json(blocks[0].ThinkingSignature!);
        Assert.Equal("backfilled", first.GetProperty("encrypted_content").GetString());
        Assert.Equal("retained", first.GetProperty("extra").GetString());
        Assert.Equal("first", first.GetProperty("summary")[0].GetProperty("text").GetString());
        Assert.Equal("original", Json(blocks[1].ThinkingSignature!).GetProperty("encrypted_content").GetString());
        var replay = await SendAsync(protocol, [Terminal()], new(null, [capture.Result, new UserMessage("continue")], null));
        var reasoning = replay.Body.GetProperty("input").EnumerateArray().Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "reasoning").ToArray();
        Assert.Equal(2, reasoning.Length);
        Assert.Equal("backfilled", reasoning[0].GetProperty("encrypted_content").GetString());
        Assert.Equal("original", reasoning[1].GetProperty("encrypted_content").GetString());

        var truncated = await SendAsync(protocol,
        ["""{"type":"response.output_item.added","output_index":1,"item":{"type":"reasoning","id":"rs_c"}}""",
            """{"type":"response.reasoning_text.delta","output_index":1,"delta":"streamed"}""",
            """{"type":"response.output_item.done","output_index":1,"item":{"type":"reasoning","id":"rs_c","summary":[],"content":[]}}""",
            Terminal("incomplete", "max_output_tokens", extra: """{"output":[{"type":"reasoning","id":"rs_c","encrypted_content":"limited"}]}"""[1..^1], eventType: "response.incomplete")]);
        var preserved = Assert.IsType<ThinkingContent>(Assert.Single(truncated.Result.Content));
        Assert.Equal(StopReason.MaxTokens, truncated.Result.StopReason);
        Assert.Equal("streamed", preserved.Thinking);
        Assert.Equal("limited", Json(preserved.ThinkingSignature!).GetProperty("encrypted_content").GetString());
    }

    /// <summary>拒答终值读取 refusal 字段；错误槽位、重复完成和迟到文本不得污染已完成内容。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    [InlineData("codex-ws")]
    public async Task Text_PreservesRefusalAndCompletedSlots(string protocol)
    {
        const string done = """{"type":"response.output_item.done","output_index":2,"item":{"type":"message","id":"msg_1","phase":"final_answer","content":[{"type":"output_text","text":"safe:"},{"type":"refusal","refusal":"no"}]}}""";
        var capture = await SendAsync(protocol, [TextAdded,
            """{"type":"response.refusal.delta","output_index":2,"delta":"draft"}""",
            """{"type":"response.output_text.delta","output_index":404,"delta":"unknown"}""",
            """{"type":"response.reasoning_text.delta","output_index":2,"delta":"wrong type"}""",
            done, done, TextDelta,
            """{"type":"response.output_item.added","output_index":5,"item":{"type":"message","id":"msg_empty"}}""",
            """{"type":"response.output_text.delta","output_index":5,"delta":"cleared"}""",
            """{"type":"response.output_item.done","output_index":5,"item":{"type":"message","id":"msg_empty","content":[]}}""", Terminal()]);
        Assert.Equal(new[] { "safe:no", "" }, capture.Result.Content.OfType<TextContent>().Select(item => item.Text));
        Assert.Equal(2, capture.Events.OfType<TextStartEvent>().Count());
        Assert.Equal(new[] { "safe:no", "" }, capture.Events.OfType<TextEndEvent>().Select(item => item.Content));
        Assert.Equal(new[] { "draft", "cleared" }, capture.Events.OfType<TextDeltaEvent>().Select(item => item.Delta));
        Assert.Empty(capture.Events.OfType<ThinkingStartEvent>());
    }

    /// <summary>所有 Codex 终态别名保留实际状态，不根据事件名称伪造 completed。</summary>
    /// <param name="protocol">Codex 传输路径。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("codex")]
    [InlineData("codex-ws")]
    public async Task CodexAliases_KeepActualStatus(string protocol)
    {
        foreach (var eventType in new[] { "response.done", "response.completed", "response.incomplete" })
        {
            var failed = await SendAsync(protocol, [Terminal("failed", eventType: eventType)]);
            Assert.Equal(StopReason.Error, failed.Result.StopReason);
            Assert.Equal("failed", failed.Result.RawStopReason);
            var limited = await SendAsync(protocol, [Terminal("incomplete", "max_output_tokens", eventType: eventType)]);
            Assert.Equal(StopReason.MaxTokens, limited.Result.StopReason);
            Assert.Equal("incomplete.max_output_tokens", limited.Result.RawStopReason);
        }
    }

    /// <summary>共享 JSON 流只有成功终态才调用释放并保留连接的回调。</summary>
    /// <param name="success">是否完成成功响应。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JsonStream_InvokesSuccessCallbackOnlyOnSuccess(bool success)
    {
        var stream = new AssistantMessageStream();
        var callbacks = 0;
        var socket = new LifecycleSocket([Terminal(success ? "completed" : "incomplete", success ? null : "content_filter")]);
        var result = await OpenAiResponsesShared.ProcessResponsesJsonEventsAsync(socket.ReadTextMessagesAsync(), new AssistantMessage { Content = [] }, stream,
            beforeDone: () => callbacks++);
        Assert.Equal(success, result);
        Assert.Equal(success ? 1 : 0, callbacks);
        Assert.Equal(success ? StopReason.EndTurn : StopReason.Error, (await stream.ResultAsync).StopReason);
    }

    /// <summary>创建终态事件 JSON，允许省略状态或附加供应商字段。</summary>
    /// <param name="status">状态值。</param>
    /// <param name="reason">不完整原因。</param>
    /// <param name="extra">其他响应字段。</param>
    /// <param name="eventType">终态事件类型。</param>
    /// <returns>事件 JSON。</returns>
    private static string Terminal(string? status = "completed", string? reason = null, string? extra = null, string eventType = "response.completed") =>
        "{\"type\":" + Quote(eventType) + ",\"response\":{\"id\":\"resp_final\"" +
        (status is null ? "" : ",\"status\":" + Quote(status)) +
        (reason is null ? "" : ",\"incomplete_details\":{\"reason\":" + Quote(reason) + "}") +
        (extra is null ? "" : "," + extra) + "}}";

    /// <summary>判断是否采用 Codex 事件适配层。</summary>
    /// <param name="protocol">协议别名。</param>
    /// <returns>是否为 Codex。</returns>
    private static bool IsCodex(string protocol) => protocol is "codex" or "codex-ws";

    /// <summary>创建与回放身份匹配的模型，并配置可核对的缓存费用。</summary>
    /// <param name="protocol">协议别名。</param>
    /// <returns>测试模型。</returns>
    private static Model Model(string protocol) => new()
    {
        Id = "lifecycle", Name = "Lifecycle", BaseUrl = "https://example.invalid/v1", Cost = new(2m, 8m, 0.5m, 1m),
        Provider = protocol == "azure" ? "azure-openai-responses" : IsCodex(protocol) ? "openai-codex" : "openai",
        Api = protocol == "azure" ? "azure-openai-responses" : IsCodex(protocol) ? "openai-codex-responses" : "openai-responses"
    };

    /// <summary>【AI】【Responses 生命周期回归】执行真实 Provider 并捕获传输报文与事件。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <param name="events">供应商事件。</param>
    /// <param name="context">可选的重放上下文。</param>
    /// <returns>最终消息、流事件和请求体。</returns>
    private static async Task<Capture> SendAsync(string protocol, string[] events, LlmContext? context = null)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(string.Concat(events.Select(item => "data: " + item + "\n\n"))));
        using var client = new HttpClient(handler);
        var socket = new LifecycleSocket(events);
        IStreamProvider provider = protocol switch
        {
            "azure" => new AzureOpenAiResponsesProvider(client),
            "codex" or "codex-ws" => new OpenAiCodexResponsesProvider(client, socket),
            _ => new OpenAiResponsesProvider(client)
        };
        using var lifetime = provider as IDisposable;
        StreamOptions options = protocol switch
        {
            "azure" => new AzureOpenAiResponsesOptions { ApiKey = "key" },
            "codex" or "codex-ws" => new OpenAiCodexResponsesOptions
            {
                ApiKey = OpenAiResponsesSharedTests.BuildFakeJwt("lifecycle"), ServiceTier = "flex",
                Transport = protocol == "codex-ws" ? StreamTransport.WebSocket : StreamTransport.Sse
            },
            _ => new OpenAiResponsesOptions { ApiKey = "key", ServiceTier = "priority" }
        };
        var stream = provider.Stream(Model(protocol), context ?? new(null, [new UserMessage("run")], null), options);
        var collected = new List<StreamEvent>();
        await foreach (var item in stream) collected.Add(item);
        return new(await stream.ResultAsync, collected, Json(socket.Frame ?? handler.CapturedBody));
    }

    /// <summary>通过源生成序列化获得 JSON 字符串字面量。</summary>
    /// <param name="value">字符串值。</param>
    /// <returns>带引号的 JSON。</returns>
    private static string Quote(string value) => JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["value"] = value },
        TauAiJsonContext.Default.DictionaryStringString).GetProperty("value").GetRawText();

    /// <summary>解析独立持有内存的 JSON。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>JSON 副本。</returns>
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    private sealed record Capture(AssistantMessage Result, IReadOnlyList<StreamEvent> Events, JsonElement Body);

    /// <summary>为真实 Codex Provider 提供内存事件传输。</summary>
    /// <param name="events">待返回的协议事件。</param>
    private sealed class LifecycleSocket(string[] events) : ICodexWebSocketTransport, ICodexWebSocketConnection
    {
        public WebSocketState State { get; private set; } = WebSocketState.Open;
        public string? Frame { get; private set; }

        /// <summary>返回内存连接。</summary>
        /// <param name="url">连接地址。</param>
        /// <param name="headers">请求头。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>模拟连接。</returns>
        public Task<ICodexWebSocketConnection> ConnectAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default) => Task.FromResult<ICodexWebSocketConnection>(this);

        /// <summary>记录实际请求帧。</summary>
        /// <param name="text">请求 JSON。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>已完成任务。</returns>
        public Task SendTextAsync(string text, CancellationToken cancellationToken = default) { Frame = text; return Task.CompletedTask; }

        /// <summary>逐条提供事件，保留真实异步枚举边界。</summary>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>事件序列。</returns>
        public async IAsyncEnumerable<string> ReadTextMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in events) { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); }
        }

        /// <summary>关闭内存连接。</summary>
        /// <param name="statusCode">关闭状态。</param>
        /// <param name="reason">关闭原因。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>已完成任务。</returns>
        public ValueTask CloseAsync(int statusCode, string reason, CancellationToken cancellationToken = default) { State = WebSocketState.Closed; return ValueTask.CompletedTask; }

        /// <summary>释放内存连接。</summary>
        /// <returns>已完成任务。</returns>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
