// 作者：xxx
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【扩展流式】将 JavaScript 提供方的原生事件流连接到 Agent 消费通道。</summary>
internal sealed class CodingAgentJavaScriptStreamProvider(
    CodingAgentJavaScriptExtensionRuntime runtime, string filePath, string providerId, long generation,
    string? customApi, bool isNative, ProviderRegistry? registry) : IStreamProvider
{
    public string Api => providerId;
    public bool SupportsTranscriptContext => true;

    /// <summary>【CodingAgent】【扩展流式】原生提供方启动 stream，兼容配置提供方沿用 streamSimple。</summary>
    /// <param name="model">请求模型。</param><param name="context">完整会话上下文。</param><param name="options">请求选项。</param>
    /// <returns>可立即消费的助手事件流。</returns>
    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) => Start(model, context, options, simple: false);

    /// <summary>【CodingAgent】【扩展流式】启动包含推理预算的简化请求。</summary>
    /// <param name="model">请求模型。</param><param name="context">完整会话上下文。</param><param name="options">请求选项。</param>
    /// <returns>可立即消费的助手事件流。</returns>
    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Start(model, context, options, simple: true);

    /// <summary>【CodingAgent】【扩展流式】异步生产事件，错误和取消也作为终止事件交付。</summary>
    /// <param name="model">请求模型。</param><param name="context">消息上下文。</param><param name="options">选项与取消信号。</param>
    /// <param name="simple">是否使用简化请求。</param><returns>独立流通道。</returns>
    private AssistantMessageStream Start(Model model, LlmContext context, StreamOptions options, bool simple)
    {
        var api = customApi switch { "openai-completions" or "openai-compatible" => "openai-chat-completions", "google-generative-ai" => "google-generative-language", _ => customApi };
        if (!isNative && model.Api != api && registry is not null)
        {
            var fallback = registry.Get(model.Api);
            var resolved = fallback.SupportsTranscriptContext ? context : Transcript.ResolveContext(context);
            return simple ? fallback.StreamSimple(model, resolved, (SimpleStreamOptions)options) : fallback.Stream(model, resolved, options);
        }
        var stream = new AssistantMessageStream();
        _ = runtime.StreamProviderAsync(filePath, providerId, generation, model, context, options, stream, simple);
        return stream;
    }
}

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    private readonly ConcurrentDictionary<string, (Model Model, StreamOptions Options)> _providerCallbacks = new();

    /// <summary>【CodingAgent】【扩展流式】序列化请求、顺序接收事件并保证缺少终值或异常时正常结束。</summary>
    /// <param name="filePath">扩展路径。</param><param name="providerId">提供方标识。</param><param name="generation">注册代次。</param>
    /// <param name="model">请求模型。</param><param name="context">完整上下文。</param><param name="options">真实请求选项。</param>
    /// <param name="stream">输出事件通道。</param><param name="simple">是否为简化调用。</param><returns>流生产任务。</returns>
    internal async Task StreamProviderAsync(string filePath, string providerId, long generation, Model model, LlmContext context,
        StreamOptions options, AssistantMessageStream stream, bool simple)
    {
        var callId = Guid.NewGuid().ToString("N");
        AssistantMessage last = new() { Api = model.Api, Provider = model.Provider, Model = model.Id, Content = [], Timestamp = DateTimeOffset.UtcNow };
        var ended = false;
        try
        {
            _providerCallbacks[callId] = (model, options);
            var request = CreateProviderRequest(providerId, callId, model, context, options, simple);
            var result = await ExecuteAsync(BuildPayload("streamProvider", filePath, _cwd, toolArgs: request), options.Signal,
                frame =>
                {
                    if (ended) return Task.CompletedTask;
                    var item = ReadProviderStreamEvent(frame, model);
                    last = item switch
                    {
                        StartEvent value => value.Partial, TextStartEvent value => value.Partial, TextDeltaEvent value => value.Partial,
                        TextEndEvent value => value.Partial, ThinkingStartEvent value => value.Partial, ThinkingDeltaEvent value => value.Partial,
                        ThinkingEndEvent value => value.Partial, ToolCallStartEvent value => value.Partial, ToolCallDeltaEvent value => value.Partial,
                        ToolCallEndEvent value => value.Partial, DoneEvent value => value.Message, ErrorEvent value => value.Message ?? value.Partial ?? last,
                        _ => last
                    };
                    ended = item is DoneEvent or ErrorEvent;
                    stream.Push(item);
                    return Task.CompletedTask;
                }, generation, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            if (!result.Success) throw new InvalidOperationException(result.Error);
            using var document = JsonDocument.Parse(result.ResultJson);
            if (!ReadBool(document.RootElement, "ok")) throw new InvalidOperationException(ReadString(document.RootElement, "error"));
            if (!ended) throw new InvalidOperationException("Extension provider ended without a terminal event.");
        }
        catch (Exception error)
        {
            if (!ended)
            {
                var message = last with { StopReason = options.Signal.IsCancellationRequested ? StopReason.Aborted : StopReason.Error,
                    ErrorMessage = options.Signal.IsCancellationRequested ? "Request aborted" : error.Message };
                stream.Push(new ErrorEvent(message.ErrorMessage!, message, message));
            }
        }
        finally { _providerCallbacks.TryRemove(callId, out _); }
    }

    /// <summary>【CodingAgent】【提供方回调】在宿主执行请求体、响应及原始事件回调，支持重入扩展钩子。</summary>
    /// <param name="request">带调用标识、回调类别和值的请求。</param><returns>回调返回值。</returns>
    internal async Task<object?> HandleProviderCallbackAsync(JsonElement request)
    {
        if (!_providerCallbacks.TryGetValue(request.GetProperty("callId").GetString()!, out var call))
            throw new InvalidOperationException("Provider request is no longer active.");
        call.Options.Signal.ThrowIfCancellationRequested();
        var value = request.GetProperty("value");
        switch (request.GetProperty("kind").GetString())
        {
            case "payload":
                var payload = value.ValueKind == JsonValueKind.Object
                    ? (object)value.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value.Clone()) : value.Clone();
                return call.Options.OnPayload is null ? payload : await call.Options.OnPayload(payload, call.Model).ConfigureAwait(false) ?? payload;
            case "response":
                var headers = value.GetProperty("headers").EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!);
                if (call.Options.OnResponse is not null) await call.Options.OnResponse(new(value.GetProperty("status").GetInt32(), headers), call.Model).ConfigureAwait(false);
                return null;
            case "streamEvent":
                if (call.Options.OnProviderStreamEvent is not null) await call.Options.OnProviderStreamEvent(value.Clone(), call.Model).ConfigureAwait(false);
                return null;
            case "headers":
                var original = value.EnumerateObject().ToDictionary(property => property.Name,
                    property => property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString(), StringComparer.OrdinalIgnoreCase);
                return call.Options.TransformHeaders is null ? original : await call.Options.TransformHeaders(original, call.Model).ConfigureAwait(false) ?? original;
            default: throw new InvalidOperationException("Unknown provider callback.");
        }
    }

    /// <summary>【CodingAgent】【提供方请求】输出原生消息、参数名称及回调能力，函数留在宿主。</summary>
    /// <param name="providerId">提供方。</param><param name="callId">调用标识。</param><param name="model">请求模型。</param>
    /// <param name="context">会话上下文。</param><param name="options">流式选项。</param><param name="simple">是否为简化调用。</param><returns>请求 JSON。</returns>
    private static JsonElement CreateProviderRequest(string providerId, string callId, Model model, LlmContext context, StreamOptions options, bool simple)
    {
        var messages = new JsonArray();
        foreach (var message in context.Messages) messages.Add(JsonSerializer.SerializeToNode(CodingAgentSessionStore.FromMessage(message), CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage));
        var settings = JsonSerializer.SerializeToNode(options, options.GetType(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject();
        foreach (var key in new[] { "timeout", "maxRetryDelay", "webSocketConnectTimeout", "reasoning" }) settings.Remove(key);
        settings["timeoutMs"] = options.Timeout?.TotalMilliseconds;
        settings["maxRetryDelayMs"] = options.MaxRetryDelay?.TotalMilliseconds;
        settings["websocketConnectTimeoutMs"] = options.WebSocketConnectTimeout?.TotalMilliseconds;
        settings["transport"] = options.Transport switch { StreamTransport.WebSocket => "websocket", StreamTransport.Auto => "auto", _ => "sse" };
        settings["cacheRetention"] = options.CacheRetention.ToString().ToLowerInvariant();
        if (options is SimpleStreamOptions simpleOptions && simpleOptions.Reasoning is { } reasoning) settings["reasoning"] = CodingAgentThinkingLevels.Format(reasoning);
        if (options is Tau.Ai.Providers.Bedrock.BedrockOptions { Reasoning: { } bedrockReasoning }) settings["reasoning"] = CodingAgentThinkingLevels.Format(bedrockReasoning);
        // 1. 【CodingAgent】【协议参数】内部强类型工具选择恢复为原生字符串或对象
        if (options is Tau.Ai.Providers.OpenAi.OpenAiOptions { ToolChoice: { } openaiChoice })
            settings["toolChoice"] = openaiChoice.IsFunction ? new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = openaiChoice.FunctionName } } : JsonValue.Create(openaiChoice.Kind);
        if (options is Tau.Ai.Providers.Anthropic.AnthropicOptions { ToolChoice: { } anthropicChoice })
            settings["toolChoice"] = anthropicChoice.IsTool ? new JsonObject { ["type"] = "tool", ["name"] = anthropicChoice.Name } : JsonValue.Create(anthropicChoice.Kind);
        if (options is Tau.Ai.Providers.Mistral.MistralOptions { ToolChoice: { } mistralChoice })
            settings["toolChoice"] = mistralChoice.FunctionName is { } functionName ? new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = functionName } } : JsonValue.Create(mistralChoice.Kind);
        var request = new JsonObject
        {
            ["providerId"] = providerId, ["callId"] = callId, ["simple"] = simple,
            ["model"] = JsonNode.Parse(CodingAgentExtensionSessionBridge.SerializeExtensionModel(model).GetRawText()),
            ["context"] = new JsonObject { ["messages"] = messages }, ["options"] = settings,
            ["callbacks"] = new JsonObject { ["payload"] = options.OnPayload is not null, ["response"] = options.OnResponse is not null,
                ["streamEvent"] = options.OnProviderStreamEvent is not null, ["headers"] = options.TransformHeaders is not null }
        };
        using var document = JsonDocument.Parse(request.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>【CodingAgent】【提供方事件】转换原生事件并保留思考、工具参数、用量与错误详情。</summary>
    /// <param name="frame">原生助手事件。</param><param name="model">默认模型身份。</param><returns>宿主事件。</returns>
    private static StreamEvent ReadProviderStreamEvent(JsonElement frame, Model model)
    {
        var type = ReadString(frame, "type");
        var field = type == "done" ? "message" : type == "error" ? "error" : "partial";
        if (!frame.TryGetProperty(field, out var source) || ReadChatMessage(source) is not AssistantMessage partial)
            throw new JsonException("Extension provider event requires an assistant message: " + type);
        partial = partial with { Api = model.Api, Provider = model.Provider, Model = model.Id };
        var index = ReadInt(frame, "contentIndex");
        var delta = ReadString(frame, "delta") ?? "";
        return type switch
        {
            "start" => new StartEvent(partial),
            "text_start" => new TextStartEvent(index, partial),
            "text_delta" => new TextDeltaEvent(index, delta, partial),
            "text_end" => new TextEndEvent(index, partial, ReadString(frame, "content"), ReadString(frame, "contentSignature")),
            "thinking_start" => new ThinkingStartEvent(index, partial),
            "thinking_delta" => new ThinkingDeltaEvent(index, delta, partial),
            "thinking_end" => new ThinkingEndEvent(index, partial, ReadString(frame, "content"), ReadString(frame, "contentSignature"), ReadBool(frame, "redacted")),
            "toolcall_start" => new ToolCallStartEvent(index, partial, ReadString(frame, "id"), ReadString(frame, "toolName")),
            "toolcall_delta" => new ToolCallDeltaEvent(index, delta, partial),
            "toolcall_end" => new ToolCallEndEvent(index, partial, partial.Content.ElementAtOrDefault(index) as ToolCallContent),
            "done" => new DoneEvent(partial),
            "error" => new ErrorEvent(partial.ErrorMessage ?? "Extension provider failed.", partial, partial),
            _ => throw new JsonException("Unknown extension provider event: " + type)
        };
    }
}
