using System.Text.Json;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.Bedrock;

internal static class BedrockMessageConverter
{
    private const string InterleavedThinkingBeta = "interleaved-thinking-2025-05-14";

    public static Dictionary<string, object> BuildRequestBody(Model model, LlmContext context, BedrockOptions options)
    {
        context = MessageTransformer.DowngradeUnsupportedImages(context, model);
        var body = new Dictionary<string, object>
        {
            ["messages"] = ConvertMessages(context.Messages, model, options.CacheRetention)
        };

        var system = BuildSystemPrompt(context.SystemPrompt, model, options.CacheRetention);
        if (system.Count > 0)
        {
            body["system"] = system;
        }

        var inferenceConfig = BuildInferenceConfig(options);
        if (inferenceConfig.Count > 0)
        {
            body["inferenceConfig"] = inferenceConfig;
        }

        var toolConfig = ConvertToolConfig(context.Tools, options);
        if (toolConfig is not null)
        {
            body["toolConfig"] = toolConfig;
        }

        var additionalFields = BuildAdditionalModelRequestFields(model, options);
        if (additionalFields is not null)
        {
            body["additionalModelRequestFields"] = additionalFields;
        }

        if (options.RequestMetadata is { Count: > 0 })
        {
            body["requestMetadata"] = options.RequestMetadata;
        }

        return body;
    }

    private static Dictionary<string, object> BuildInferenceConfig(BedrockOptions options)
    {
        var config = new Dictionary<string, object>();
        if (options.MaxTokens.HasValue)
        {
            config["maxTokens"] = options.MaxTokens.Value;
        }

        if (options.Temperature.HasValue)
        {
            config["temperature"] = options.Temperature.Value;
        }

        if (options.TopP.HasValue)
        {
            config["topP"] = options.TopP.Value;
        }

        return config;
    }

    private static List<object> BuildSystemPrompt(string? systemPrompt, Model model, CacheRetention cacheRetention)
    {
        var result = new List<object>();
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            return result;
        }

        result.Add(new Dictionary<string, object> { ["text"] = SanitizeText(systemPrompt!) });
        AddCachePointIfNeeded(result, model, cacheRetention);
        return result;
    }

    /// <summary>【AI】【Bedrock 历史】转换共享重放结果，连续工具结果保持同一个 user 消息。</summary>
    /// <param name="messages">历史内容。</param><param name="model">目标模型。</param><param name="cacheRetention">缓存策略。</param>
    /// <returns>协议消息列表。</returns>
    private static List<object> ConvertMessages(IReadOnlyList<ChatMessage> messages, Model model, CacheRetention cacheRetention)
    {
        var result = new List<object>();
        messages = MessageTransformer.TransformMessages(messages, model, static (id, _, _) => NormalizeToolCallId(id));

        for (var i = 0; i < messages.Count; i++)
        {
            switch (messages[i])
            {
                case UserMessage user:
                    result.Add(new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = ConvertUserContent(user.Content)
                    });
                    break;

                case AssistantMessage assistant:
                    var assistantContent = ConvertAssistantContent(assistant.Content, model);
                    if (assistantContent.Count > 0)
                    {
                        result.Add(new Dictionary<string, object>
                        {
                            ["role"] = "assistant",
                            ["content"] = assistantContent
                        });
                    }
                    break;

                case ToolResultMessage:
                    var toolResults = new List<object>();
                    var cursor = i;
                    while (cursor < messages.Count && messages[cursor] is ToolResultMessage toolResult)
                    {
                        toolResults.Add(ConvertToolResult(toolResult));
                        cursor++;
                    }

                    result.Add(new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = toolResults
                    });
                    i = cursor - 1;
                    break;
            }
        }

        if (result.Count > 0 && cacheRetention != CacheRetention.None && SupportsPromptCaching(model))
        {
            var lastMessage = result[^1] as Dictionary<string, object>;
            if (lastMessage is not null &&
                string.Equals(lastMessage.GetValueOrDefault("role") as string, "user", StringComparison.Ordinal) &&
                lastMessage.GetValueOrDefault("content") is List<object> content)
            {
                AddCachePoint(content, cacheRetention);
            }
        }

        return result;
    }

    /// <summary>【AI】【Bedrock 用户内容】过滤清理后的空白正文，为空数组提供协议占位。</summary>
    /// <param name="content">用户内容。</param><returns>至少含一项的内容数组。</returns>
    private static List<object> ConvertUserContent(IReadOnlyList<ContentBlock> content)
    {
        var result = new List<object>();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent text:
                    if (CreateTextBlock(text.Text) is { } textBlock) result.Add(textBlock);
                    break;
                case ImageContent image:
                    result.Add(new Dictionary<string, object> { ["image"] = CreateImageBlock(image) });
                    break;
            }
        }

        if (result.Count == 0) result.Add(new Dictionary<string, object> { ["text"] = "<empty>" });
        return result;
    }

    /// <summary>【AI】【Bedrock 助手】保留同模型有效思考，工具标识由共享转换层处理。</summary>
    /// <param name="content">已归一内容。</param><param name="model">目标模型。</param><returns>有效协议块。</returns>
    private static List<object> ConvertAssistantContent(
        IReadOnlyList<ContentBlock> content,
        Model model)
    {
        var result = new List<object>();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                    if (CreateTextBlock(text.Text) is { } textBlock) result.Add(textBlock);
                    break;

                case ThinkingContent thinking when !thinking.Redacted && !string.IsNullOrWhiteSpace(thinking.Thinking):
                    if (CreateTextBlock(thinking.Thinking) is not null) result.Add(ConvertThinkingContent(thinking, model));
                    break;

                case ThinkingContent thinking when thinking.Redacted:
                    if (TryDecodeRedactedContent(thinking.ThinkingSignature) is { Length: > 0 } redactedContent)
                    {
                        result.Add(new Dictionary<string, object>
                        {
                            ["reasoningContent"] = new Dictionary<string, object>
                            {
                                ["redactedContent"] = redactedContent
                            }
                        });
                    }
                    break;

                case ToolCallContent toolCall:
                    result.Add(new Dictionary<string, object>
                    {
                        ["toolUse"] = new Dictionary<string, object>
                        {
                            ["toolUseId"] = toolCall.Id,
                            ["name"] = toolCall.Name,
                            ["input"] = ParseArguments(toolCall.Arguments)
                        }
                    });
                    break;
            }
        }

        return result;
    }

    /// <summary>【AI】【Bedrock 思考】Claude 缺少签名时降为正文，其他模型仅回放无签名推理文本。</summary>
    /// <param name="thinking">有效思考。</param><param name="model">模型身份。</param><returns>正文或推理块。</returns>
    private static object ConvertThinkingContent(ThinkingContent thinking, Model model)
    {
        if (SupportsThinkingSignature(model) && string.IsNullOrWhiteSpace(thinking.ThinkingSignature))
            return new Dictionary<string, object> { ["text"] = SanitizeText(thinking.Thinking) };
        var reasoningText = new Dictionary<string, object>
        {
            ["text"] = SanitizeText(thinking.Thinking)
        };
        if (SupportsThinkingSignature(model) && !string.IsNullOrWhiteSpace(thinking.ThinkingSignature))
        {
            reasoningText["signature"] = thinking.ThinkingSignature!;
        }

        return new Dictionary<string, object>
        {
            ["reasoningContent"] = new Dictionary<string, object>
            {
                ["reasoningText"] = reasoningText
            }
        };
    }

    /// <summary>【AI】【Bedrock 工具结果】使用共享转换后的配对标识和明确的成功或失败状态。</summary>
    /// <param name="toolResult">执行结果。</param><returns>协议结果块。</returns>
    private static Dictionary<string, object> ConvertToolResult(ToolResultMessage toolResult)
    {
        return new Dictionary<string, object>
        {
            ["toolResult"] = new Dictionary<string, object>
            {
                ["toolUseId"] = toolResult.ToolCallId,
                ["content"] = ConvertToolResultContent(toolResult.Content),
                ["status"] = toolResult.IsError ? "error" : "success"
            }
        };
    }

    /// <summary>【AI】【Bedrock 结果内容】保留图像和非空正文，空结果使用必需占位。</summary>
    /// <param name="content">工具内容。</param><returns>至少一项的结果块。</returns>
    private static List<object> ConvertToolResultContent(IReadOnlyList<ContentBlock> content)
    {
        var result = new List<object>();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent text:
                    if (CreateTextBlock(text.Text) is { } textBlock) result.Add(textBlock);
                    break;
                case ImageContent image:
                    result.Add(new Dictionary<string, object> { ["image"] = CreateImageBlock(image) });
                    break;
            }
        }

        if (result.Count == 0)
        {
            result.Add(new Dictionary<string, object> { ["text"] = "<empty>" });
        }

        return result;
    }

    private static string SanitizeText(string text) =>
        UnicodeTextSanitizer.RemoveUnpairedSurrogates(text);

    /// <summary>【AI】【Bedrock 文本】先清除无配对代理字符，再检查协议要求的非空白内容。</summary>
    /// <param name="text">原文本。</param><returns>有效文本块或 null。</returns>
    private static Dictionary<string, object>? CreateTextBlock(string text)
    {
        var sanitized = SanitizeText(text);
        return string.IsNullOrWhiteSpace(sanitized) ? null : new Dictionary<string, object> { ["text"] = sanitized };
    }

    private static Dictionary<string, object> CreateImageBlock(ImageContent image) => new()
    {
        ["format"] = GetImageFormat(image.MimeType),
        ["source"] = new Dictionary<string, object>
        {
            ["bytes"] = image.Data
        }
    };

    private static string GetImageFormat(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => "jpeg",
        "image/png" => "png",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => throw new InvalidOperationException($"Unsupported Bedrock image MIME type: {mimeType}")
    };

    private static Dictionary<string, object>? ConvertToolConfig(IReadOnlyList<Tool>? tools, BedrockOptions options)
    {
        if (tools is not { Count: > 0 } ||
            string.Equals(options.ToolChoice, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var result = new Dictionary<string, object>
        {
            ["tools"] = tools.Select(tool => (object)new Dictionary<string, object>
            {
                ["toolSpec"] = new Dictionary<string, object>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["inputSchema"] = new Dictionary<string, object>
                    {
                        ["json"] = tool.ParameterSchema
                    }
                }
            }).ToList()
        };

        var toolChoice = BuildToolChoice(options);
        if (toolChoice is not null)
        {
            result["toolChoice"] = toolChoice;
        }

        return result;
    }

    private static Dictionary<string, object>? BuildToolChoice(BedrockOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ToolChoice))
        {
            return null;
        }

        return options.ToolChoice.Trim().ToLowerInvariant() switch
        {
            "auto" => new Dictionary<string, object> { ["auto"] = new Dictionary<string, object>() },
            "any" => new Dictionary<string, object> { ["any"] = new Dictionary<string, object>() },
            "tool" when !string.IsNullOrWhiteSpace(options.ToolName) => new Dictionary<string, object>
            {
                ["tool"] = new Dictionary<string, object> { ["name"] = options.ToolName! }
            },
            _ => null
        };
    }

    private static Dictionary<string, object>? BuildAdditionalModelRequestFields(Model model, BedrockOptions options)
    {
        if (!model.Reasoning || options.Reasoning is null || !IsClaudeModel(model))
        {
            return null;
        }

        var thinking = new Dictionary<string, object>
        {
            ["type"] = "enabled",
            ["budget_tokens"] = options.ThinkingBudgetTokens ?? MapThinkingBudget(options.Reasoning.Value, options.ThinkingBudgets)
        };
        if (!string.IsNullOrWhiteSpace(options.ThinkingDisplay))
        {
            thinking["display"] = options.ThinkingDisplay!;
        }

        var result = new Dictionary<string, object> { ["thinking"] = thinking };
        if (options.InterleavedThinking == true && !SupportsAdaptiveThinking(model))
        {
            result["anthropic_beta"] = new List<object> { InterleavedThinkingBeta };
        }

        return result;
    }

    private static int MapThinkingBudget(ThinkingLevel level, ThinkingBudgets? budgets) =>
        StreamOptionHelpers.GetThinkingBudget(
            budgets,
            level,
            defaultMinimal: 1_024,
            defaultLow: 2_048,
            defaultMedium: 8_192,
            defaultHigh: 16_384);

    private static void AddCachePointIfNeeded(List<object> blocks, Model model, CacheRetention cacheRetention)
    {
        if (cacheRetention != CacheRetention.None && SupportsPromptCaching(model))
        {
            AddCachePoint(blocks, cacheRetention);
        }
    }

    private static void AddCachePoint(List<object> blocks, CacheRetention cacheRetention)
    {
        var cachePoint = new Dictionary<string, object> { ["type"] = "default" };
        if (cacheRetention == CacheRetention.Long)
        {
            cachePoint["ttl"] = "1h";
        }

        blocks.Add(new Dictionary<string, object> { ["cachePoint"] = cachePoint });
    }

    /// <summary>【AI】【Bedrock 参数】解析历史参数并递归清理服务端不支持的空属性名。</summary>
    /// <param name="arguments">参数 JSON。</param><returns>清理后的文档，损坏输入回退为空对象。</returns>
    private static object ParseArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new Dictionary<string, object>();
        }

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return SanitizeDocument(doc.RootElement);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object>();
        }
    }

    /// <summary>【AI】【Bedrock 文档】递归过滤空键，保留数组顺序和其他键值。</summary>
    /// <param name="value">JSON 节点。</param><returns>独立且可序列化的文档。</returns>
    private static object SanitizeDocument(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Where(property => property.Name.Length > 0)
            .ToDictionary(property => property.Name, property => SanitizeDocument(property.Value), StringComparer.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Select(SanitizeDocument).ToList(),
        _ => value.Clone()
    };

    private static byte[]? TryDecodeRedactedContent(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        try
        {
            return Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>【AI】【Bedrock 调用标识】仅跨模型调用进入此转换，保留至多 64 个协议字符。</summary>
    /// <param name="id">原始标识。</param><returns>规范标识。</returns>
    private static string NormalizeToolCallId(string id)
    {
        var normalized = new string(id
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_')
            .ToArray());
        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private static bool SupportsPromptCaching(Model model)
    {
        var id = model.Id.ToLowerInvariant();
        return id.Contains("claude-3-7-sonnet", StringComparison.Ordinal) ||
               id.Contains("claude-3-5-haiku", StringComparison.Ordinal) ||
               id.Contains("-4-", StringComparison.Ordinal) ||
               id.Contains("-4.", StringComparison.Ordinal);
    }

    private static bool SupportsThinkingSignature(Model model) => IsClaudeModel(model);

    private static bool SupportsAdaptiveThinking(Model model)
    {
        var id = model.Id.ToLowerInvariant();
        return id.Contains("opus-4-6", StringComparison.Ordinal) ||
               id.Contains("opus-4.6", StringComparison.Ordinal) ||
               id.Contains("opus-4-7", StringComparison.Ordinal) ||
               id.Contains("opus-4.7", StringComparison.Ordinal) ||
               id.Contains("sonnet-4-6", StringComparison.Ordinal) ||
               id.Contains("sonnet-4.6", StringComparison.Ordinal);
    }

    private static bool IsClaudeModel(Model model)
    {
        var id = model.Id.ToLowerInvariant();
        return id.Contains("anthropic.claude", StringComparison.Ordinal) ||
               id.Contains("anthropic/claude", StringComparison.Ordinal);
    }
}
