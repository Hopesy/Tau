using System.Text;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Streaming;
using Tau.Ai.Utilities;
using Tau.Ai.Providers;

namespace Tau.Ai.Providers.PiMessages;

/// <summary>
/// 实现 pi-messages POST/SSE 协议，供 Radius 及兼容 gateway 使用。
/// </summary>
public sealed class PiMessagesProvider : IStreamProvider
{
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 创建 pi-messages provider。
    /// </summary>
    /// <param name="httpClient">可选 HTTP 客户端。</param>
    public PiMessagesProvider(HttpClient? httpClient = null) => _httpClient = httpClient ?? TauHttpClientFactory.Create();

    /// <summary>返回协议名称。</summary>
    public string Api => "pi-messages";
    /// <summary>【PiMessages】【会话声明】网关接收完整 transcript，不在客户端折叠系统增量。</summary>
    public bool SupportsTranscriptContext => true;

    /// <summary>
    /// 启动 pi-messages 流式请求。
    /// </summary>
    /// <param name="model">请求模型。</param>
    /// <param name="context">系统提示、消息和工具上下文。</param>
    /// <param name="options">请求选项。</param>
    /// <returns>异步 assistant 事件流。</returns>
    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var result = new AssistantMessageStream();
        _ = Task.Run(() => StreamAsync(model, context, options, result));
        return result;
    }

    /// <summary>【PiMessages】【请求入口】以简单选项启动原生会话流。</summary>
    /// <param name="model">请求模型。</param>
    /// <param name="context">会话上下文。</param>
    /// <param name="options">推理、工具选择及通用选项。</param>
    /// <returns>助手事件流。</returns>
    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Stream(model, context, options);

    /// <summary>【PiMessages】【报文传输】转换完整会话为网关 wire 数据并消费 SSE 响应。</summary>
    /// <param name="model">请求模型。</param>
    /// <param name="context">原始上下文。</param>
    /// <param name="options">请求与生命周期选项。</param>
    /// <param name="output">事件输出流。</param>
    /// <returns>请求处理任务。</returns>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Payload callbacks and tool details may contain caller-defined JSON-compatible objects.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Payload callbacks and tool details may contain caller-defined JSON-compatible objects.")]
    private async Task StreamAsync(Model model, LlmContext context, StreamOptions options, AssistantMessageStream output)
    {
        AssistantMessage? partial = null;
        var terminalSeen = false;
        try
        {
            var baseUrl = (model.BaseUrl ?? string.Empty).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("pi-messages model baseUrl is required.");
            if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new InvalidOperationException($"No API key provided for provider \"{model.Provider}\".");
            var typed = options as PiMessagesOptions;
            var simple = options as SimpleStreamOptions;
            var requestOptions = new Dictionary<string, object?>
            {
                ["temperature"] = options.Temperature,
                ["maxTokens"] = options.MaxTokens,
                ["reasoning"] = simple?.Reasoning switch { null or ThinkingLevel.Off => null, ThinkingLevel.ExtraHigh => "xhigh", var reasoning => reasoning.ToString()!.ToLowerInvariant() },
                ["cacheRetention"] = ResolveCacheRetention(options),
                ["sessionId"] = options.SessionId,
                ["toolChoice"] = simple?.ToolChoice
            };
            var body = new Dictionary<string, object>
            {
                ["model"] = model.Id,
                ["context"] = PiMessagesContextSerializer.Convert(context, model),
                ["options"] = requestOptions.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!)
            };
            var transformed = await StreamOptionHelpers.ApplyPayloadCallbackAsync(options, model, body).ConfigureAwait(false);
            var endpoint = $"{baseUrl}/messages" + (typed?.Debug == true ? "?debug=1" : string.Empty);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(transformed, JsonOptions), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
            request.Headers.Accept.ParseAdd("text/event-stream");
            ApplyHeaders(request, model.Headers);
            ApplyHeaders(request, options.Headers);
            await StreamOptionHelpers.ApplyHeadersCallbackAsync(options, model, request).ConfigureAwait(false);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, options.Signal).ConfigureAwait(false);
            if (options.OnResponse is not null)
            {
                await options.OnResponse(
                    new ProviderResponse((int)response.StatusCode, response.Headers.ToDictionary(x => x.Key, x => x.Value.FirstOrDefault() ?? string.Empty)),
                    model).ConfigureAwait(false);
            }
            if (!response.IsSuccessStatusCode)
            {
                var rawBody = await response.Content.ReadAsStringAsync(options.Signal).ConfigureAwait(false);
                throw new PiMessagesResponseException(
                    ErrorBody.Format(rawBody, $"{(int)response.StatusCode} {response.ReasonPhrase}"),
                    diagnosticDetails: new Dictionary<string, object?> { ["status"] = (int)response.StatusCode, ["body"] = rawBody });
            }

            partial = new AssistantMessage
            {
                Api = Api,
                Provider = model.Provider,
                Model = model.Id,
                Content = [],
                Usage = CreateEmptyUsage()
            };
            output.Push(new StartEvent(partial));
            await using var stream = await response.Content.ReadAsStreamAsync(options.Signal).ConfigureAwait(false);
            await foreach (var sse in SseParser.ParseAsync(stream, options.Signal))
            {
                if (string.IsNullOrWhiteSpace(sse.Data)) continue;
                if (sse.Data.Trim().Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) continue;
                using var document = JsonDocument.Parse(sse.Data);
                if (options.OnProviderStreamEvent is not null) await options.OnProviderStreamEvent(document.RootElement.Clone(), model).ConfigureAwait(false);
                partial = ApplyEvent(document.RootElement, partial, output, ref terminalSeen);
                if (terminalSeen) break;
            }
            if (!terminalSeen)
            {
                var error = new InvalidOperationException($"{model.Provider} stream ended without a terminal event");
                var errored = partial with { StopReason = StopReason.Error, ErrorMessage = error.Message, Timestamp = DateTimeOffset.UtcNow };
                output.Push(new ErrorEvent(error.Message, partial, errored));
            }
        }
        catch (OperationCanceledException) when (options.Signal.IsCancellationRequested)
        {
            if (!terminalSeen)
                output.Push(new ErrorEvent("Request was aborted", partial, StreamOptionHelpers.CreateAbortedMessage(model, Api, partial)));
        }
        catch (Exception ex)
        {
            if (!terminalSeen)
            {
                var message = partial ?? new AssistantMessage
                {
                    Api = Api,
                    Provider = model.Provider,
                    Model = model.Id,
                    Content = [],
                    Usage = CreateEmptyUsage()
                };
                var details = new Dictionary<string, object?> { ["provider"] = model.Provider, ["model"] = model.Id };
                if (ex is PiMessagesResponseException responseException)
                    foreach (var pair in responseException.DiagnosticDetails) details[pair.Key] = pair.Value;
                var diagnostic = AssistantMessageDiagnostics.CreateAssistantMessageDiagnostic("pi_messages_response_failure", ex, details);
                var errored = AssistantMessageDiagnostics.AppendAssistantMessageDiagnostic(message with { StopReason = StopReason.Error, ErrorMessage = ex.Message, Timestamp = DateTimeOffset.UtcNow }, diagnostic);
                output.Push(new ErrorEvent(ex.Message, message, errored));
            }
        }
    }

    /// <summary>【PiMessages】【响应重放】应用流事件并把内容签名写回最终消息，供下一请求继续使用。</summary>
    /// <param name="eventElement">服务端事件。</param>
    /// <param name="partial">当前助手状态。</param>
    /// <param name="output">事件输出流。</param>
    /// <param name="terminalSeen">是否已接收终态。</param>
    /// <returns>更新后的助手消息。</returns>
    private static AssistantMessage ApplyEvent(JsonElement eventElement, AssistantMessage partial, AssistantMessageStream output, ref bool terminalSeen)
    {
        var type = eventElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        var index = eventElement.TryGetProperty("contentIndex", out var indexElement) && indexElement.TryGetInt32(out var value) ? value : 0;
        switch (type)
        {
            case "text_start":
                partial = ReplaceContent(partial, index, _ => new TextContent(string.Empty));
                output.Push(new TextStartEvent(index, partial));
                break;
            case "text_delta":
                var textDelta = eventElement.TryGetProperty("delta", out var delta) ? delta.GetString() ?? string.Empty : string.Empty;
                partial = AppendContent(partial, index, new TextContent(textDelta), merge: true);
                output.Push(new TextDeltaEvent(index, textDelta, partial));
                break;
            case "text_end":
                var text = GetString(eventElement, "content");
                partial = ReplaceContent(partial, index, block => block is TextContent textBlock
                    ? textBlock with { Text = text ?? textBlock.Text, TextSignature = GetString(eventElement, "contentSignature") ?? textBlock.TextSignature } : block);
                output.Push(new TextEndEvent(index, partial, text, GetString(eventElement, "contentSignature")));
                break;
            case "thinking_start":
                partial = ReplaceContent(partial, index, _ => new ThinkingContent(string.Empty));
                output.Push(new ThinkingStartEvent(index, partial));
                break;
            case "thinking_delta":
                var thinkingDelta = eventElement.TryGetProperty("delta", out var thinking) ? thinking.GetString() ?? string.Empty : string.Empty;
                partial = AppendContent(partial, index, new ThinkingContent(thinkingDelta), merge: true);
                output.Push(new ThinkingDeltaEvent(index, thinkingDelta, partial));
                break;
            case "thinking_end":
                var thinkingText = GetString(eventElement, "content");
                var isRedacted = eventElement.TryGetProperty("redacted", out var redacted) && redacted.ValueKind == JsonValueKind.True;
                partial = ReplaceContent(partial, index, block => block is ThinkingContent thinkingBlock
                    ? thinkingBlock with { Thinking = thinkingText ?? thinkingBlock.Thinking,
                        ThinkingSignature = GetString(eventElement, "contentSignature") ?? thinkingBlock.ThinkingSignature, Redacted = isRedacted } : block);
                output.Push(new ThinkingEndEvent(index, partial, thinkingText, GetString(eventElement, "contentSignature"), isRedacted));
                break;
            case "toolcall_start":
                var toolId = GetString(eventElement, "id") ?? string.Empty;
                var toolName = GetString(eventElement, "toolName") ?? GetString(eventElement, "name") ?? string.Empty;
                partial = ReplaceContent(partial, index, _ => new ToolCallContent(toolId, toolName, "{}"));
                output.Push(new ToolCallStartEvent(index, partial, toolId, toolName));
                break;
            case "toolcall_delta":
            {
                var toolDeltaText = eventElement.TryGetProperty("delta", out var toolDelta) ? toolDelta.GetString() ?? string.Empty : string.Empty;
                if (index < partial.Content.Count && partial.Content[index] is ToolCallContent currentCall)
                    partial = ReplaceContent(partial, index, _ => currentCall with { Arguments = currentCall.Arguments == "{}" ? toolDeltaText : currentCall.Arguments + toolDeltaText });
                output.Push(new ToolCallDeltaEvent(index, toolDeltaText, partial));
                break;
            }
            case "toolcall_end":
            {
                if (eventElement.TryGetProperty("toolCall", out var call) && call.ValueKind == JsonValueKind.Object)
                {
                    var toolCall = new ToolCallContent(call.TryGetProperty("id", out var callId) ? callId.GetString() ?? string.Empty : string.Empty, call.TryGetProperty("toolName", out var callName) ? callName.GetString() ?? string.Empty : call.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty, call.TryGetProperty("arguments", out var arguments) ? arguments.ValueKind == JsonValueKind.String ? arguments.GetString() ?? "{}" : arguments.GetRawText() : "{}");
                    toolCall = toolCall with { ThoughtSignature = GetString(call, "thoughtSignature"), Namespace = GetString(call, "namespace") };
                    partial = AppendContent(partial, index, toolCall, merge: false);
                    output.Push(new ToolCallEndEvent(index, partial, toolCall));
                }
                else output.Push(new ToolCallEndEvent(index, partial));
                break;
            }
            case "done":
                terminalSeen = true;
                partial = partial with { ProviderThinkingLevel = GetString(eventElement, "providerThinkingLevel") ?? partial.ProviderThinkingLevel };
                partial = partial with { StopReason = ParseStopReason(eventElement), Usage = ParseUsage(eventElement), ResponseId = GetString(eventElement, "responseId"), Timestamp = DateTimeOffset.UtcNow };
                partial = AppendRewriteDiagnostic(partial, eventElement);
                output.Push(new DoneEvent(partial));
                break;
            case "error":
                terminalSeen = true;
                partial = partial with { ProviderThinkingLevel = GetString(eventElement, "providerThinkingLevel") ?? partial.ProviderThinkingLevel };
                var errorMessage = GetString(eventElement, "errorMessage") ?? "pi-messages error";
                partial = partial with { StopReason = ParseStopReason(eventElement), ErrorMessage = errorMessage, Usage = ParseUsage(eventElement), ResponseId = GetString(eventElement, "responseId"), Timestamp = DateTimeOffset.UtcNow };
                partial = AppendRewriteDiagnostic(partial, eventElement);
                output.Push(new ErrorEvent(errorMessage, Message: partial));
                break;
        }
        return partial;
    }

    private static AssistantMessage ReplaceContent(AssistantMessage message, int index, Func<ContentBlock, ContentBlock> replace)
    {
        var content = message.Content.ToList();
        while (content.Count <= index) content.Add(new TextContent(string.Empty));
        content[index] = replace(content[index]);
        return message with { Content = content };
    }

    private static AssistantMessage AppendContent(AssistantMessage message, int index, ContentBlock block, bool merge)
    {
        var content = message.Content.ToList();
        while (content.Count <= index) content.Add(new TextContent(string.Empty));
        if (merge && content[index] is TextContent text && block is TextContent next) content[index] = text with { Text = text.Text + next.Text };
        else if (merge && content[index] is ThinkingContent thinking && block is ThinkingContent nextThinking) content[index] = thinking with { Thinking = thinking.Thinking + nextThinking.Thinking };
        else content[index] = block;
        return message with { Content = content };
    }

    private static StopReason ParseStopReason(JsonElement element) => (GetString(element, "reason") ?? "stop") switch { "length" => StopReason.MaxTokens, "toolUse" => StopReason.ToolUse, "error" => StopReason.Error, "aborted" => StopReason.Aborted, _ => StopReason.EndTurn };
    /// <summary>【PiMessages】【用量恢复】读取服务端 token、缓存细分和计费数据。</summary>
    /// <param name="element">终态事件。</param>
    /// <returns>助手用量。</returns>
    private static Usage ParseUsage(JsonElement element)
    {
        if (!element.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return CreateEmptyUsage();
        var result = new Usage(ReadInt(usage, "input"), ReadInt(usage, "output"), ReadNullableInt(usage, "cacheRead"), ReadNullableInt(usage, "cacheWrite"))
        {
            ReasoningTokens = ReadNullableInt(usage, "reasoning"),
            CacheWrite1hTokens = ReadNullableInt(usage, "cacheWrite1h"),
            TotalTokens = ReadNullableInt(usage, "totalTokens")
        };
        if (usage.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object)
            result = result with { Cost = new UsageCost(ReadDecimal(cost, "input"), ReadDecimal(cost, "output"), ReadDecimal(cost, "cacheRead"), ReadDecimal(cost, "cacheWrite")) };
        return result;
    }

    private static Usage CreateEmptyUsage() => new(0, 0, 0, 0)
    {
        TotalTokens = 0,
        Cost = new UsageCost(0, 0, 0, 0)
    };

    private static AssistantMessage AppendRewriteDiagnostic(AssistantMessage message, JsonElement eventElement)
    {
        if (!eventElement.TryGetProperty("rewrite", out var rewrite) || rewrite.ValueKind != JsonValueKind.Object)
            return message;

        var details = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in rewrite.EnumerateObject())
        {
            details[property.Name] = property.Value.Clone();
        }

        var diagnostic = AssistantMessageDiagnostics.CreateAssistantMessageDiagnostic(
            "pi_messages_rewrite",
            new InvalidOperationException("pi-messages backend rewrote the request"),
            details);
        return AssistantMessageDiagnostics.AppendAssistantMessageDiagnostic(message, diagnostic);
    }

    private static int ReadInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static int? ReadNullableInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static decimal ReadDecimal(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetDecimal(out var result) ? result : 0m;
    /// <summary>【PiMessages】【缓存选项】显式 none 优先于环境配置，未指定时保留网关默认值。</summary>
    /// <param name="options">请求选项。</param>
    /// <returns>网关缓存策略或空。</returns>
    private static object? ResolveCacheRetention(StreamOptions options)
    {
        if (options.HasExplicitCacheRetention)
        {
            return options.CacheRetention.ToString().ToLowerInvariant();
        }

        return string.Equals(
            ProviderEnvironment.GetValue("PI_CACHE_RETENTION", options.Env),
            "long",
            StringComparison.OrdinalIgnoreCase)
            ? "long"
            : null;
    }
    private static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static void ApplyHeaders(HttpRequestMessage request, IDictionary<string, string>? headers) { if (headers is null) return; foreach (var pair in headers) { request.Headers.Remove(pair.Key); if (!string.IsNullOrEmpty(pair.Value)) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value); } }
}
