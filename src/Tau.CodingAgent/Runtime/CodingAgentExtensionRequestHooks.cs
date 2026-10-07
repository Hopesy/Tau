// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【请求钩子】连接扩展上下文转换和提供方请求、响应回调。</summary>
public sealed partial class CodingAgentExtensionLifecycleEventSink
{
    /// <summary>【CodingAgent】【上下文钩子】先执行普通上下文处理器，再执行拥有完整系统状态的处理器。</summary>
    /// <param name="messages">待请求的完整会话副本来源。</param>
    /// <param name="reportError">处理器错误报告回调。</param>
    /// <param name="token">取消信号。</param>
    /// <returns>仅用于本次模型请求的消息，不修改持久化历史。</returns>
    public IReadOnlyList<ChatMessage> TransformRequestContext(
        IReadOnlyList<ChatMessage> messages, Action<CodingAgentExtensionLifecycleEventError> reportError, CancellationToken token)
    {
        var current = messages;
        foreach (var eventType in new[] { "context", "context_with_system" })
        {
            foreach (var module in _modules.Where(module => module.EventTypes.Contains(eventType, StringComparer.Ordinal)))
            {
                token.ThrowIfCancellationRequested();
                // 1. 【CodingAgent】【消息隔离】标准消息序列化隔离对象引用，扩展只修改本次请求的投影
                var payload = new JsonObject { ["type"] = eventType, ["messages"] = new JsonArray() };
                foreach (var message in current)
                    payload["messages"]!.AsArray().Add(JsonSerializer.SerializeToNode(
                        CodingAgentSessionStore.FromMessage(message), CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage));
                var result = EmitRequestHook(module, JsonSerializer.SerializeToElement(payload), reportError, token);
                if (result is not { } transformed || !transformed.TryGetProperty("messages", out var returned)) continue;
                try
                {
                    if (returned.ValueKind != JsonValueKind.Array) throw new JsonException("Context messages must be an array.");
                    current = returned.EnumerateArray().Select(message =>
                    {
                        var stored = JsonSerializer.Deserialize(message, CodingAgentSessionJsonContext.Default.CodingAgentSessionMessage)
                            ?? throw new JsonException("Invalid context message.");
                        return CodingAgentSessionStore.ToMessage(stored) ?? throw new JsonException("Unsupported context message role.");
                    }).ToArray();
                }
                catch (JsonException ex) { reportError(new(module.FilePath, module.Scope, module.Runtime, eventType, ex.Message)); }
            }
        }
        return current;
    }

    /// <summary>【CodingAgent】【提供方钩子】保留调用方回调，并按扩展顺序转换请求、通知最终响应。</summary>
    /// <param name="options">本次请求的已有选项。</param>
    /// <param name="reportError">扩展错误报告回调。</param>
    /// <returns>包含真实提供方回调的选项副本。</returns>
    public SimpleStreamOptions WrapRequestOptions(SimpleStreamOptions options, Action<CodingAgentExtensionLifecycleEventError> reportError)
    {
        var payloadModules = _modules.Where(module => module.EventTypes.Contains("before_provider_request", StringComparer.Ordinal)).ToArray();
        var responseModules = _modules.Where(module => module.EventTypes.Contains("after_provider_response", StringComparer.Ordinal)).ToArray();
        var headerModules = _modules.Where(module => module.EventTypes.Contains("before_provider_headers", StringComparer.Ordinal)).ToArray();
        var streamModules = _modules.Where(module => module.EventTypes.Contains("provider_stream_event", StringComparer.Ordinal)).ToArray();
        return options with
        {
            TransformHeaders = headerModules.Length == 0 ? options.TransformHeaders : async (headers, model) =>
            {
                var current = options.TransformHeaders is null ? headers : await options.TransformHeaders(headers, model).ConfigureAwait(false) ?? headers;
                foreach (var module in headerModules)
                {
                    options.Signal.ThrowIfCancellationRequested();
                    var result = EmitRequestHook(module, JsonSerializer.SerializeToElement(new { type = "before_provider_headers", headers = current }), reportError, options.Signal);
                    if (result is { } transformed && transformed.TryGetProperty("headers", out var replacement) && replacement.ValueKind == JsonValueKind.Object)
                    {
                        var updated = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var property in replacement.EnumerateObject())
                            updated[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                        current = updated;
                    }
                }
                return current;
            },
            OnProviderStreamEvent = streamModules.Length == 0 ? options.OnProviderStreamEvent : async (data, model) =>
            {
                if (options.OnProviderStreamEvent is not null) await options.OnProviderStreamEvent(data, model).ConfigureAwait(false);
                var payload = JsonSerializer.SerializeToElement(new { type = "provider_stream_event", provider = model.Provider, api = model.Api, model = model.Id, data });
                foreach (var module in streamModules)
                {
                    options.Signal.ThrowIfCancellationRequested();
                    EmitRequestHook(module, payload, reportError, options.Signal);
                }
            },
            OnPayload = payloadModules.Length == 0 ? options.OnPayload : async (payload, model) =>
            {
                var original = options.OnPayload is null ? payload : await options.OnPayload(payload, model).ConfigureAwait(false) ?? payload;
                var current = JsonSerializer.SerializeToElement(original, original.GetType());
                // 1. 【CodingAgent】【请求体钩子】返回值直接作为下一处理器的 payload，同时保留原位修改
                foreach (var module in payloadModules)
                {
                    options.Signal.ThrowIfCancellationRequested();
                    var result = EmitRequestHook(module, JsonSerializer.SerializeToElement(new { type = "before_provider_request", payload = current }), reportError, options.Signal);
                    if (result is { } transformed && transformed.TryGetProperty("payload", out var replacement)) current = replacement.Clone();
                }
                if (current.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Provider request payload must be an object.");
                return current.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value.Clone(), StringComparer.Ordinal);
            },
            OnResponse = responseModules.Length == 0 ? options.OnResponse : async (response, model) =>
            {
                if (options.OnResponse is not null) await options.OnResponse(response, model).ConfigureAwait(false);
                var payload = JsonSerializer.SerializeToElement(new { type = "after_provider_response", status = response.Status, headers = response.Headers });
                foreach (var module in responseModules)
                {
                    options.Signal.ThrowIfCancellationRequested();
                    EmitRequestHook(module, payload, reportError, options.Signal);
                }
            }
        };
    }

    /// <summary>【CodingAgent】【钩子调用】调用单个模块并隔离处理器失败，返回完整转换事件。</summary>
    /// <param name="module">目标模块。</param>
    /// <param name="payload">事件参数。</param>
    /// <param name="reportError">错误报告回调。</param>
    /// <returns>转换后的事件；失败或通知事件返回空值。</returns>
    private JsonElement? EmitRequestHook(CodingAgentExtensionLifecycleEventModule module, JsonElement payload,
        Action<CodingAgentExtensionLifecycleEventError> reportError, CancellationToken token)
    {
        var eventType = payload.GetProperty("type").GetString()!;
        var result = _runtime.EmitEvent(module.FilePath, payload, token);
        if (!result.Success) reportError(new(module.FilePath, module.Scope, module.Runtime, eventType, result.Error ?? "Extension request hook failed."));
        foreach (var error in result.HandlerErrors) reportError(new(module.FilePath, module.Scope, module.Runtime, eventType, error));
        return result.Success ? result.TransformedEvent : null;
    }
}
