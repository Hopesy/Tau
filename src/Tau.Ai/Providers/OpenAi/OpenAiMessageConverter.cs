using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.OpenAi;

/// <summary>
/// Converts between Tau message types and OpenAI API format.
/// </summary>
internal static class OpenAiMessageConverter
{
    /// <summary>【AI】【语法工具】在声明折叠前建立历史工具到输入属性的映射。</summary>
    /// <param name="model">携带语法能力的模型。</param>
    /// <param name="context">包含旧式字段或消息式声明的上下文。</param>
    /// <returns>最后一次同名声明确定的输入属性映射。</returns>
    public static IReadOnlyDictionary<string, string> CreateGrammarToolInputProperties(Model model, LlmContext context) =>
        ConstrainedSampling.CreateGrammarToolInputProperties(
            Transcript.GetDeclaredTools(Transcript.NormalizeContext(context).Messages),
            model.Compat?.SupportsOpenAiGrammarTools == true);

    public static bool HasToolHistory(IReadOnlyList<ChatMessage> messages) =>
        messages.Any(static message =>
            message is ToolResultMessage ||
            message is AssistantMessage assistant &&
            assistant.Content.Any(static block => block is ToolCallContent));

    public static JsonElement ConvertMessages(
        IReadOnlyList<ChatMessage> messages,
        bool supportsImages = true,
        bool requiresThinkingAsText = false,
        bool requiresToolResultName = false,
        bool requiresAssistantAfterToolResult = false,
        bool requiresReasoningContentOnAssistantMessages = false,
        bool modelReasoning = false) =>
        JsonSerializer.SerializeToElement(
            ConvertMessageObjects(
                messages,
                supportsImages,
                requiresThinkingAsText,
                requiresToolResultName,
                requiresAssistantAfterToolResult,
                requiresReasoningContentOnAssistantMessages,
                modelReasoning),
            OpenAiJsonContext.Default.ListObject);

    /// <summary>【AI】【Chat Completions】按原顺序转换消息，并渲染系统增量和工具新增声明。</summary>
    /// <param name="messages">已按模型能力处理的会话。</param>
    /// <param name="supportsImages">是否传输图片。</param>
    /// <param name="requiresThinkingAsText">是否把思考转成文本。</param>
    /// <param name="requiresToolResultName">是否保留工具结果名称。</param>
    /// <param name="requiresAssistantAfterToolResult">是否补充工具结果之后的助手消息。</param>
    /// <param name="requiresReasoningContentOnAssistantMessages">是否要求 reasoning_content 字段。</param>
    /// <param name="modelReasoning">是否为推理模型。</param>
    /// <param name="instructionRole">指令使用 system 或 developer 角色。</param>
    /// <param name="anchorsToolAdditions">是否在后续系统消息处传输新增工具。</param>
    /// <param name="supportsStrictMode">新增工具是否支持严格 schema。</param>
    /// <param name="supportsOpenAiGrammarTools">是否发送自定义语法工具。</param>
    /// <param name="grammarToolInputProperties">历史语法工具的输入属性映射。</param>
    /// <param name="provider">目标提供方，用于 opencode-go 的原始思考字段兼容。</param>
    /// <returns>待序列化的协议消息列表。</returns>
    public static List<object> ConvertMessageObjects(
        IReadOnlyList<ChatMessage> messages,
        bool supportsImages = true,
        bool requiresThinkingAsText = false,
        bool requiresToolResultName = false,
        bool requiresAssistantAfterToolResult = false,
        bool requiresReasoningContentOnAssistantMessages = false,
        bool modelReasoning = false,
        string instructionRole = "system",
        bool anchorsToolAdditions = false,
        bool supportsStrictMode = false,
        bool supportsOpenAiGrammarTools = false,
        IReadOnlyDictionary<string, string>? grammarToolInputProperties = null,
        string? provider = null)
    {
        var array = new List<object>();
        var lastRole = string.Empty;
        for (var sourceIndex = 0; sourceIndex < messages.Count; sourceIndex++)
        {
            var msg = messages[sourceIndex];
            var isInitial = sourceIndex == 0;
            if (requiresAssistantAfterToolResult &&
                string.Equals(lastRole, "toolResult", StringComparison.Ordinal) &&
                msg is UserMessage)
            {
                array.Add(new Dictionary<string, object>
                {
                    ["role"] = "assistant",
                    ["content"] = "I have processed the tool results."
                });
            }

            switch (msg)
            {
                case SystemMessage system:
                    if (!isInitial && anchorsToolAdditions && system.ToolsAdded is { Count: > 0 })
                        array.Add(new Dictionary<string, object>
                        {
                            ["role"] = "system",
                            ["tools"] = ConvertToolObjects(system.ToolsAdded, supportsStrictMode, supportsOpenAiGrammarTools)
                        });
                    var instruction = isInitial ? Transcript.GetSystemMessageText(system) : Transcript.RenderSystemMessageUpdate(system);
                    if (instruction.Length > 0)
                        array.Add(new Dictionary<string, object> { ["role"] = instructionRole, ["content"] = SanitizeText(instruction) });
                    break;
                case UserMessage user:
                    if (ConvertUserMessage(user, supportsImages) is { } convertedUser) array.Add(convertedUser);
                    break;
                case AssistantMessage assistant:
                    if (ConvertAssistantMessage(
                        assistant,
                        requiresThinkingAsText,
                        requiresReasoningContentOnAssistantMessages,
                        modelReasoning,
                        grammarToolInputProperties,
                        requiresAssistantAfterToolResult,
                        provider) is { } convertedAssistant) array.Add(convertedAssistant);
                    break;
                case ToolResultMessage:
                    // 2. 【AI】【工具图片】连续工具结果先完整配对，图片集中放在随后的一条用户消息中
                    var imageParts = new List<object>();
                    while (sourceIndex < messages.Count && messages[sourceIndex] is ToolResultMessage toolResult)
                    {
                        array.Add(ConvertToolResultMessage(toolResult, requiresToolResultName));
                        if (supportsImages)
                            foreach (var image in toolResult.Content.OfType<ImageContent>())
                                imageParts.Add(new Dictionary<string, object>
                                {
                                    ["type"] = "image_url",
                                    ["image_url"] = new Dictionary<string, string> { ["url"] = $"data:{image.MimeType};base64,{image.Data}" }
                                });
                        sourceIndex++;
                    }
                    sourceIndex--;
                    if (imageParts.Count > 0)
                    {
                        if (requiresAssistantAfterToolResult)
                            array.Add(new Dictionary<string, object> { ["role"] = "assistant", ["content"] = "I have processed the tool results." });
                        imageParts.Insert(0, new Dictionary<string, object> { ["type"] = "text", ["text"] = "Attached image(s) from tool result:" });
                        array.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = imageParts });
                        lastRole = "user";
                    }
                    else lastRole = "toolResult";
                    continue;
            }

            lastRole = msg.Role;
        }
        return array;
    }

    /// <summary>【AI】【用户正文】空内容集合不生成消息，图文数组移除空文本块。</summary>
    /// <param name="msg">用户消息。</param><param name="supportsImages">是否传输图片。</param><returns>请求消息或空值。</returns>
    private static object? ConvertUserMessage(UserMessage msg, bool supportsImages)
    {
        if (msg.Content.Count == 0) return null;
        var hasNonText = msg.Content.Any(c => c is not TextContent);
        if (!hasNonText)
        {
            var text = SanitizeText(string.Join("", msg.Content.OfType<TextContent>().Select(t => t.Text)));
            return new Dictionary<string, object> { ["role"] = "user", ["content"] = text };
        }

        var parts = new List<object>();
        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case TextContent text when text.Text.Length > 0:
                    parts.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = SanitizeText(text.Text) });
                    break;
                case ImageContent image when supportsImages:
                    parts.Add(new Dictionary<string, object>
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new Dictionary<string, string>
                        {
                            ["url"] = $"data:{image.MimeType};base64,{image.Data}"
                        }
                    });
                    break;
            }
        }

        return parts.Count == 0 ? null : new Dictionary<string, object>
        {
            ["role"] = "user",
            ["content"] = parts
        };
    }

    /// <summary>【AI】【语法工具】转换助手内容，并按历史声明恢复 custom 或 function 调用。</summary>
    /// <param name="msg">历史助手消息。</param>
    /// <param name="requiresThinkingAsText">是否将思考写入普通文本。</param>
    /// <param name="requiresReasoningContentOnAssistantMessages">是否需要推理占位字段。</param>
    /// <param name="modelReasoning">模型是否支持推理。</param>
    /// <param name="grammarToolInputProperties">语法工具的输入属性映射。</param>
    /// <param name="requiresAssistantAfterToolResult">是否要求空字符串正文。</param><param name="provider">目标提供方。</param>
    /// <returns>Chat Completions 助手消息；没有正文及工具调用时为空。</returns>
    private static object? ConvertAssistantMessage(
        AssistantMessage msg,
        bool requiresThinkingAsText,
        bool requiresReasoningContentOnAssistantMessages,
        bool modelReasoning,
        IReadOnlyDictionary<string, string>? grammarToolInputProperties,
        bool requiresAssistantAfterToolResult,
        string? provider)
    {
        var result = new Dictionary<string, object> { ["role"] = "assistant", ["content"] = requiresAssistantAfterToolResult ? "" : null! };

        var toolCalls = msg.Content.OfType<ToolCallContent>().ToList();
        var textParts = msg.Content.OfType<TextContent>().Where(text => !string.IsNullOrWhiteSpace(text.Text)).ToList();
        var allThinkingParts = msg.Content.OfType<ThinkingContent>().ToList();
        var thinkingParts = allThinkingParts.Where(t => !string.IsNullOrWhiteSpace(t.Thinking)).ToList();
        var reasoningDetails = allThinkingParts.Select(thinking => OpenAiReasoningDetails.ParseArray(thinking.ThinkingSignature))
            .FirstOrDefault(details => details.HasValue);
        if (reasoningDetails is null)
        {
            var legacy = toolCalls.Select(call => OpenAiReasoningDetails.ParseLegacyEncrypted(call.ThoughtSignature))
                .Where(detail => detail.HasValue).Select(detail => detail!.Value).ToList();
            if (legacy.Count > 0) reasoningDetails = JsonSerializer.SerializeToElement(legacy, OpenAiJsonContext.Default.ListJsonElement);
        }
        // 1. 【AI】【思考正文】要求转为正文时保留文本块边界，其余格式的普通正文使用字符串
        if (requiresThinkingAsText && thinkingParts.Count > 0)
        {
            var content = new List<object>
            {
                new Dictionary<string, object> { ["type"] = "text", ["text"] = SanitizeText(string.Join("\n\n", thinkingParts.Select(part => part.Thinking))) }
            };
            content.AddRange(textParts.Select(part => (object)new Dictionary<string, object> { ["type"] = "text", ["text"] = SanitizeText(part.Text) }));
            result["content"] = content;
        }
        else
        {
            if (textParts.Count > 0) result["content"] = SanitizeText(string.Concat(textParts.Select(part => part.Text)));
            if (thinkingParts.Count > 0 && reasoningDetails is null)
            {
                var signature = thinkingParts[0].ThinkingSignature;
                if (provider == "opencode-go" && signature == "reasoning") signature = "reasoning_content";
                if (signature is "reasoning" or "reasoning_content" or "reasoning_text")
                    result[signature] = string.Join("\n", thinkingParts.Select(part => part.Thinking));
            }
        }

        if (toolCalls.Count > 0)
        {
            var serializedToolCalls = new List<object>();
            foreach (var tc in toolCalls)
            {
                // 1. 【AI】【语法工具】历史 JSON 参数需恢复成供应商约定的原始字符串
                if (grammarToolInputProperties?.TryGetValue(tc.Name, out var property) == true)
                {
                    using var arguments = JsonDocument.Parse(tc.Arguments);
                    var values = arguments.RootElement.ValueKind == JsonValueKind.Object
                        ? arguments.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => (object?)item.Value)
                        : new Dictionary<string, object?>();
                    serializedToolCalls.Add(new Dictionary<string, object>
                    {
                        ["id"] = tc.Id,
                        ["type"] = "custom",
                        ["custom"] = new Dictionary<string, string>
                        {
                            ["name"] = tc.Name,
                            ["input"] = SanitizeText(ConstrainedSampling.GetGrammarToolInput(tc.Name, values, property))
                        }
                    });
                    continue;
                }

                serializedToolCalls.Add(new Dictionary<string, object>
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, string>
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = tc.Arguments
                    }
                });
            }

            result["tool_calls"] = serializedToolCalls;
        }

        // 2. 【AI】【签名重放】结构化明细优先于原始思考字段，缺省占位不得覆盖真实 reasoning_content
        if (reasoningDetails is { } details)
        {
            result["reasoning_details"] = details;
        }

        if (requiresReasoningContentOnAssistantMessages && modelReasoning && !result.ContainsKey("reasoning_content"))
        {
            result["reasoning_content"] = string.Empty;
        }

        return toolCalls.Count > 0 || result["content"] is string { Length: > 0 } or List<object> { Count: > 0 } ? result : null;
    }

    /// <summary>【AI】【工具正文】多个文本块用换行连接，只有图片或空结果时发送对应占位文字。</summary>
    /// <param name="msg">工具结果。</param><param name="requiresToolResultName">是否发送工具名。</param>
    /// <returns>不包含图片的工具结果消息。</returns>
    private static object ConvertToolResultMessage(ToolResultMessage msg, bool requiresToolResultName)
    {
        var text = string.Join("\n", msg.Content.OfType<TextContent>().Select(t => t.Text));
        if (text.Length == 0) text = msg.Content.Any(block => block is ImageContent) ? "(see attached image)" : "(no tool output)";
        text = SanitizeText(text);
        var result = new Dictionary<string, object>
        {
            ["role"] = "tool",
            ["tool_call_id"] = msg.ToolCallId,
            ["content"] = text
        };

        if (requiresToolResultName && !string.IsNullOrWhiteSpace(msg.ToolName))
        {
            result["name"] = msg.ToolName;
        }

        return result;
    }

    private static string SanitizeText(string text) =>
        Tau.Ai.UnicodeTextSanitizer.RemoveUnpairedSurrogates(text);

    /// <summary>【AI】【工具采样】转换工具声明并返回独立 JSON 节点。</summary>
    /// <param name="tools">工具声明。</param>
    /// <param name="supportsStrictMode">模型是否支持严格工具参数。</param>
    /// <param name="supportsOpenAiGrammarTools">是否支持自定义语法工具。</param>
    /// <returns>Chat Completions 工具数组。</returns>
    public static JsonElement ConvertTools(IReadOnlyList<Tool> tools, bool supportsStrictMode = false, bool supportsOpenAiGrammarTools = false) =>
        JsonSerializer.SerializeToElement(ConvertToolObjects(tools, supportsStrictMode, supportsOpenAiGrammarTools), OpenAiJsonContext.Default.ListObject);

    /// <summary>【AI】【工具采样】按工具显式 prefer/require 配置生成顶层或中途工具声明。</summary>
    /// <param name="tools">实际发送的工具声明。</param>
    /// <param name="supportsStrictMode">模型是否显式支持 strict。</param>
    /// <param name="supportsOpenAiGrammarTools">模型是否显式支持 grammar。</param>
    /// <returns>工具报文；require 不能满足时抛出包含工具名的异常。</returns>
    public static List<object> ConvertToolObjects(IReadOnlyList<Tool> tools, bool supportsStrictMode = false, bool supportsOpenAiGrammarTools = false)
    {
        var array = new List<object>();
        foreach (var tool in tools)
        {
            // 1. 【AI】【语法工具】Chat Completions 的 grammar 位于 custom.format.grammar
            if (ConstrainedSampling.ResolveGrammarConstrainedSampling(tool, supportsOpenAiGrammarTools) is { } grammar)
            {
                array.Add(new Dictionary<string, object>
                {
                    ["type"] = "custom",
                    ["custom"] = new Dictionary<string, object>
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["format"] = new Dictionary<string, object>
                        {
                            ["type"] = "grammar",
                            ["grammar"] = new Dictionary<string, string>
                            {
                                ["syntax"] = grammar.Format,
                                ["definition"] = grammar.Definition
                            }
                        }
                    }
                });
                continue;
            }

            var strict = ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, supportsStrictMode);
            var function = new Dictionary<string, object>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict)
            };
            if (supportsStrictMode)
            {
                function["strict"] = strict ?? false;
            }

            array.Add(new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = function
            });
        }

        return array;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(List<JsonElement>))]
internal partial class OpenAiJsonContext : JsonSerializerContext;
