using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.OpenAiResponses;

public static class OpenAiResponsesShared
{
    public const int OpenAiPromptCacheKeyMaxLength = 64;

    private static readonly HashSet<string> DefaultToolCallProviders = new(StringComparer.Ordinal)
    {
        "openai",
        "openai-codex",
        "opencode"
    };

    /// <summary>【AI】【Responses】转换完整上下文并保留模型支持的中途系统声明。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">旧式或消息式上下文。</param>
    /// <returns>Responses input 项。</returns>
    public static List<object> ConvertResponsesMessages(Model model, LlmContext context) =>
        ConvertResponsesMessages(model, context, includeSystemPrompt: true);

    /// <summary>【AI】【Responses】转换消息，允许 Codex 单独通过 instructions 发送开场提示。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">旧式或消息式上下文。</param>
    /// <param name="includeSystemPrompt">是否在 input 中包含开场提示；中途指令始终按能力处理。</param>
    /// <param name="grammarToolInputProperties">折叠前的历史语法输入映射；未提供时从原始会话计算。</param>
    /// <returns>保持声明位置的 Responses input 项。</returns>
    public static List<object> ConvertResponsesMessages(Model model, LlmContext context, bool includeSystemPrompt,
        IReadOnlyDictionary<string, string>? grammarToolInputProperties = null)
    {
        grammarToolInputProperties ??= CreateGrammarToolInputProperties(model, context);
        context = MessageTransformer.DowngradeUnsupportedImages(context, model);
        context = Transcript.ResolveTranscript(context, model.Compat?.SupportsMidConvoSystemMessages == true);
        var transcriptTools = Transcript.ResolveTranscriptTools(context.Messages,
            model.Compat?.SupportsAdditionalTools == true || model.Compat?.SupportsToolSearch == true);
        // 1. 【AI】【历史重放】统一处理签名来源、中断消息和系统更新期间的工具配对
        var transformed = MessageTransformer.TransformMessages(context.Messages, model, NormalizeToolCallId);
        var messages = new List<object>();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceIndex = 0;
        var messageIndex = 0;

        // 2. 【AI】【协议转换】文本项与工具加载项共用转换后消息序号，跳过无输出的消息
        foreach (var message in transformed)
        {
            var isInitialSystem = sourceIndex++ == 0 && message is SystemMessage;
            switch (message)
            {
                case SystemMessage system:
                    if (!isInitialSystem && transcriptTools.AnchorsAdditions && system.ToolsAdded is { Count: > 0 })
                        AppendSystemToolAdditions(messages, system.ToolsAdded, model, messageIndex);
                    if (!isInitialSystem || includeSystemPrompt)
                    {
                        var instruction = isInitialSystem ? Transcript.GetSystemMessageText(system) : Transcript.RenderSystemMessageUpdate(system);
                        if (instruction.Length > 0)
                            messages.Add(new Dictionary<string, object>
                            {
                                ["role"] = model.Reasoning && model.Compat?.SupportsDeveloperRole != false ? "developer" : "system",
                                ["content"] = SanitizeText(instruction)
                            });
                    }
                    break;
                case UserMessage user:
                    var userMessage = ConvertUserMessage(user, model);
                    if (userMessage is null) continue;
                    messages.Add(userMessage);
                    break;

                case AssistantMessage assistant:
                    var converted = ConvertAssistantMessage(assistant, model, messageIndex, grammarToolInputProperties);
                    if (converted.Count == 0) continue;
                    messages.AddRange(converted);
                    toolNames.Clear();
                    foreach (var call in assistant.Content.OfType<ToolCallContent>()) toolNames[call.Id] = call.Name;
                    break;

                case ToolResultMessage toolResult:
                    var toolName = toolResult.ToolName ?? toolNames.GetValueOrDefault(toolResult.ToolCallId);
                    messages.Add(BuildFunctionCallOutput(toolResult.ToolCallId, BuildToolResultOutput(toolResult.Content, model),
                        custom: toolName is not null && grammarToolInputProperties.ContainsKey(toolName)));
                    break;
            }
            if (!isInitialSystem) messageIndex++;
        }

        return messages;
    }

    /// <summary>【AI】【Responses】在系统更新位置发送 additional_tools 或客户端工具搜索结果。</summary>
    /// <param name="messages">待追加的协议消息。</param>
    /// <param name="tools">本次新增工具。</param>
    /// <param name="model">协议能力配置。</param>
    /// <param name="messageIndex">排除开场系统消息后的来源序号，用于稳定调用标识。</param>
    private static void AppendSystemToolAdditions(List<object> messages, IReadOnlyList<Tool> tools, Model model, int messageIndex)
    {
        if (model.Compat?.SupportsAdditionalTools == true)
        {
            messages.Add(new Dictionary<string, object>
            {
                ["type"] = "additional_tools", ["role"] = "developer", ["tools"] = ConvertResponsesToolsForModel(tools, model)
            });
            return;
        }
        if (model.Compat?.SupportsToolSearch != true) return;
        var names = tools.Select(tool => tool.Name).ToArray();
        var callId = "pi_tool_load_" + Tau.Ai.ShortHash.Compute($"system:{messageIndex}:{string.Join(",", names)}");
        messages.Add(new Dictionary<string, object>
        {
            ["type"] = "tool_search_call", ["call_id"] = callId, ["execution"] = "client", ["status"] = "completed",
            ["arguments"] = new Dictionary<string, object> { ["query"] = string.Join(" ", names), ["limit"] = names.Length }
        });
        messages.Add(new Dictionary<string, object>
        {
            ["type"] = "tool_search_output", ["call_id"] = callId, ["execution"] = "client", ["status"] = "completed",
            ["tools"] = ConvertResponsesToolsForModel(tools, model, toolSearchResult: true)
        });
    }

    /// <summary>【AI】【Responses 工具采样】按协议默认值解析模型能力，统一顶层、内联与工具搜索声明。</summary>
    /// <param name="tools">待发送的工具。</param>
    /// <param name="model">实际请求模型；普通 Responses 缺省不支持 strict，Azure/Codex 缺省支持。</param>
    /// <param name="toolSearchResult">是否为工具搜索输出。</param>
    /// <returns>完成严格采样处理的工具声明。</returns>
    internal static List<object> ConvertResponsesToolsForModel(IReadOnlyList<Tool>? tools, Model model, bool toolSearchResult = false) =>
        ConvertResponsesTools(tools,
            strict: model.Api == "openai-codex-responses" ? null : false,
            toolSearchResult: toolSearchResult,
            supportsStrictMode: model.Compat?.SupportsStrictMode ?? (model.Api is "azure-openai-responses" or "openai-codex-responses"),
            supportsOpenAiGrammarTools: model.Compat?.SupportsOpenAiGrammarTools == true);

    /// <summary>【AI】【Responses 语法映射】在系统声明折叠前收集历史工具，保持已移除调用可重放。</summary>
    /// <param name="model">目标模型及语法能力。</param>
    /// <param name="context">原始请求上下文。</param>
    /// <returns>工具名称到字符串输入属性的映射。</returns>
    internal static IReadOnlyDictionary<string, string> CreateGrammarToolInputProperties(Model model, LlmContext context) =>
        ConstrainedSampling.CreateGrammarToolInputProperties(
            Transcript.GetDeclaredTools(Transcript.NormalizeContext(context).Messages), model.Compat?.SupportsOpenAiGrammarTools == true);

    /// <summary>【AI】【Responses 工具采样】解析工具级 strict 策略，为客户端工具搜索结果标记延迟加载。</summary>
    /// <param name="tools">待发送的工具集合。</param>
    /// <param name="strict">未配置工具级策略时的 strict 值；Codex 使用显式 null。</param>
    /// <param name="toolSearchResult">是否为工具搜索输出。</param>
    /// <param name="supportsStrictMode">是否支持 strict，不支持时省略该字段。</param>
    /// <param name="supportsOpenAiGrammarTools">是否将显式 grammar 工具转换为 custom 声明。</param>
    /// <returns>协议工具定义列表。</returns>
    public static List<object> ConvertResponsesTools(IReadOnlyList<Tool>? tools, bool? strict = false, bool toolSearchResult = false,
        bool supportsStrictMode = true, bool supportsOpenAiGrammarTools = false)
    {
        var result = new List<object>();
        if (tools is null)
        {
            return result;
        }

        foreach (var tool in tools)
        {
            if (ConstrainedSampling.ResolveGrammarConstrainedSampling(tool, supportsOpenAiGrammarTools) is { } grammar)
            {
                var custom = new Dictionary<string, object>
                {
                    ["type"] = "custom", ["name"] = tool.Name, ["description"] = tool.Description,
                    ["format"] = new Dictionary<string, object>
                    {
                        ["type"] = "grammar", ["syntax"] = grammar.Format, ["definition"] = grammar.Definition
                    }
                };
                if (toolSearchResult) custom["defer_loading"] = true;
                result.Add(custom);
                continue;
            }
            var resolvedStrict = ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, supportsStrictMode) ?? strict;
            var entry = new Dictionary<string, object>
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = ConstrainedSampling.GetJsonSchemaToolParameters(tool, resolvedStrict)
            };
            if (supportsStrictMode)
            {
                entry["strict"] = resolvedStrict.HasValue ? resolvedStrict.Value : null!;
            }
            if (toolSearchResult) entry["defer_loading"] = true;

            result.Add(entry);
        }

        return result;
    }

    /// <summary>【AI】【调用标识】规范跨模型调用 ID，使用主线摘要隔离外部 Responses 输出项。</summary>
    /// <param name="id">原调用 ID。</param><param name="targetModel">目标模型。</param><param name="source">来源助手。</param>
    /// <returns>与结果同步使用的规范 ID。</returns>
    public static string NormalizeToolCallId(string id, Model targetModel, AssistantMessage source)
    {
        var allowsAzure = targetModel.Api == "azure-openai-responses" && targetModel.Provider == "azure-openai-responses";
        if (!DefaultToolCallProviders.Contains(targetModel.Provider) && !allowsAzure)
        {
            return NormalizeIdPart(id);
        }

        if (!id.Contains('|', StringComparison.Ordinal))
        {
            return NormalizeIdPart(id);
        }

        var parts = id.Split('|');
        var callId = NormalizeIdPart(parts[0]);
        var itemId = parts[1];
        var isForeignToolCall =
            source.Provider != targetModel.Provider ||
            ModelApiNames.Normalize(source.Api) != ModelApiNames.Normalize(targetModel.Api);

        var normalizedItemId = isForeignToolCall
            ? $"fc_{Tau.Ai.ShortHash.Compute(itemId)}"
            : NormalizeIdPart(itemId);
        if (!normalizedItemId.StartsWith("fc_", StringComparison.Ordinal))
        {
            normalizedItemId = NormalizeIdPart($"fc_{normalizedItemId}");
        }

        return $"{callId}|{normalizedItemId}";
    }

    public static (string CallId, string? ItemId) SplitToolCallId(string id)
    {
        var parts = id.Split('|', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (id, null);
    }

    public static string ExtractAccountIdFromJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("Codex token is not a JWT.");
        }

        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("https://api.openai.com/auth", out var auth) ||
            !auth.TryGetProperty("chatgpt_account_id", out var accountId) ||
            accountId.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(accountId.GetString()))
        {
            throw new InvalidOperationException("Codex token does not contain chatgpt_account_id.");
        }

        return accountId.GetString()!;
    }

    /// <summary>【AI】【Codex 事件】统一终态事件名称，保留有效状态和原始失败原因。</summary>
    /// <param name="json">Codex 供应商事件 JSON。</param>
    /// <returns>转换后的共享 Responses 事件；供应商错误抛出包含具体原因的异常。</returns>
    public static string MapCodexEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            return json;
        }

        var type = typeElement.GetString();
        // 1. 【AI】【Codex 事件】兼容顶层和嵌套错误字段，避免丢失服务端诊断
        if (type == "error")
        {
            var nested = root.TryGetProperty("error", out var error) ? error : default;
            var message = GetString(root, "message") ?? GetString(nested, "message");
            var code = GetString(root, "code") ?? GetString(nested, "code");
            throw new InvalidOperationException($"Codex error: {NonEmpty(message) ?? NonEmpty(code) ?? json}");
        }
        if (type == "response.failed")
        {
            var failedResponse = root.TryGetProperty("response", out var failed) ? failed : default;
            var error = failedResponse.ValueKind == JsonValueKind.Object && failedResponse.TryGetProperty("error", out var detail) ? detail : default;
            throw new InvalidOperationException(NonEmpty(GetString(error, "message")) ?? "Codex response failed");
        }
        if (type is not ("response.done" or "response.completed" or "response.incomplete"))
        {
            return json;
        }

        var clone = JsonSerializer.Deserialize(json, OpenAiResponsesJsonContext.Default.DictionaryStringObject)
            ?? new Dictionary<string, object>();
        clone["type"] = "response.completed";
        if (clone.TryGetValue("response", out var responseValue) && responseValue is JsonElement { ValueKind: JsonValueKind.Object } responseElement)
        {
            var response = JsonSerializer.Deserialize(responseElement.GetRawText(), OpenAiResponsesJsonContext.Default.DictionaryStringObject)
                ?? new Dictionary<string, object>();
            // 2. 【AI】【Codex 事件】事件别名不能把 failed/incomplete 覆盖成 completed
            var status = GetString(responseElement, "status");
            if (status is "completed" or "incomplete" or "failed" or "cancelled" or "queued" or "in_progress") response["status"] = status;
            else response.Remove("status");
            clone["response"] = response;
        }

        return JsonSerializer.Serialize(clone, OpenAiResponsesJsonContext.Default.DictionaryStringObject);
    }

    /// <summary>【AI】【Responses 字段】保留非空字符串，匹配供应商错误字段的空值回退。</summary>
    /// <param name="value">可选字符串。</param>
    /// <returns>非空原值或 null。</returns>
    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>【AI】【Responses 流】处理 SSE 事件并保留语法输入和失败前的响应内容。</summary>
    /// <param name="responseStream">供应商响应流。</param>
    /// <param name="partial">初始助手消息。</param>
    /// <param name="stream">事件输出。</param>
    /// <param name="eventMapper">可选的协议事件转换。</param>
    /// <param name="requestedServiceTier">请求的服务等级。</param>
    /// <param name="resolveServiceTier">服务等级解析回调。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <param name="grammarToolInputProperties">语法工具输入属性映射。</param>
    /// <param name="mapCancellation">区分调用方取消和请求超时的异常转换。</param>
    /// <param name="onProviderStreamEvent">供应商事件重映射之前的 JSON 观察器。</param>
    /// <returns>流处理任务。</returns>
    public static async Task ProcessResponsesStreamAsync(
        Stream responseStream,
        AssistantMessage partial,
        AssistantMessageStream stream,
        Func<string, string>? eventMapper = null,
        string? requestedServiceTier = null,
        Func<string?, string?, string?>? resolveServiceTier = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? grammarToolInputProperties = null,
        Func<OperationCanceledException, Exception>? mapCancellation = null,
        Func<string, ValueTask>? onProviderStreamEvent = null)
    {
        var state = new ResponsesStreamState(
            partial,
            stream,
            requestedServiceTier: requestedServiceTier,
            resolveServiceTier: resolveServiceTier,
            grammarToolInputProperties: grammarToolInputProperties);
        try
        {
            await foreach (var sse in SseParser.ParseAsync(responseStream, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sse.Data == "[DONE]") break;
                if (onProviderStreamEvent is not null) await onProviderStreamEvent(sse.Data).ConfigureAwait(false);
                state.ProcessJson(eventMapper is null ? sse.Data : eventMapper(sse.Data));
                if (state.IsComplete) break;
            }
            if (!state.IsComplete)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("OpenAI Responses stream ended before a terminal response event");
            }
        }
        catch (OperationCanceledException exception)
        {
            // 1. 【AI】【Responses 中断】在丢失流状态前保留正文、工具参数和响应元数据
            var failure = mapCancellation?.Invoke(exception) ?? exception;
            var aborted = failure is OperationCanceledException && cancellationToken.IsCancellationRequested;
            state.Fail(aborted ? StreamOptionHelpers.AbortedErrorMessage : failure.Message,
                aborted ? StopReason.Aborted : StopReason.Error);
            throw;
        }
        catch (Exception exception)
        {
            state.Fail(exception.Message);
            throw;
        }
    }

    /// <summary>【AI】【Responses 流】处理 WebSocket JSON 事件，失败时保留部分内容并让连接层回收连接。</summary>
    /// <param name="jsonEvents">JSON 事件序列。</param>
    /// <param name="partial">初始助手消息。</param>
    /// <param name="stream">事件输出。</param>
    /// <param name="eventMapper">可选的协议事件转换。</param>
    /// <param name="beforeDone">成功完成前释放连接的回调。</param>
    /// <param name="requestedServiceTier">请求的服务等级。</param>
    /// <param name="resolveServiceTier">服务等级解析回调。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <param name="grammarToolInputProperties">语法工具输入属性映射。</param>
    /// <param name="onProviderStreamEvent">供应商事件重映射之前的 JSON 观察器。</param>
    /// <returns>收到非错误终态时为 true。</returns>
    public static async Task<bool> ProcessResponsesJsonEventsAsync(
        IAsyncEnumerable<string> jsonEvents,
        AssistantMessage partial,
        AssistantMessageStream stream,
        Func<string, string>? eventMapper = null,
        Action? beforeDone = null,
        string? requestedServiceTier = null,
        Func<string?, string?, string?>? resolveServiceTier = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? grammarToolInputProperties = null,
        Func<string, ValueTask>? onProviderStreamEvent = null)
    {
        var state = new ResponsesStreamState(
            partial,
            stream,
            beforeDone,
            requestedServiceTier,
            resolveServiceTier,
            grammarToolInputProperties);
        try
        {
            await foreach (var json in jsonEvents.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (onProviderStreamEvent is not null) await onProviderStreamEvent(json).ConfigureAwait(false);
                state.ProcessJson(eventMapper is null ? json : eventMapper(json));
                if (state.IsComplete) return !state.IsFailed;
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("OpenAI Responses stream ended before a terminal response event");
        }
        catch (OperationCanceledException exception)
        {
            var aborted = cancellationToken.IsCancellationRequested;
            state.Fail(aborted ? StreamOptionHelpers.AbortedErrorMessage : exception.Message,
                aborted ? StopReason.Aborted : StopReason.Error);
            throw;
        }
        catch (Exception exception)
        {
            state.Fail(exception.Message);
            throw;
        }
    }

    public static void AddBaseParameters(Dictionary<string, object> body, Model model, StreamOptions options)
    {
        if (options.MaxTokens.HasValue && model.Compat?.SupportsMaxOutputTokens != false)
        {
            body["max_output_tokens"] = options.MaxTokens.Value;
        }

        if (options.Temperature.HasValue)
        {
            body["temperature"] = options.Temperature.Value;
        }

        if (options.TopP.HasValue)
        {
            body["top_p"] = options.TopP.Value;
        }

        if (options.CacheRetention != CacheRetention.None && !string.IsNullOrWhiteSpace(options.SessionId))
        {
            body["prompt_cache_key"] = ClampOpenAiPromptCacheKey(options.SessionId)!;
        }

        if (options.CacheRetention == CacheRetention.Long &&
            model.Compat?.SupportsLongCacheRetention != false &&
            (string.IsNullOrWhiteSpace(model.BaseUrl) ||
             model.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase)))
        {
            body["prompt_cache_retention"] = "24h";
        }
    }

    public static string? ClampOpenAiPromptCacheKey(string? key)
    {
        if (key is null)
        {
            return null;
        }

        var runeCount = 0;
        var endIndex = 0;
        foreach (var rune in key.EnumerateRunes())
        {
            if (runeCount == OpenAiPromptCacheKeyMaxLength)
            {
                return key[..endIndex];
            }

            endIndex += rune.Utf16SequenceLength;
            runeCount++;
        }

        return key;
    }

    /// <summary>【AI】【Responses 思考】将通用思考等级限制到模型支持范围，关闭时交由请求层处理。</summary>
    /// <param name="level">调用方指定的等级。</param>
    /// <param name="model">模型能力与等级映射。</param>
    /// <returns>支持的逻辑等级；未设置或关闭时为 null。</returns>
    public static string? MapReasoningEffort(ThinkingLevel? level, Model model)
    {
        if (level is null)
        {
            return null;
        }

        var requested = level.Value switch
        {
            ThinkingLevel.Off => "off",
            ThinkingLevel.Minimal => "minimal",
            ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.High => "high",
            ThinkingLevel.ExtraHigh => "xhigh",
            ThinkingLevel.Max => "max",
            _ => "medium"
        };
        var clamped = Tau.Ai.Registry.ModelCatalog.ClampThinkingLevel(model, requested);
        return clamped == "off" ? null : clamped;
    }

    /// <summary>【AI】【Responses 思考】统一普通和 Azure 请求的思考开关、原生等级与可重放签名声明。</summary>
    /// <param name="body">待填充的请求体。</param>
    /// <param name="model">模型及供应商能力。</param>
    /// <param name="effort">逻辑思考等级。</param>
    /// <param name="summary">可选摘要模式。</param>
    /// <param name="preserveCopilotDefault">是否保留 Copilot 缺省思考行为。</param>
    public static void AddResponsesReasoning(Dictionary<string, object> body, Model model, string? effort, string? summary,
        bool preserveCopilotDefault = true)
    {
        if (!model.Reasoning) return;
        if (!string.IsNullOrWhiteSpace(effort) || !string.IsNullOrWhiteSpace(summary))
        {
            var mapped = string.IsNullOrWhiteSpace(effort) ? "medium" :
                model.ThinkingLevelMap?.TryGetValue(effort, out var value) == true ? value ?? effort : effort;
            body["reasoning"] = new Dictionary<string, object>
            {
                ["effort"] = mapped,
                ["summary"] = string.IsNullOrWhiteSpace(summary) ? "auto" : summary
            };
            body["include"] = new List<object> { "reasoning.encrypted_content" };
        }
        else if (!(preserveCopilotDefault && model.Provider.Equals("github-copilot", StringComparison.OrdinalIgnoreCase)) &&
            !(model.ThinkingLevelMap?.TryGetValue("off", out var off) == true && off is null))
        {
            body["reasoning"] = new Dictionary<string, object>
            {
                ["effort"] = model.ThinkingLevelMap?.GetValueOrDefault("off") ?? "none"
            };
        }
        if (model.Provider.Equals("xai", StringComparison.OrdinalIgnoreCase))
            body["include"] = new List<object> { "reasoning.encrypted_content" };
    }

    public static bool IsRetryableError(int statusCode, string errorText) =>
        statusCode is 429 or 500 or 502 or 503 or 504 ||
        System.Text.RegularExpressions.Regex.IsMatch(
            errorText,
            "rate.?limit|overloaded|service.?unavailable|upstream.?connect|connection.?refused",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

    private static object? ConvertUserMessage(UserMessage message, Model model)
    {
        var content = new List<object>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextContent text:
                    content.Add(new Dictionary<string, object>
                    {
                        ["type"] = "input_text",
                        ["text"] = SanitizeText(text.Text)
                    });
                    break;
                case ImageContent image when model.InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase):
                    content.Add(new Dictionary<string, object>
                    {
                        ["type"] = "input_image",
                        ["detail"] = "auto",
                        ["image_url"] = $"data:{image.MimeType};base64,{image.Data}"
                    });
                    break;
            }
        }

        return content.Count == 0
            ? null
            : new Dictionary<string, object>
            {
                ["role"] = "user",
                ["content"] = content
            };
    }

    /// <summary>【AI】【Responses 历史】转换已归一的助手内容，按消息和块序号生成稳定文本标识。</summary>
    /// <param name="message">已完成历史转换的助手。</param><param name="model">目标模型。</param>
    /// <param name="messageIndex">转换后消息序号。</param><param name="grammarToolInputProperties">语法工具输入映射。</param>
    /// <returns>正文、思考或工具调用协议项。</returns>
    private static List<object> ConvertAssistantMessage(AssistantMessage message, Model model, int messageIndex,
        IReadOnlyDictionary<string, string> grammarToolInputProperties)
    {
        var items = new List<object>();
        var sameProviderAndApi = message.Provider == model.Provider &&
            ModelApiNames.Normalize(message.Api) == ModelApiNames.Normalize(model.Api);
        var differentModel = sameProviderAndApi && message.Model != model.Id;
        var textBlockIndex = 0;
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case ThinkingContent thinking when !string.IsNullOrEmpty(thinking.ThinkingSignature):
                    // 1. 【AI】【思考签名】跨模型签名已被移除，同模型签名必须是可解析 JSON
                    using (var reasoning = JsonDocument.Parse(thinking.ThinkingSignature)) items.Add(reasoning.RootElement.Clone());
                    break;
                case TextContent text:
                    var signature = ParseTextSignature(text.TextSignature);
                    var fallbackId = textBlockIndex == 0 ? $"msg_pi_{messageIndex}" : $"msg_pi_{messageIndex}_{textBlockIndex}";
                    textBlockIndex++;
                    var messageId = string.IsNullOrEmpty(signature.Id) ? fallbackId : signature.Id;
                    if (messageId.Length > 64) messageId = $"msg_{Tau.Ai.ShortHash.Compute(messageId)}";
                    items.Add(BuildAssistantTextItem(messageId, SanitizeText(text.Text), signature.Phase));
                    break;
                case ToolCallContent toolCall:
                    // 2. 【AI】【工具回放】仅同提供方换模型或项类型不匹配时丢弃 item ID
                    var (callId, itemId) = SplitToolCallId(toolCall.Id);
                    var custom = grammarToolInputProperties.TryGetValue(toolCall.Name, out var inputProperty);
                    var functionCall = new Dictionary<string, object>
                    {
                        ["type"] = custom ? "custom_tool_call" : "function_call",
                        ["call_id"] = callId,
                        ["name"] = toolCall.Name
                    };
                    if (custom)
                    {
                        using var arguments = JsonDocument.Parse(toolCall.Arguments);
                        var values = arguments.RootElement.ValueKind == JsonValueKind.Object
                            ? arguments.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal)
                            : new Dictionary<string, object?>();
                        functionCall["input"] = SanitizeText(ConstrainedSampling.GetGrammarToolInput(toolCall.Name, values, inputProperty!));
                    }
                    else functionCall["arguments"] = toolCall.Arguments;
                    if (!differentModel && itemId?.StartsWith(custom ? "ctc_" : "fc_", StringComparison.Ordinal) == true)
                        functionCall["id"] = itemId;
                    if (sameProviderAndApi && message.Model == model.Id && toolCall.Namespace is not null)
                        functionCall["namespace"] = toolCall.Namespace;
                    items.Add(functionCall);
                    break;
            }
        }
        return items;
    }

    private static Dictionary<string, object> BuildAssistantTextItem(string id, string text, string? phase)
    {
        var item = new Dictionary<string, object>
        {
            ["type"] = "message",
            ["role"] = "assistant",
            ["status"] = "completed",
            ["id"] = id,
            ["content"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["type"] = "output_text",
                    ["text"] = text,
                    ["annotations"] = new List<object>()
                }
            }
        };
        if (!string.IsNullOrWhiteSpace(phase))
        {
            item["phase"] = phase!;
        }

        return item;
    }

    /// <summary>【AI】【Responses 工具结果】按当前声明选择函数或语法结果类型。</summary>
    /// <param name="toolCallId">调用标识。</param>
    /// <param name="output">文本或图片结果。</param>
    /// <param name="custom">是否为语法工具。</param>
    /// <returns>可与调用配对的结果项。</returns>
    private static Dictionary<string, object> BuildFunctionCallOutput(string toolCallId, object output, bool custom = false)
    {
        var (callId, _) = SplitToolCallId(toolCallId);
        var result = new Dictionary<string, object>
        {
            ["type"] = custom ? "custom_tool_call_output" : "function_call_output",
            ["call_id"] = callId,
            ["output"] = output
        };
        return result;
    }

    /// <summary>【AI】【工具输出】保留非空白或空白正文，为空结果提供占位，视觉模型返回图文数组。</summary>
    /// <param name="content">工具内容。</param><param name="model">目标模型。</param><returns>字符串或图文数组。</returns>
    private static object BuildToolResultOutput(IReadOnlyList<ContentBlock> content, Model model)
    {
        var text = string.Join("\n", content.OfType<TextContent>().Select(block => block.Text));
        text = SanitizeText(text);
        var images = content.OfType<ImageContent>().ToList();
        if (images.Count > 0 && model.InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase))
        {
            var output = new List<object>();
            if (text.Length > 0)
            {
                output.Add(new Dictionary<string, object>
                {
                    ["type"] = "input_text",
                    ["text"] = text
                });
            }

            foreach (var image in images)
            {
                output.Add(new Dictionary<string, object>
                {
                    ["type"] = "input_image",
                    ["detail"] = "auto",
                    ["image_url"] = $"data:{image.MimeType};base64,{image.Data}"
                });
            }

            return output;
        }

        return text.Length > 0 ? text : images.Count > 0 ? "(see attached image)" : "(no tool output)";
    }

    private static string SanitizeText(string text) =>
        UnicodeTextSanitizer.RemoveUnpairedSurrogates(text);

    /// <summary>【AI】【文本签名】读取版本化文本 ID，只接受已知 phase，其余签名按旧格式保留。</summary>
    /// <param name="signature">版本化 JSON 或旧字符串。</param><returns>文本 ID 和可选阶段。</returns>
    private static (string? Id, string? Phase) ParseTextSignature(string? signature)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return (null, null);
        }

        if (signature.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(signature);
                var root = doc.RootElement;
                if (root.TryGetProperty("v", out var version) && version.ValueKind == JsonValueKind.Number &&
                    version.TryGetDouble(out var number) && number == 1 &&
                    root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    var phase = root.TryGetProperty("phase", out var phaseElement) && phaseElement.ValueKind == JsonValueKind.String
                        ? phaseElement.GetString()
                        : null;
                    return (id.GetString(), phase is "commentary" or "final_answer" ? phase : null);
                }
            }
            catch (JsonException)
            {
                return (signature, null);
            }
        }

        return (signature, null);
    }

    private static string NormalizeIdPart(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        var normalized = builder.ToString();
        if (normalized.Length > 64)
        {
            normalized = normalized[..64];
        }

        return normalized.TrimEnd('_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static string EncodeTextSignature(string? id, string? phase)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var payload = new Dictionary<string, object>
        {
            ["v"] = 1,
            ["id"] = id!
        };
        if (!string.IsNullOrWhiteSpace(phase))
        {
            payload["phase"] = phase!;
        }

        return JsonSerializer.Serialize(payload, OpenAiResponsesJsonContext.Default.DictionaryStringObject);
    }



    private sealed class ResponsesStreamState
    {
        private readonly AssistantMessageStream _stream;
        private readonly Dictionary<string, int> _itemIndexes = new(StringComparer.Ordinal);
        private readonly Dictionary<int, int> _outputIndexes = new();
        private readonly HashSet<int> _completedIndexes = new();
        private readonly Dictionary<int, string> _toolCallArguments = new();
        private readonly Dictionary<int, CustomToolInput> _customToolInputs = new();
        private readonly Dictionary<string, int> _reasoningIndexes = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, string> _grammarToolInputProperties;
        private readonly Action? _beforeDone;
        private readonly string? _requestedServiceTier;
        private readonly Func<string?, string?, string?>? _resolveServiceTier;
        private AssistantMessage _partial;
        public bool IsComplete { get; private set; }
        public bool IsFailed { get; private set; }

        /// <summary>【AI】【Responses 流状态】建立输出槽位和语法输入映射。</summary>
        /// <param name="partial">初始消息。</param>
        /// <param name="stream">事件输出。</param>
        /// <param name="beforeDone">成功完成前回调。</param>
        /// <param name="requestedServiceTier">请求服务等级。</param>
        /// <param name="resolveServiceTier">服务等级解析回调。</param>
        /// <param name="grammarToolInputProperties">语法工具属性映射。</param>
        public ResponsesStreamState(
            AssistantMessage partial,
            AssistantMessageStream stream,
            Action? beforeDone = null,
            string? requestedServiceTier = null,
            Func<string?, string?, string?>? resolveServiceTier = null,
            IReadOnlyDictionary<string, string>? grammarToolInputProperties = null)
        {
            _partial = partial;
            _stream = stream;
            _beforeDone = beforeDone;
            _requestedServiceTier = requestedServiceTier;
            _resolveServiceTier = resolveServiceTier;
            _grammarToolInputProperties = grammarToolInputProperties ?? new Dictionary<string, string>();
        }

        /// <summary>【AI】【Responses 流状态】按事件种类更新独立输出槽位。</summary>
        /// <param name="json">供应商 JSON 事件。</param>
        public void ProcessJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (GetString(root, "type"))
            {
                case "response.created":
                    ApplyResponse(root);
                    break;
                case "response.output_item.added":
                    AddOutputItem(root);
                    break;
                case "response.content_part.added":
                    AddContentPart(root);
                    break;
                case "response.reasoning_summary_part.added":
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                case "response.reasoning.delta":
                    AppendThinkingDelta(root);
                    break;
                case "response.reasoning_summary_part.done":
                    AppendThinkingDelta(root, "\n\n");
                    break;
                case "response.output_text.delta":
                case "response.refusal.delta":
                    AppendTextDelta(root);
                    break;
                case "response.function_call_arguments.delta":
                    AppendToolCallDelta(root);
                    break;
                case "response.function_call_arguments.done":
                    CompleteToolCallArguments(root);
                    break;
                case "response.custom_tool_call_input.delta":
                    AppendCustomToolInput(root, close: false);
                    break;
                case "response.custom_tool_call_input.done":
                    AppendCustomToolInput(root, close: true);
                    break;
                case "response.output_item.done":
                    CompleteOutputItem(root);
                    break;
                case "response.completed":
                case "response.incomplete":
                    CompleteResponse(root);
                    break;
                case "response.failed":
                    var response = root.TryGetProperty("response", out var failed) ? failed : default;
                    _partial = _partial with { RawStopReason = GetString(response, "status") };
                    Fail(ExtractError(root));
                    break;
                case "error":
                    Fail(ExtractError(root));
                    break;
            }
        }

        /// <summary>【AI】【Responses 元数据】读取响应标识、实际模型和有效用量，空字段不覆盖已有统计。</summary>
        /// <param name="root">包含 response 的生命周期事件。</param>
        private void ApplyResponse(JsonElement root)
        {
            if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (GetString(response, "id") is { } id)
            {
                _partial = _partial with { ResponseId = id };
            }

            if (GetString(response, "model") is { Length: > 0 } responseModel)
            {
                _partial = _partial with { ResponseModel = responseModel };
            }

            // 1. 【AI】【Responses 元数据】开始事件通常 usage=null，后续有效统计才替换快照
            if (response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                _partial = _partial with { Usage = ExtractUsage(usage, ResolveServiceTier(response)) };
            }
        }

        /// <summary>【AI】【Responses 输出槽位】创建正文、思考或工具内容，重复开始事件不重置已有缓冲。</summary>
        /// <param name="root">包含 item 和可选 output_index 的事件。</param>
        private void AddOutputItem(JsonElement root)
        {
            if (!root.TryGetProperty("item", out var item))
            {
                return;
            }

            var itemId = GetString(item, "id") ??
                         GetString(root, "item_id") ??
                         Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            // 1. 【AI】【Responses 输出槽位】同一输出项只初始化一次，独立保存每个调用的缓冲
            var outputIndex = GetInt(root, "output_index");
            if (_itemIndexes.ContainsKey(itemId) || (outputIndex is { } existingOutput && _outputIndexes.ContainsKey(existingOutput))) return;
            switch (GetString(item, "type"))
            {
                case "reasoning":
                    AddBlock(itemId, new ThinkingContent(""));
                    _stream.Push(new ThinkingStartEvent(_itemIndexes[itemId], _partial));
                    break;
                case "message":
                    AddBlock(itemId, new TextContent(""));
                    _stream.Push(new TextStartEvent(_itemIndexes[itemId], _partial));
                    break;
                case "function_call":
                    var callId = GetString(item, "call_id") ?? GetString(item, "id") ?? itemId;
                    var name = GetString(item, "name") ?? string.Empty;
                    var arguments = GetString(item, "arguments") ?? string.Empty;
                    AddBlock(itemId, new ToolCallContent(
                        $"{callId}|{itemId}",
                        name,
                        StreamingJsonParser.ParseStreamingJsonObjectRawText(arguments)) { Namespace = GetString(item, "namespace") });
                    var index = _itemIndexes[itemId];
                    _toolCallArguments[index] = arguments;
                    _stream.Push(new ToolCallStartEvent(index, _partial));
                    break;
                case "custom_tool_call":
                    var customName = GetString(item, "name") ?? string.Empty;
                    var property = _grammarToolInputProperties.GetValueOrDefault(customName) ?? "input";
                    var input = GetString(item, "input") ?? string.Empty;
                    var customCallId = GetString(item, "call_id") ?? itemId;
                    AddBlock(itemId, new ToolCallContent($"{customCallId}|{itemId}", customName, SerializeCustomArguments(property, input))
                    { Namespace = GetString(item, "namespace") });
                    var customIndex = _itemIndexes[itemId];
                    _customToolInputs[customIndex] = new(property, new()) { Input = input };
                    _stream.Push(new ToolCallStartEvent(customIndex, _partial, $"{customCallId}|{itemId}", customName));
                    break;
            }
            if (outputIndex is { } output && _itemIndexes.TryGetValue(itemId, out var contentIndex)) _outputIndexes[output] = contentIndex;
        }

        /// <summary>【AI】【Responses 内容】兼容以 content_part 开始的正文流，并绑定明确的输出槽位。</summary>
        /// <param name="root">内容部分开始事件。</param>
        private void AddContentPart(JsonElement root)
        {
            var itemId = GetString(root, "item_id");
            var outputIndex = GetInt(root, "output_index");
            if (itemId is null || _itemIndexes.ContainsKey(itemId) ||
                outputIndex is { } output && _outputIndexes.ContainsKey(output))
            {
                return;
            }

            if (root.TryGetProperty("part", out var part) &&
                GetString(part, "type") is "output_text" or "refusal")
            {
                AddBlock(itemId, new TextContent(""));
                if (outputIndex is { } slot) _outputIndexes[slot] = _itemIndexes[itemId];
                _stream.Push(new TextStartEvent(_itemIndexes[itemId], _partial));
            }
        }

        /// <summary>【AI】【Responses 内容】仅向匹配且未完成的正文追加文本或拒答增量。</summary>
        /// <param name="root">正文或拒答增量事件。</param>
        private void AppendTextDelta(JsonElement root)
        {
            var delta = GetString(root, "delta") ?? string.Empty;
            var index = FindIndex(root, static block => block is TextContent);
            if (index is null)
            {
                // 1. 【AI】【Responses 内容】明确但无效的输出槽位不能创建幽灵块或覆盖其他类型
                if (GetInt(root, "output_index") is not null ||
                    GetString(root, "item_id") is { } existingId && _itemIndexes.ContainsKey(existingId)) return;
                var itemId = GetString(root, "item_id") ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                AddBlock(itemId, new TextContent(""));
                index = _itemIndexes[itemId];
                _stream.Push(new TextStartEvent(index.Value, _partial));
            }
            if (_completedIndexes.Contains(index.Value)) return;

            UpdateBlock(index.Value, block => block is TextContent text
                ? text with { Text = text.Text + delta }
                : block);
            _stream.Push(new TextDeltaEvent(index.Value, delta, _partial));
        }

        /// <summary>【AI】【Responses 推理】累计摘要、原始推理文本和摘要部分之间的段落分隔。</summary>
        /// <param name="root">推理事件。</param>
        /// <param name="overrideDelta">摘要部分完成时使用的固定分隔符。</param>
        private void AppendThinkingDelta(JsonElement root, string? overrideDelta = null)
        {
            var delta = overrideDelta ?? GetString(root, "delta") ?? GetString(root, "text") ?? string.Empty;
            var index = FindIndex(root, static block => block is ThinkingContent);
            if (index is null)
            {
                if (GetInt(root, "output_index") is not null ||
                    GetString(root, "item_id") is { } existingId && _itemIndexes.ContainsKey(existingId)) return;
                var itemId = GetString(root, "item_id") ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                AddBlock(itemId, new ThinkingContent(""));
                index = _itemIndexes[itemId];
                _stream.Push(new ThinkingStartEvent(index.Value, _partial));
            }
            if (_completedIndexes.Contains(index.Value)) return;

            UpdateBlock(index.Value, block => block is ThinkingContent thinking
                ? thinking with { Thinking = thinking.Thinking + delta }
                : block);
            _stream.Push(new ThinkingDeltaEvent(index.Value, delta, _partial));
        }

        /// <summary>追加普通函数参数，禁止写入 grammar 或已结束的调用。</summary>
        /// <param name="root">函数参数增量事件。</param>
        private void AppendToolCallDelta(JsonElement root)
        {
            var delta = GetString(root, "delta") ?? string.Empty;
            var index = FindIndex(root, static block => block is ToolCallContent, allowLast: false);
            if (index is null || !_toolCallArguments.ContainsKey(index.Value) || _completedIndexes.Contains(index.Value))
            {
                return;
            }

            var accumulatedArguments = AccumulateToolCallArguments(index.Value, delta);
            UpdateBlock(index.Value, block => block is ToolCallContent toolCall
                ? toolCall with { Arguments = StreamingJsonParser.ParseStreamingJsonObjectRawText(accumulatedArguments) }
                : block);
            _stream.Push(new ToolCallDeltaEvent(index.Value, delta, _partial));
        }

        /// <summary>补齐函数参数终值；仅对单调增加的后缀发出遗漏增量。</summary>
        /// <param name="root">函数参数完成事件。</param>
        private void CompleteToolCallArguments(JsonElement root)
        {
            var index = FindIndex(root, static block => block is ToolCallContent, allowLast: false);
            var arguments = GetString(root, "arguments");
            if (index is not null && arguments is not null && _toolCallArguments.TryGetValue(index.Value, out var previous) && !_completedIndexes.Contains(index.Value))
            {
                _toolCallArguments[index.Value] = arguments;
                UpdateBlock(index.Value, block => block is ToolCallContent toolCall
                    ? toolCall with { Arguments = StreamingJsonParser.ParseStreamingJsonObjectRawText(arguments) }
                    : block);
                if (arguments.StartsWith(previous, StringComparison.Ordinal) && arguments.Length > previous.Length)
                    _stream.Push(new ToolCallDeltaEvent(index.Value, arguments[previous.Length..], _partial));
            }
        }

        /// <summary>【AI】【语法工具响应】把原始字符串输入转换为统一 JSON 参数增量。</summary>
        /// <param name="root">增量或输入完成事件。</param>
        /// <param name="close">是否闭合 JSON 字符串。</param>
        private void AppendCustomToolInput(JsonElement root, bool close)
        {
            var index = FindIndex(root, static block => block is ToolCallContent, allowLast: false);
            if (index is null || !_customToolInputs.TryGetValue(index.Value, out var custom) || _completedIndexes.Contains(index.Value)) return;
            var input = close ? GetString(root, "input") ?? custom.Input : custom.Input + (GetString(root, "delta") ?? "");
            UpdateCustomToolInput(index.Value, custom, input, close);
        }

        /// <summary>更新一个语法调用的参数快照并发出可拼接的 JSON 增量。</summary>
        /// <param name="index">内容索引。</param>
        /// <param name="custom">独立调用缓冲。</param>
        /// <param name="input">完整原始输入。</param>
        /// <param name="close">是否闭合输入。</param>
        private void UpdateCustomToolInput(int index, CustomToolInput custom, string input, bool close)
        {
            var delta = ConstrainedSampling.AppendGrammarToolInputJsonDelta(custom.Buffer, custom.Property, input, close);
            custom.Input = input;
            UpdateBlock(index, block => ((ToolCallContent)block) with { Arguments = SerializeCustomArguments(custom.Property, input) });
            if (delta is not null) _stream.Push(new ToolCallDeltaEvent(index, delta, _partial));
        }

        /// <summary>序列化原始语法输入为工具可执行的单字段 JSON 对象。</summary>
        /// <param name="property">输入属性名。</param>
        /// <param name="input">原始输入。</param>
        /// <returns>完整工具参数 JSON。</returns>
        private static string SerializeCustomArguments(string property, string input) => JsonSerializer.Serialize(
            new Dictionary<string, string> { [property] = input }, OpenAiResponsesJsonContext.Default.DictionaryStringString);

        /// <summary>保存一个语法调用的属性、增量状态和初始输入。</summary>
        /// <param name="Property">输入属性名。</param>
        /// <param name="Buffer">JSON 增量缓冲。</param>
        private sealed record CustomToolInput(string Property, ConstrainedSampling.GrammarToolInputBuffer Buffer)
        {
            public string Input { get; set; } = "";
        }

        /// <summary>【AI】【Responses 输出完成】补建缺少开始事件的槽位，应用内容终值并发出一次结束事件。</summary>
        /// <param name="root">输出项完成事件。</param>
        private void CompleteOutputItem(JsonElement root)
        {
            var item = root.TryGetProperty("item", out var itemElement) ? itemElement : default;
            var itemId = item.ValueKind == JsonValueKind.Object
                ? GetString(item, "id") ?? GetString(root, "item_id")
                : GetString(root, "item_id");
            var outputIndex = GetInt(root, "output_index");
            int? index = outputIndex is { } output
                ? (_outputIndexes.TryGetValue(output, out var slot) ? slot : null)
                : (itemId is not null && _itemIndexes.TryGetValue(itemId, out var mapped) ? mapped : null);
            if (index is null && item.ValueKind == JsonValueKind.Object)
            {
                AddOutputItem(root);
                if (outputIndex is { } createdOutput && _outputIndexes.TryGetValue(createdOutput, out var createdSlot)) index = createdSlot;
                else if (outputIndex is null && itemId is not null && _itemIndexes.TryGetValue(itemId, out var created)) index = created;
            }
            if (index is null || index.Value < 0 || index.Value >= _partial.Content.Count)
            {
                return;
            }
            if (_completedIndexes.Contains(index.Value)) return;

            // 1. 【AI】【Responses 输出完成】损坏或类型不匹配的完成事件不能关闭其他内容或释放待执行调用
            var block = _partial.Content[index.Value];
            var expectedType = block switch
            {
                TextContent => "message",
                ThinkingContent => "reasoning",
                _ => _customToolInputs.ContainsKey(index.Value) ? "custom_tool_call" : "function_call"
            };
            if (item.ValueKind != JsonValueKind.Object || GetString(item, "type") != expectedType) return;
            ApplyDoneItem(index.Value, item, block);

            switch (_partial.Content[index.Value])
            {
                case TextContent text:
                    _stream.Push(new TextEndEvent(index.Value, _partial, text.Text, text.TextSignature));
                    break;
                case ThinkingContent thinking:
                    _stream.Push(new ThinkingEndEvent(index.Value, _partial, thinking.Thinking, thinking.ThinkingSignature));
                    break;
                case ToolCallContent toolCall:
                    _toolCallArguments.Remove(index.Value);
                    _customToolInputs.Remove(index.Value);
                    _stream.Push(new ToolCallEndEvent(index.Value, _partial, toolCall));
                    break;
            }
            _completedIndexes.Add(index.Value);
        }

        /// <summary>【AI】【Responses 终态】未收到工具项完成事件时拒绝交付可执行调用。</summary>
        /// <param name="root">响应终态事件。</param>
        private void CompleteResponse(JsonElement root)
        {
            var response = root.TryGetProperty("response", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
            ApplyResponse(root);
            if (response.ValueKind == JsonValueKind.Object)
            {
                BackfillReasoningSignatures(response);
                if (_partial.Usage is { } usage)
                    _partial = _partial with { Usage = usage with { ServiceTier = ResolveServiceTier(response) ?? usage.ServiceTier } };
                if (response.TryGetProperty("end_turn", out var endTurn) && endTurn.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    _partial = _partial with { EndTurn = endTurn.GetBoolean() };
            }

            // 1. 【AI】【Responses 终态】保留具体失败原因，只有输出额度耗尽属于长度截断
            var status = GetString(response, "status");
            var details = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("incomplete_details", out var incomplete) ? incomplete : default;
            var incompleteReason = GetString(details, "reason");
            _partial = _partial with { RawStopReason = NonEmpty(incompleteReason) is null ? status : $"{status ?? "undefined"}.{incompleteReason}" };
            var (stopReason, errorMessage) = MapStopReason(status, incompleteReason);
            if (stopReason == StopReason.EndTurn && _partial.Content.OfType<ToolCallContent>().Any()) stopReason = StopReason.ToolUse;
            _partial = _partial with { StopReason = stopReason, ErrorMessage = errorMessage, Timestamp = DateTimeOffset.UtcNow };
            if (stopReason == StopReason.Error)
            {
                Fail(errorMessage ?? "An unknown error occurred");
                return;
            }

            // 2. 【AI】【Responses 工具完成】只有全部调用收到输出项完成事件才发布成功终态
            if (_partial.StopReason == StopReason.ToolUse && (_toolCallArguments.Count > 0 || _customToolInputs.Count > 0))
            {
                var unfinishedIndex = _toolCallArguments.Keys.Concat(_customToolInputs.Keys).First();
                var unfinished = (ToolCallContent)_partial.Content[unfinishedIndex];
                throw new InvalidOperationException($"OpenAI Responses stream completed with an unfinished tool call: {unfinished.Name} ({unfinished.Id})");
            }
            _beforeDone?.Invoke();
            _stream.Push(new DoneEvent(_partial));
            IsComplete = true;
        }

        /// <summary>【AI】【Responses 输出完成】应用正文、推理和工具终值，语法输入保持单调且闭合。</summary>
        /// <param name="index">内容索引。</param>
        /// <param name="item">最终输出项。</param>
        /// <param name="block">当前内容快照。</param>
        private void ApplyDoneItem(int index, JsonElement item, ContentBlock block)
        {
            switch (block)
            {
                case TextContent text:
                    var textFromItem = ExtractOutputText(item);
                    if (textFromItem is not null)
                    {
                        UpdateBlock(index, _ => text with
                        {
                            Text = textFromItem,
                            TextSignature = EncodeTextSignature(GetString(item, "id"), GetString(item, "phase"))
                        });
                    }
                    else if (GetString(item, "id") is { } id)
                    {
                        UpdateBlock(index, _ => text with { TextSignature = EncodeTextSignature(id, GetString(item, "phase")) });
                    }
                    break;
                case ThinkingContent thinking:
                    var summary = ExtractReasoningText(item, "summary");
                    var content = ExtractReasoningText(item, "content");
                    UpdateBlock(index, _ => thinking with
                    {
                        Thinking = NonEmpty(summary) ?? NonEmpty(content) ?? thinking.Thinking,
                        ThinkingSignature = item.GetRawText()
                    });
                    if (GetString(item, "id") is { } reasoningId) _reasoningIndexes[reasoningId] = index;
                    break;
                case ToolCallContent toolCall:
                    if (_customToolInputs.TryGetValue(index, out var custom))
                    {
                        UpdateCustomToolInput(index, custom, GetString(item, "input") ?? custom.Input, close: true);
                        if (GetString(item, "namespace") is { } customNamespace)
                            UpdateBlock(index, current => ((ToolCallContent)current) with { Namespace = customNamespace });
                        break;
                    }
                    var callId = GetString(item, "call_id");
                    var itemId = GetString(item, "id");
                    var name = GetString(item, "name");
                    var args = GetString(item, "arguments");
                    var finalArgs = args ?? (_toolCallArguments.TryGetValue(index, out var accumulated)
                        ? accumulated
                        : toolCall.Arguments);
                    UpdateBlock(index, _ => toolCall with
                    {
                        Id = callId is not null && itemId is not null ? $"{callId}|{itemId}" : toolCall.Id,
                        Name = name ?? toolCall.Name,
                        Arguments = StreamingJsonParser.ParseStreamingJsonObjectRawText(finalArgs),
                        Namespace = GetString(item, "namespace") ?? toolCall.Namespace
                    });
                    break;
            }
        }

        /// <summary>【AI】【Responses 推理】优先使用供应商终值，将摘要或原始内容按段落连接。</summary>
        /// <param name="item">最终推理输出项。</param>
        /// <param name="field">summary 或 content 字段名。</param>
        /// <returns>推理文本；字段缺失时为空串。</returns>
        private static string ExtractReasoningText(JsonElement item, string field) =>
            item.TryGetProperty(field, out var parts) && parts.ValueKind == JsonValueKind.Array
                ? string.Join("\n\n", parts.EnumerateArray().Select(part => GetString(part, "text") ?? ""))
                : "";

        /// <summary>【AI】【Responses 推理重放】补入仅在终态返回的加密内容，保留原推理项的其他元数据。</summary>
        /// <param name="response">完成或不完整响应对象。</param>
        private void BackfillReasoningSignatures(JsonElement response)
        {
            if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return;
            foreach (var item in output.EnumerateArray())
            {
                if (GetString(item, "type") != "reasoning" || NonEmpty(GetString(item, "encrypted_content")) is not { } encrypted ||
                    GetString(item, "id") is not { } id || !_reasoningIndexes.TryGetValue(id, out var index) ||
                    _partial.Content[index] is not ThinkingContent { ThinkingSignature: { } signature } thinking) continue;
                using var stored = JsonDocument.Parse(signature);
                if (NonEmpty(GetString(stored.RootElement, "encrypted_content")) is not null) continue;
                var merged = JsonSerializer.Deserialize(signature, OpenAiResponsesJsonContext.Default.DictionaryStringObject)!;
                merged["encrypted_content"] = encrypted;
                UpdateBlock(index, _ => thinking with { ThinkingSignature = JsonSerializer.Serialize(merged, OpenAiResponsesJsonContext.Default.DictionaryStringObject) });
            }
        }

        private string AccumulateToolCallArguments(int index, string delta)
        {
            var current = _toolCallArguments.TryGetValue(index, out var existing)
                ? existing
                : string.Empty;
            var accumulated = current + delta;
            _toolCallArguments[index] = accumulated;
            return accumulated;
        }

        /// <summary>优先按输出槽位匹配，兼容明确 item_id，工具事件禁止猜测最近的调用。</summary>
        /// <param name="root">事件。</param>
        /// <param name="predicate">要求的内容类型。</param>
        /// <param name="allowLast">无标识时是否允许旧文本流的末块回退。</param>
        /// <returns>匹配内容索引。</returns>
        private int? FindIndex(JsonElement root, Func<ContentBlock, bool> predicate, bool allowLast = true)
        {
            if (GetInt(root, "output_index") is { } output)
                return _outputIndexes.TryGetValue(output, out var slot) && predicate(_partial.Content[slot]) ? slot : null;
            var itemId = GetString(root, "item_id");
            if (itemId is not null && _itemIndexes.TryGetValue(itemId, out var mapped))
            {
                return predicate(_partial.Content[mapped]) ? mapped : null;
            }
            if (itemId is not null || !allowLast) return null;

            for (var i = _partial.Content.Count - 1; i >= 0; i--)
            {
                if (predicate(_partial.Content[i]))
                {
                    return i;
                }
            }

            return null;
        }

        /// <summary>【AI】【Responses 流错误】保留已生成内容并结束事件流，避免调用方等待没有终态的响应。</summary>
        /// <param name="error">失败原因。</param>
        /// <param name="stopReason">错误或调用方取消的终态。</param>
        public void Fail(string error, StopReason stopReason = StopReason.Error)
        {
            if (IsComplete) return;
            IsFailed = true;
            IsComplete = true;
            _partial = _partial with { StopReason = stopReason, ErrorMessage = error, Timestamp = DateTimeOffset.UtcNow };
            _stream.Push(new ErrorEvent(error, _partial, _partial));
        }

        private void AddBlock(string itemId, ContentBlock block)
        {
            if (_itemIndexes.ContainsKey(itemId))
            {
                return;
            }

            var content = _partial.Content.ToList();
            content.Add(block);
            _partial = _partial with { Content = content };
            _itemIndexes[itemId] = content.Count - 1;
        }

        private void UpdateBlock(int index, Func<ContentBlock, ContentBlock> update)
        {
            var content = _partial.Content.ToList();
            content[index] = update(content[index]);
            _partial = _partial with { Content = content };
        }

        /// <summary>【AI】【Responses 正文】从最终消息提取文本和拒答，空内容数组也是有效终值。</summary>
        /// <param name="item">消息输出项。</param>
        /// <returns>最终正文；没有内容数组时为 null。</returns>
        private static string? ExtractOutputText(JsonElement item)
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var builder = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                var text = GetString(part, "type") switch
                {
                    "output_text" => GetString(part, "text"),
                    "refusal" => GetString(part, "refusal") ?? GetString(part, "text"),
                    _ => null
                };
                if (text is not null) builder.Append(text);
            }

            return builder.ToString();
        }

        private string? ResolveServiceTier(JsonElement response)
        {
            var responseServiceTier = GetString(response, "service_tier");
            return _resolveServiceTier is null
                ? responseServiceTier ?? _requestedServiceTier
                : _resolveServiceTier(responseServiceTier, _requestedServiceTier);
        }

        /// <summary>【AI】【Responses 用量】拆分输入、缓存读写及推理用量，避免缓存被重复计入普通输入。</summary>
        /// <param name="usage">供应商用量对象。</param>
        /// <param name="serviceTier">解析后的服务等级。</param>
        /// <returns>供上下文估算和费用计算使用的统一用量。</returns>
        private static Usage ExtractUsage(JsonElement usage, string? serviceTier)
        {
            var input = GetInt(usage, "input_tokens") ?? GetInt(usage, "prompt_tokens") ?? 0;
            var output = GetInt(usage, "output_tokens") ?? GetInt(usage, "completion_tokens") ?? 0;
            int? cacheRead = null;
            int? cacheWrite = null;
            if (usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty("input_tokens_details", out var inputDetails) &&
                inputDetails.ValueKind == JsonValueKind.Object)
            {
                cacheRead = GetInt(inputDetails, "cached_tokens");
                cacheWrite = GetInt(inputDetails, "cache_write_tokens");
            }

            cacheRead ??= GetInt(usage, "prompt_cache_hit_tokens") ?? GetInt(usage, "cache_read_input_tokens");
            cacheWrite ??= GetInt(usage, "prompt_cache_miss_tokens") ?? GetInt(usage, "cache_creation_input_tokens");
            // 1. 【AI】【Responses 用量】input_tokens 已包含缓存，使用长整数避免异常统计减法溢出
            var uncachedInput = (int)Math.Clamp((long)input - cacheRead.GetValueOrDefault() - cacheWrite.GetValueOrDefault(), 0, int.MaxValue);
            var outputDetails = usage.TryGetProperty("output_tokens_details", out var details) ? details : default;
            return new Usage(uncachedInput, output, cacheRead, cacheWrite, serviceTier)
            {
                ReasoningTokens = GetInt(outputDetails, "reasoning_tokens"),
                TotalTokens = GetInt(usage, "total_tokens")
            };
        }

        /// <summary>【AI】【Responses 终态】区分输出额度截断、内容过滤和其他失败，不把未知状态当成功。</summary>
        /// <param name="status">服务端响应状态。</param>
        /// <param name="incompleteReason">供应商报告的不完整原因。</param>
        /// <returns>停止原因和可选诊断文本。</returns>
        private static (StopReason StopReason, string? ErrorMessage) MapStopReason(string? status, string? incompleteReason) =>
            status switch
            {
                null or "" or "completed" or "in_progress" or "queued" => (StopReason.EndTurn, null),
                "incomplete" when incompleteReason == "max_output_tokens" => (StopReason.MaxTokens, null),
                "incomplete" => (StopReason.Error, NonEmpty(incompleteReason) is null
                    ? "Response incomplete without a provider reason" : $"Response incomplete: {incompleteReason}"),
                "failed" or "cancelled" => (StopReason.Error, null),
                _ => throw new InvalidOperationException($"Unhandled stop reason: {status}")
            };

        /// <summary>【AI】【Responses 错误】保留错误码和正文，并为缺省失败信息生成可诊断回退。</summary>
        /// <param name="root">error 或 response.failed 事件。</param>
        /// <returns>供应商失败说明。</returns>
        private static string ExtractError(JsonElement root)
        {
            if (GetString(root, "type") == "response.failed")
            {
                var response = root.TryGetProperty("response", out var value) ? value : default;
                if (response.ValueKind == JsonValueKind.Object)
                {
                    if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                        return $"{NonEmpty(GetString(error, "code")) ?? "unknown"}: {NonEmpty(GetString(error, "message")) ?? "no message"}";
                    if (response.TryGetProperty("incomplete_details", out var details) && NonEmpty(GetString(details, "reason")) is { } reason)
                        return $"incomplete: {reason}";
                }
                return "Unknown error (no error details in response)";
            }
            var nested = root.TryGetProperty("error", out var nestedError) ? nestedError : default;
            var code = GetString(root, "code") ?? GetString(nested, "code") ?? "undefined";
            var message = GetString(root, "message") ?? GetString(nested, "message") ??
                (nested.ValueKind == JsonValueKind.String ? nested.GetString() : null) ?? "undefined";
            return $"Error Code {code}: {message}";
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(float?))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(decimal?))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(TimeSpan?))]
internal partial class OpenAiResponsesJsonContext : JsonSerializerContext;
