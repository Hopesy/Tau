// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Providers.Google;
using Tau.Ai.Providers.Bedrock;
using Tau.Ai.Providers.Mistral;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private static readonly JsonSerializerOptions RegistryStreamJson = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>【CodingAgent】【目录调用】通过会话配置的真实提供方执行独立模型请求，不修改 Agent 会话消息。</summary>
    /// <param name="request">模型、上下文、选项和回调标识。</param><param name="extensions">回调桥接。</param>
    /// <param name="progress">原生流事件接收器。</param><param name="token">调用取消信号。</param><returns>流终止任务。</returns>
    internal async Task StreamRegistryModelAsync(JsonElement request, CodingAgentJavaScriptExtensionRuntime extensions, Action<JsonElement>? progress, CancellationToken token)
    {
        var provider = request.GetProperty("provider").GetString()!;
        var model = _modelCatalog.GetModel(provider, request.GetProperty("modelId").GetString()!);
        AssistantMessage last = new() { Api = model.Api, Provider = model.Provider, Model = model.Id, Content = [], Timestamp = DateTimeOffset.UtcNow };
        var ended = false;
        try
        {
            // 1. 【CodingAgent】【目录调用】建立独立上下文和协议参数，绑定本次流的取消与回调
            var context = ReadRegistryContext(request.GetProperty("context"));
            var simple = request.GetProperty("simple").GetBoolean();
            var options = ReadRegistryOptions(request.GetProperty("options"), model.Api, simple);
            if (options.ApiKey is null && model.Provider == Model.Provider) options = options with { ApiKey = _config.StreamOptions?.ApiKey };
            if (model.Api == "pi-virtual")
            {
                if (!simple) throw new InvalidOperationException($"Virtual model {model.Provider}/{model.Id} must be routed before streaming");
                var selectedProvider = model.Provider;
                var route = await ResolveVirtualModelAsync(extensions, model, Transcript.NormalizeContext(context).Messages,
                    (options as SimpleStreamOptions)?.Reasoning, "direct", token).ConfigureAwait(false);
                model = route.Model;
                options = (SimpleStreamOptions)options with { Reasoning = route.Reasoning,
                    MaxTokens = options.MaxTokens is { } maximum && model.MaxOutputTokens is > 0 ? Math.Min(maximum, model.MaxOutputTokens.Value) : options.MaxTokens,
                    ApiKey = model.Provider == selectedProvider ? options.ApiKey : null,
                    Headers = model.Provider == selectedProvider ? options.Headers : null,
                    Env = model.Provider == selectedProvider ? options.Env : null };
            }
            options = extensions.BindRegistryCallbacks(request, model, options with { Signal = token });
            var stream = simple
                ? StreamFunctions.StreamSimple(_config.ProviderRegistry, model, context, (SimpleStreamOptions)options, _modelCatalog.ConfigurationStore, _authResolver)
                : StreamFunctions.Stream(_config.ProviderRegistry, model, context, options, _modelCatalog.ConfigurationStore, _authResolver);
            // 2. 【CodingAgent】【目录调用】逐帧发布不可变快照，不向 Agent 消息历史追加结果
            await foreach (var frame in stream.WithCancellation(token).ConfigureAwait(false))
            {
                var native = SerializeRegistryFrame(frame, model, ref last);
                ended = frame is DoneEvent or ErrorEvent;
                progress?.Invoke(native);
                if (ended) break;
            }
            if (!ended) throw new InvalidOperationException("Model stream ended without a terminal event.");
        }
        catch (Exception error)
        {
            // 3. 【CodingAgent】【目录调用】异常和取消保留最近正文，并生成唯一终止帧
            if (!ended)
            {
                last = last with { StopReason = token.IsCancellationRequested ? StopReason.Aborted : StopReason.Error,
                    ErrorMessage = token.IsCancellationRequested ? "Request aborted" : error.Message };
                progress?.Invoke(SerializeRegistryFrame(new ErrorEvent(last.ErrorMessage!, last, last), model, ref last));
            }
        }
    }

    /// <summary>【CodingAgent】【目录上下文】转换原生消息、系统提示及工具声明。</summary>
    /// <param name="value">原生上下文。</param><returns>独立请求上下文。</returns>
    private static LlmContext ReadRegistryContext(JsonElement value)
    {
        var messages = value.GetProperty("messages").EnumerateArray().Select(message =>
            CodingAgentSessionStore.ToMessage(JsonSerializer.Deserialize(message, CodingAgentSessionJsonContext.Default.CodingAgentSessionMessage)! )
            ?? throw new JsonException("Unsupported model context message.")).ToArray();
        var tools = value.TryGetProperty("tools", out var definitions) && definitions.ValueKind == JsonValueKind.Array ? definitions.EnumerateArray().Select(tool =>
            new Tool(tool.GetProperty("name").GetString()!, tool.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "", tool.GetProperty("parameters").Clone())
            { ConstrainedSampling = tool.TryGetProperty("constrainedSampling", out var sampling) ? sampling.Deserialize<ConstrainedSamplingConfig>(RegistryStreamJson) : null }).ToArray() : null;
        return new(value.TryGetProperty("systemPrompt", out var system) ? system.GetString() : null, messages, tools);
    }

    /// <summary>【CodingAgent】【目录参数】恢复时间、枚举和协议专用参数，未知字段保留供自定义提供方使用。</summary>
    /// <param name="value">原生请求选项。</param><param name="api">协议。</param><param name="simple">是否简化请求。</param><returns>真实请求选项。</returns>
    private static StreamOptions ReadRegistryOptions(JsonElement value, string api, bool simple)
    {
        // 1. 【CodingAgent】【目录参数】将原生毫秒和推理等级转为宿主类型
        var node = JsonNode.Parse(value.GetRawText())!.AsObject();
        foreach (var (native, managed) in new[] { ("timeoutMs", "timeout"), ("maxRetryDelayMs", "maxRetryDelay"), ("websocketConnectTimeoutMs", "webSocketConnectTimeout") })
            if (node.Remove(native, out var milliseconds) && milliseconds is not null) node[managed] = TimeSpan.FromMilliseconds(milliseconds.GetValue<double>()).ToString("c");
        if (node["reasoning"]?.GetValue<string>() == "xhigh") node["reasoning"] = "ExtraHigh";
        var type = simple ? typeof(SimpleStreamOptions) : api switch
        {
            "openai-chat-completions" => typeof(OpenAiOptions), "openai-responses" => typeof(OpenAiResponsesOptions),
            "openai-codex-responses" => typeof(OpenAiCodexResponsesOptions), "azure-openai-responses" => typeof(AzureOpenAiResponsesOptions),
            "anthropic-messages" => typeof(AnthropicOptions), "mistral-conversations" => typeof(MistralOptions),
            "google-generative-language" => typeof(GoogleOptions), "google-gemini-cli" => typeof(GoogleGeminiCliOptions),
            "google-vertex" => typeof(GoogleVertexOptions), "bedrock-converse-stream" => typeof(BedrockOptions),
            "pi-messages" => typeof(PiMessagesOptions), _ => typeof(StreamOptions)
        };
        // 2. 【CodingAgent】【目录参数】单独处理具有私有构造器的协议工具选择类型
        var choice = node["toolChoice"]?.DeepClone();
        if (type == typeof(OpenAiOptions) || type == typeof(AnthropicOptions) || type == typeof(MistralOptions)) node.Remove("toolChoice");
        var options = (StreamOptions)node.Deserialize(type, RegistryStreamJson)!;
        if (choice is not null)
        {
            var kind = choice is JsonValue scalar ? scalar.GetValue<string>() : choice["type"]?.GetValue<string>() ?? "auto";
            var name = choice is JsonObject obj ? obj["name"]?.GetValue<string>() ?? obj["function"]?["name"]?.GetValue<string>() : null;
            options = options switch
            {
                OpenAiOptions openai => openai with { ToolChoice = name is null ? OpenAiToolChoice.FromString(kind) : OpenAiToolChoice.Function(name) },
                AnthropicOptions anthropic => anthropic with { ToolChoice = name is null ? AnthropicToolChoice.FromString(kind) : AnthropicToolChoice.Tool(name) },
                MistralOptions mistral => mistral with { ToolChoice = name is null ? MistralToolChoice.FromString(kind) : MistralToolChoice.Function(name) },
                _ => options
            };
        }
        return options;
    }

    /// <summary>【CodingAgent】【目录事件】按原生消息协议写入流帧，保留最后一份部分消息用于取消。</summary>
    /// <param name="frame">宿主流帧。</param><param name="model">目标模型。</param><param name="last">最近消息。</param><returns>原生事件快照。</returns>
    private static JsonElement SerializeRegistryFrame(StreamEvent frame, Model model, ref AssistantMessage last)
    {
        var node = JsonSerializer.SerializeToNode(frame, frame.GetType(), RegistryStreamJson)!.AsObject();
        last = frame switch
        {
            StartEvent e => e.Partial, TextStartEvent e => e.Partial, TextDeltaEvent e => e.Partial, TextEndEvent e => e.Partial,
            ThinkingStartEvent e => e.Partial, ThinkingDeltaEvent e => e.Partial, ThinkingEndEvent e => e.Partial,
            ToolCallStartEvent e => e.Partial, ToolCallDeltaEvent e => e.Partial, ToolCallEndEvent e => e.Partial,
            DoneEvent e => e.Message, ErrorEvent e => e.Message ?? e.Partial ?? last, _ => last
        };
        last = last with { Api = model.Api, Provider = model.Provider, Model = model.Id };
        if (frame is ErrorEvent failure) last = last with { StopReason = last.StopReason == StopReason.Aborted ? StopReason.Aborted : StopReason.Error, ErrorMessage = last.ErrorMessage ?? failure.Error };
        var message = JsonSerializer.SerializeToNode(CodingAgentSessionStore.FromMessage(last), CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage)!;
        var field = frame is DoneEvent ? "message" : frame is ErrorEvent ? "error" : "partial";
        node[field] = message;
        if (frame is DoneEvent or ErrorEvent) node["reason"] = message["stopReason"]?.DeepClone();
        if (frame is ErrorEvent) { node.Remove("partial"); node.Remove("message"); }
        if (frame is ToolCallEndEvent tool && message["content"] is JsonArray content && tool.ContentIndex < content.Count)
            node["toolCall"] = content[tool.ContentIndex]?.DeepClone();
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【目录回调】将 Node 持有的函数绑定到真实请求钩子，允许回调重入目录操作。</summary>
    /// <param name="request">调用及能力标识。</param><param name="model">请求模型。</param><param name="options">请求选项。</param><returns>绑定后的同类型选项。</returns>
    internal StreamOptions BindRegistryCallbacks(JsonElement request, Model model, StreamOptions options)
    {
        var callbacks = request.GetProperty("callbacks");
        var file = request.GetProperty("filePath").GetString()!;
        var callId = request.GetProperty("callId").GetString()!;
        var generation = ResetGeneration;
        return options with
        {
            OnPayload = ReadBool(callbacks, "payload") ? async (payload, actual) =>
            {
                var value = await InvokeRegistryCallbackAsync(file, callId, generation, "payload", payload, actual, options.Signal).ConfigureAwait(false);
                return value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().ToDictionary(field => field.Name, field => (object)field.Value.Clone()) : (object?)null;
            } : null,
            OnResponse = ReadBool(callbacks, "response") ? async (response, actual) => { await InvokeRegistryCallbackAsync(file, callId, generation, "response", response, actual, options.Signal).ConfigureAwait(false); } : null,
            OnProviderStreamEvent = ReadBool(callbacks, "streamEvent") ? async (value, actual) => { await InvokeRegistryCallbackAsync(file, callId, generation, "streamEvent", value, actual, options.Signal).ConfigureAwait(false); } : null,
            TransformHeaders = ReadBool(callbacks, "headers") ? async (headers, actual) =>
            {
                var value = await InvokeRegistryCallbackAsync(file, callId, generation, "headers", headers, actual, options.Signal).ConfigureAwait(false);
                return value.ValueKind == JsonValueKind.Null ? headers : value.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.ValueKind == JsonValueKind.Null ? null : field.Value.GetString(), StringComparer.OrdinalIgnoreCase);
            } : null
        };
    }

    /// <summary>【CodingAgent】【目录回调】用独立标识执行原请求闭包中的回调，不附带重复会话快照。</summary>
    /// <param name="file">扩展文件。</param><param name="callId">流标识。</param><param name="generation">运行时代次。</param>
    /// <param name="kind">回调类型。</param><param name="value">参数。</param><param name="model">实际请求模型。</param><param name="token">取消信号。</param><returns>回调值。</returns>
    private async Task<JsonElement> InvokeRegistryCallbackAsync(string file, string callId, long generation, string kind, object value, Model model, CancellationToken token)
    {
        var request = JsonSerializer.SerializeToElement(new { callId, kind, value, model = CodingAgentExtensionSessionBridge.SerializeExtensionModel(model) },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var result = await ExecuteAsync(BuildPayload("modelRegistryCallback", file, _cwd, toolArgs: request), token, expectedGeneration: generation).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        using var document = JsonDocument.Parse(result.ResultJson);
        if (!ReadBool(document.RootElement, "ok")) throw new InvalidOperationException(ReadString(document.RootElement, "error"));
        return document.RootElement.GetProperty("value").Clone();
    }
}
