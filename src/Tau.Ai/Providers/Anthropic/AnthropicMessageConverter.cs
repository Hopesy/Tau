using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tau.Ai.Providers.Anthropic;

/// <summary>
/// Converts between Tau message types and Anthropic Messages API format.
/// Anthropic uses content-block arrays natively, and tool_result is carried in user messages.
/// </summary>
internal static class AnthropicMessageConverter
{
    public static List<object> ConvertMessages(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyDictionary<string, object>? cacheControl = null,
        bool allowEmptySignature = false)
    {
        var result = new List<object>();
        var i = 0;

        while (i < messages.Count)
        {
            var msg = messages[i];

            switch (msg)
            {
                case UserMessage user:
                    result.Add(ConvertUserMessage(user));
                    i++;
                    break;

                case AssistantMessage assistant:
                    result.Add(ConvertAssistantMessage(assistant, allowEmptySignature));
                    i++;
                    break;

                case ToolResultMessage:
                    // Coalesce consecutive tool_result messages into a single user message
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

        ApplyCacheControlToLastUserMessage(result, cacheControl);
        return result;
    }

    private static object ConvertUserMessage(UserMessage msg)
    {
        var parts = new List<object>();
        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case TextContent text:
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

        return new Dictionary<string, object>
        {
            ["role"] = "user",
            ["content"] = parts
        };
    }

    private static object ConvertAssistantMessage(AssistantMessage msg, bool allowEmptySignature)
    {
        var parts = new List<object>();
        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case TextContent text:
                    parts.Add(new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = SanitizeText(text.Text)
                    });
                    break;
                case ThinkingContent thinking when !thinking.Redacted:
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
                        ["name"] = toolCall.Name,
                        ["input"] = ParseArgumentsToInput(toolCall.Arguments)
                    });
                    break;
            }
        }

        return new Dictionary<string, object>
        {
            ["role"] = "assistant",
            ["content"] = parts
        };
    }

    private static object ConvertToolResultBlock(ToolResultMessage msg)
    {
        var content = SanitizeText(msg.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "");
        var result = new Dictionary<string, object>
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = msg.ToolCallId,
            ["content"] = content
        };
        if (msg.IsError)
            result["is_error"] = true;
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

    public static List<object> ConvertTools(
        IReadOnlyList<Tool> tools,
        bool supportsEagerToolInputStreaming = true,
        IReadOnlyDictionary<string, object>? cacheControl = null)
    {
        var converted = tools.Select(t =>
        {
            var tool = new Dictionary<string, object>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.ParameterSchema
            };
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

    private static void ApplyCacheControlToLastUserMessage(
        List<object> messages,
        IReadOnlyDictionary<string, object>? cacheControl)
    {
        if (cacheControl is null)
        {
            return;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not Dictionary<string, object> message ||
                !message.TryGetValue("role", out var role) ||
                !string.Equals(Convert.ToString(role), "user", StringComparison.Ordinal) ||
                !message.TryGetValue("content", out var content))
            {
                continue;
            }

            if (content is List<object> blocks)
            {
                ApplyCacheControlToLastContentBlock(blocks, cacheControl);
                return;
            }
        }
    }

    private static void ApplyCacheControlToLastContentBlock(
        List<object> blocks,
        IReadOnlyDictionary<string, object> cacheControl)
    {
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i] is not Dictionary<string, object> block ||
                !block.TryGetValue("type", out var type))
            {
                continue;
            }

            var blockType = Convert.ToString(type);
            if (blockType is "text" or "image" or "tool_result")
            {
                block["cache_control"] = cacheControl;
                return;
            }
        }
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
