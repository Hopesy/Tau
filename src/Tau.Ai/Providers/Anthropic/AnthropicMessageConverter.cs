using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.Anthropic;

/// <summary>
/// Converts between Tau message types and Anthropic Messages API format.
/// Anthropic uses content-block arrays natively, and tool_result is carried in user messages.
/// </summary>
internal static class AnthropicMessageConverter
{
    private static readonly HashSet<string> StrictUnsupportedKeywords = new(StringComparer.Ordinal)
    {
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "maxItems", "uniqueItems",
        "minContains", "maxContains", "minProperties", "maxProperties"
    };
    private static readonly HashSet<string> StrictStringFormats = new(StringComparer.Ordinal)
    {
        "date-time", "time", "date", "duration", "email", "hostname", "uri", "ipv4", "ipv6", "uuid"
    };

    /// <summary>【AI】【Anthropic 会话】转换消息，把系统更新延后到助手消息之前，避免打断工具调用配对。</summary>
    /// <param name="messages">不含开场系统声明的会话消息。</param>
    /// <param name="cacheControl">最后一条用户或系统消息使用的缓存标记。</param>
    /// <param name="allowEmptySignature">是否允许空的思考签名。</param>
    /// <param name="convertToolDefinitions">内联工具定义转换器；null 表示工具变化已在顶层合并。</param>
    /// <param name="isOAuth">是否转换 Claude Code 工具名称。</param>
    /// <param name="managedProvider">启用会话 effort 的供应商；null 表示不插入等级消息。</param>
    /// <param name="activeEffort">当前请求的原生思考等级。</param>
    /// <returns>保持用户和工具结果顺序的协议消息。</returns>
    public static List<object> ConvertMessages(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyDictionary<string, object>? cacheControl = null,
        bool allowEmptySignature = false,
        Func<IReadOnlyList<Tool>, List<object>>? convertToolDefinitions = null,
        bool isOAuth = false,
        string? managedProvider = null,
        string activeEffort = "high")
    {
        var result = new List<object>();
        var pendingSystemMessages = new List<object>();
        var assistantLevels = new Dictionary<int, string>();
        var i = 0;

        while (i < messages.Count)
        {
            var msg = messages[i];

            switch (msg)
            {
                case SystemMessage system:
                    // 1. 【AI】【Anthropic 会话】暂存中途指令，工具结果可以先紧跟已有调用
                    var blocks = ConvertSystemUpdate(system, convertToolDefinitions, isOAuth);
                    if (blocks.Count > 0)
                        pendingSystemMessages.Add(new Dictionary<string, object> { ["role"] = "system", ["content"] = blocks });
                    i++;
                    break;
                case UserMessage user:
                    if (ConvertUserMessage(user) is { } convertedUser) result.Add(convertedUser);
                    i++;
                    break;

                case AssistantMessage assistant:
                    // 2. 【AI】【Anthropic 会话】在下一条助手消息前按原顺序发出积累的系统更新
                    result.AddRange(pendingSystemMessages);
                    pendingSystemMessages.Clear();
                    var converted = ConvertAssistantMessage(assistant, allowEmptySignature, isOAuth);
                    if (converted is not null)
                    {
                        if (managedProvider is not null && assistant.Api == "anthropic-messages" && assistant.Provider == managedProvider && AnthropicProtocol.IsEffort(assistant.ProviderThinkingLevel))
                            assistantLevels[result.Count] = assistant.ProviderThinkingLevel!;
                        result.Add(converted);
                    }
                    i++;
                    break;

                case ToolResultMessage:
                    // 3. 【AI】【Anthropic 会话】把连续工具结果合并为一个 user 消息
                    var toolResults = new List<object>();
                    while (i < messages.Count && messages[i] is ToolResultMessage tr)
                    {
                        toolResults.Add(ConvertToolResultBlock(tr));
                        i++;
                    }
                    result.Add(new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = toolResults
                    });
                    break;

                default:
                    i++;
                    break;
            }
        }

        // 4. 【AI】【Anthropic 缓存】剩余更新放在末尾，缓存断点只放在最终消息的最终可缓存块
        result.AddRange(pendingSystemMessages);
        ApplyCacheControlToLastMessage(result, cacheControl);
        // 5. 【AI】【Anthropic 思考】缓存标记先固定在会话内容上，再插入历史等级及本轮等级消息
        if (managedProvider is not null)
        {
            var managed = new List<object>();
            for (var index = 0; index < result.Count; index++)
            {
                if (assistantLevels.TryGetValue(index, out var effort)) managed.Add(CreateEffortMessage(effort));
                managed.Add(result[index]);
            }
            managed.Add(CreateEffortMessage(activeEffort));
            return managed;
        }
        return result;
    }

    /// <summary>【AI】【Anthropic 思考】构造不带缓存标记的供应商等级更新。</summary>
    /// <param name="effort">需要恢复或启用的等级。</param>
    /// <returns>空内容的系统配置消息。</returns>
    private static Dictionary<string, object> CreateEffortMessage(string effort) => new()
    {
        ["role"] = "system", ["content"] = new List<object>(),
        ["output_config"] = new Dictionary<string, object> { ["effort"] = effort }
    };

    /// <summary>【AI】【Anthropic 工具】将段落补丁、工具移除和内联定义转为系统内容块。</summary>
    /// <param name="message">待发送的系统更新。</param>
    /// <param name="convertToolDefinitions">工具定义转换器；null 时只传输指令。</param>
    /// <param name="isOAuth">是否转换移除工具的名称。</param>
    /// <returns>依次为文本、移除和新增的内容块；空更新返回空列表。</returns>
    private static List<object> ConvertSystemUpdate(SystemMessage message, Func<IReadOnlyList<Tool>, List<object>>? convertToolDefinitions, bool isOAuth)
    {
        var blocks = new List<object>();
        var text = Transcript.RenderSystemMessageUpdate(message);
        if (text.Length > 0) blocks.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = SanitizeText(text) });
        if (convertToolDefinitions is null) return blocks;

        // 1. 【AI】【Anthropic 工具】同一更新中的同名新增直接替换旧定义，不再发送移除块
        var added = message.ToolsAdded ?? [];
        var redefined = added.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var tool in message.ToolsRemoved ?? [])
        {
            if (redefined.Contains(tool.Name)) continue;
            blocks.Add(new Dictionary<string, object>
            {
                ["type"] = "tool_removal",
                ["tool"] = new Dictionary<string, object> { ["type"] = "tool_reference", ["name"] = isOAuth ? AnthropicProtocol.ToClaudeCodeName(tool.Name) : tool.Name }
            });
        }

        // 2. 【AI】【Anthropic 工具】内联携带完整定义，缓存标记由外层内容块处理
        foreach (var definition in convertToolDefinitions(added))
            blocks.Add(new Dictionary<string, object>
            {
                ["type"] = "tool_addition",
                ["tool"] = new Dictionary<string, object> { ["type"] = "tool_definition", ["definition"] = definition }
            });
        return blocks;
    }

    /// <summary>【AI】【调用标识】跨模型工具 ID 限制为 64 个 ASCII 字母、数字、下划线或连字符。</summary>
    /// <param name="id">来源标识。</param><returns>兼容 Anthropic 的调用标识。</returns>
    internal static string NormalizeToolCallId(string id) =>
        new(id.Take(64).Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());

    /// <summary>【AI】【用户内容】过滤纯空白文本，省略无有效内容的用户消息。</summary>
    /// <param name="msg">用户历史。</param><returns>用户协议消息，无有效内容时为 null。</returns>
    private static object? ConvertUserMessage(UserMessage msg)
    {
        var parts = ConvertContentParts(msg.Content, skipBlankText: true);
        return parts.Count == 0 ? null : new Dictionary<string, object> { ["role"] = "user", ["content"] = parts };
    }

    /// <summary>【AI】【图文内容】按原顺序保留全部文本与图片，用户消息可过滤空白文本。</summary>
    /// <param name="content">内容块。</param><param name="skipBlankText">是否过滤空白文本。</param><returns>协议内容数组。</returns>
    private static List<object> ConvertContentParts(IReadOnlyList<ContentBlock> content, bool skipBlankText)
    {
        var parts = new List<object>();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent text when !skipBlankText || !string.IsNullOrWhiteSpace(text.Text):
                    parts.Add(new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = SanitizeText(text.Text)
                    });
                    break;
                case ImageContent image:
                    parts.Add(new Dictionary<string, object>
                    {
                        ["type"] = "image",
                        ["source"] = new Dictionary<string, string>
                        {
                            ["type"] = "base64",
                            ["media_type"] = image.MimeType,
                            ["data"] = image.Data
                        }
                    });
                    break;
            }
        }

        return parts;
    }

    /// <summary>【AI】【Anthropic 历史】转换助手内容，保留脱敏签名，跳过空块并按 OAuth 规则转换调用名称。</summary>
    /// <param name="msg">历史助手消息。</param>
    /// <param name="allowEmptySignature">是否保留空思考签名。</param>
    /// <param name="isOAuth">是否转换工具名称。</param>
    /// <returns>可发送的助手消息；无有效内容时为空。</returns>
    private static object? ConvertAssistantMessage(AssistantMessage msg, bool allowEmptySignature, bool isOAuth)
    {
        var parts = new List<object>();
        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                    parts.Add(new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = SanitizeText(text.Text)
                    });
                    break;
                case ThinkingContent { Redacted: true } redacted:
                    parts.Add(new Dictionary<string, object> { ["type"] = "redacted_thinking", ["data"] = redacted.ThinkingSignature! });
                    break;
                case ThinkingContent thinking:
                    if (string.IsNullOrWhiteSpace(thinking.Thinking) && string.IsNullOrWhiteSpace(thinking.ThinkingSignature)) break;
                    if (string.IsNullOrWhiteSpace(thinking.ThinkingSignature))
                    {
                        parts.Add(allowEmptySignature
                            ? new Dictionary<string, object>
                            {
                                ["type"] = "thinking",
                                ["thinking"] = SanitizeText(thinking.Thinking),
                                ["signature"] = string.Empty
                            }
                            : new Dictionary<string, object>
                            {
                                ["type"] = "text",
                                ["text"] = SanitizeText(thinking.Thinking)
                            });
                    }
                    else
                    {
                        parts.Add(new Dictionary<string, object>
                        {
                            ["type"] = "thinking",
                            ["thinking"] = SanitizeText(thinking.Thinking),
                            ["signature"] = thinking.ThinkingSignature
                        });
                    }
                    break;
                case ToolCallContent toolCall:
                    parts.Add(new Dictionary<string, object>
                    {
                        ["type"] = "tool_use",
                        ["id"] = toolCall.Id,
                        ["name"] = isOAuth ? AnthropicProtocol.ToClaudeCodeName(toolCall.Name) : toolCall.Name,
                        ["input"] = ParseArgumentsToInput(toolCall.Arguments)
                    });
                    break;
            }
        }

        if (parts.Count == 0) return null;
        return new Dictionary<string, object>
        {
            ["role"] = "assistant",
            ["content"] = parts
        };
    }

    /// <summary>【AI】【工具结果】纯文本按换行连接，含图片时保留完整图文顺序，并显式携带错误标记。</summary>
    /// <param name="msg">工具执行结果。</param><returns>tool_result 内容块。</returns>
    private static object ConvertToolResultBlock(ToolResultMessage msg)
    {
        object content = msg.Content.Any(block => block is ImageContent)
            ? ConvertContentParts(msg.Content, skipBlankText: false)
            : SanitizeText(string.Join("\n", msg.Content.OfType<TextContent>().Select(block => block.Text)));
        var result = new Dictionary<string, object>
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = msg.ToolCallId,
            ["content"] = content,
            ["is_error"] = msg.IsError
        };
        return result;
    }

    private static object ParseArgumentsToInput(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return new Dictionary<string, object>();

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return JsonSerializer.Deserialize(doc.RootElement.GetRawText(),
                AnthropicJsonContext.Default.JsonElement);
        }
        catch
        {
            return new Dictionary<string, object>();
        }
    }

    /// <summary>【AI】【Anthropic 工具】统一转换顶层与内联定义，按工具约束策略决定 strict，并只缓存最后一个定义。</summary>
    /// <param name="tools">本次声明的工具。</param>
    /// <param name="supportsEagerToolInputStreaming">是否发送工具输入即时流标记。</param>
    /// <param name="cacheControl">最后一个定义的缓存标记；内联定义不传入。</param>
    /// <param name="supportsStrictTools">模型是否显式支持严格工具采样。</param>
    /// <param name="isOAuth">是否使用 Claude Code 工具名称。</param>
    /// <returns>与 Anthropic 协议一致的工具定义。</returns>
    public static List<object> ConvertTools(
        IReadOnlyList<Tool> tools,
        bool supportsEagerToolInputStreaming = true,
        IReadOnlyDictionary<string, object>? cacheControl = null,
        bool supportsStrictTools = false,
        bool isOAuth = false)
    {
        var converted = tools.Select(t =>
        {
            // 1. 【AI】【Anthropic 工具】仅显式 JSON Schema 配置启用 strict，require 失败在 HTTP 之前抛出
            var strict = ConstrainedSampling.ResolveJsonSchemaStrictSampling(t, supportsStrictTools, IsStrictUnsupportedKeyword);
            var parameters = ConstrainedSampling.GetJsonSchemaToolParameters(t, strict);
            var inputSchema = strict == true
                ? parameters.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value, StringComparer.Ordinal)
                : new Dictionary<string, object>();

            // 2. 【AI】【Anthropic 工具】非严格模式保持上游三字段结构，严格模式保留根级约束和描述
            inputSchema["type"] = "object";
            inputSchema["properties"] = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("properties", out var properties) && properties.ValueKind != JsonValueKind.Null
                ? properties : new Dictionary<string, object>();
            inputSchema["required"] = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("required", out var required) && required.ValueKind != JsonValueKind.Null
                ? required : new List<object>();
            var tool = new Dictionary<string, object>
            {
                ["name"] = isOAuth ? AnthropicProtocol.ToClaudeCodeName(t.Name) : t.Name,
                ["description"] = t.Description,
                ["input_schema"] = inputSchema
            };
            if (strict == true) tool["strict"] = true;
            if (supportsEagerToolInputStreaming)
            {
                tool["eager_input_streaming"] = true;
            }

            return tool;
        }).ToList();

        if (cacheControl is not null && converted.Count > 0)
        {
            converted[^1]["cache_control"] = cacheControl;
        }

        return converted.Select(static tool => (object)tool).ToList();
    }

    /// <summary>【AI】【Anthropic 工具】检查 Anthropic 严格采样的额外关键字限制。</summary>
    /// <param name="key">当前 Schema 节点的关键字。</param>
    /// <param name="value">关键字值。</param>
    /// <returns>严格模式不接受该键值时为 true。</returns>
    private static bool IsStrictUnsupportedKeyword(string key, JsonElement value)
    {
        if (StrictUnsupportedKeywords.Contains(key)) return true;
        if (key == "minItems") return value.ValueKind != JsonValueKind.Number || (value.GetDouble() is not (0 or 1));
        if (key == "format") return value.ValueKind != JsonValueKind.String || !StrictStringFormats.Contains(value.GetString()!);
        return false;
    }

    /// <summary>【AI】【Anthropic 缓存】仅在最后一条消息为 user 或 system 时标记其末块，不回溯助手之前的消息。</summary>
    /// <param name="messages">转换后的协议消息。</param>
    /// <param name="cacheControl">缓存设置；null 时不添加标记。</param>
    private static void ApplyCacheControlToLastMessage(
        List<object> messages,
        IReadOnlyDictionary<string, object>? cacheControl)
    {
        if (cacheControl is null || messages.Count == 0 ||
            messages[^1] is not Dictionary<string, object> message ||
            !message.TryGetValue("role", out var role) || Convert.ToString(role) is not ("user" or "system") ||
            !message.TryGetValue("content", out var content) || content is not List<object> { Count: > 0 } blocks ||
            blocks[^1] is not Dictionary<string, object> block || !block.TryGetValue("type", out var type)) return;

        if (Convert.ToString(type) is "text" or "image" or "tool_result" or "tool_addition" or "tool_removal")
            block["cache_control"] = cacheControl;
    }

    private static string SanitizeText(string text) =>
        UnicodeTextSanitizer.RemoveUnpairedSurrogates(text);
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
internal partial class AnthropicJsonContext : JsonSerializerContext;
