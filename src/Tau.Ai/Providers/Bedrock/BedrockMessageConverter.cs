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

    private static List<object> ConvertMessages(IReadOnlyList<ChatMessage> messages, Model model, CacheRetention cacheRetention)
    {
        var result = new List<object>();
        var toolCallIds = new Dictionary<string, string>(StringComparer.Ordinal);

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
                    var assistantContent = ConvertAssistantContent(assistant.Content, model, toolCallIds);
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
                        toolResults.Add(ConvertToolResult(toolResult, toolCallIds));
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

    private static List<object> ConvertUserContent(IReadOnlyList<ContentBlock> content)
    {
        var result = new List<object>();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent text:
                    result.Add(new Dictionary<string, object> { ["text"] = SanitizeText(text.Text) });
                    break;
                case ImageContent image:
                    result.Add(new Dictionary<string, object> { ["image"] = CreateImageBlock(image) });
                    break;
            }
        }

        return result;
    }

    private static List<object> ConvertAssistantContent(
        IReadOnlyList<ContentBlock> content,
        Model model,
        IDictionary<string, string> toolCallIds)
    {
        var result = new List<object>();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                    result.Add(new Dictionary<string, object> { ["text"] = SanitizeText(text.Text) });
                    break;

                case ThinkingContent thinking when !thinking.Redacted && !string.IsNullOrWhiteSpace(thinking.Thinking):
                    result.Add(ConvertThinkingContent(thinking, model));
                    break;

                case ToolCallContent toolCall:
                    var toolUseId = NormalizeToolCallId(toolCall.Id);
                    toolCallIds[toolCall.Id] = toolUseId;
                    result.Add(new Dictionary<string, object>
                    {
                        ["toolUse"] = new Dictionary<string, object>
                        {
                            ["toolUseId"] = toolUseId,
                            ["name"] = toolCall.Name,
                            ["input"] = ParseArguments(toolCall.Arguments)
                        }
                    });
                    break;
            }
        }

        return result;
    }

    private static object ConvertThinkingContent(ThinkingContent thinking, Model model)
    {
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

    private static Dictionary<string, object> ConvertToolResult(
        ToolResultMessage toolResult,
        IDictionary<string, string> toolCallIds)
    {
        var toolUseId = toolCallIds.TryGetValue(toolResult.ToolCallId, out var normalized)
            ? normalized
            : NormalizeToolCallId(toolResult.ToolCallId);

        return new Dictionary<string, object>
        {
            ["toolResult"] = new Dictionary<string, object>
            {
                ["toolUseId"] = toolUseId,
                ["content"] = ConvertToolResultContent(toolResult.Content),
                ["status"] = toolResult.IsError ? "error" : "success"
            }
        };
    }

    private static List<object> ConvertToolResultContent(IReadOnlyList<ContentBlock> content)
    {
        var result = new List<object>();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent text:
                    result.Add(new Dictionary<string, object> { ["text"] = SanitizeText(text.Text) });
                    break;
                case ImageContent image:
                    result.Add(new Dictionary<string, object> { ["image"] = CreateImageBlock(image) });
                    break;
            }
        }

        if (result.Count == 0)
        {
            result.Add(new Dictionary<string, object> { ["text"] = string.Empty });
        }

        return result;
    }

    private static string SanitizeText(string text) =>
        UnicodeTextSanitizer.RemoveUnpairedSurrogates(text);

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

    private static object ParseArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new Dictionary<string, object>();
        }

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object>();
        }
    }

    private static string NormalizeToolCallId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "tool_call";
        }

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
