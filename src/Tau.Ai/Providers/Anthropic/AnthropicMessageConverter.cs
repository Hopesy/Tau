using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tau.Ai.Providers.Anthropic;

/// <summary>
/// Converts between Tau message types and Anthropic Messages API format.
/// Anthropic uses content-block arrays natively, and tool_result is carried in user messages.
/// </summary>
internal static class AnthropicMessageConverter
{
    public static List<object> ConvertMessages(IReadOnlyList<ChatMessage> messages)
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
                    result.Add(ConvertAssistantMessage(assistant));
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

    private static object ConvertAssistantMessage(AssistantMessage msg)
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
                    var thinkingBlock = new Dictionary<string, object>
                    {
                        ["type"] = "thinking",
                        ["thinking"] = SanitizeText(thinking.Thinking)
                    };
                    if (thinking.ThinkingSignature is not null)
                        thinkingBlock["signature"] = thinking.ThinkingSignature;
                    parts.Add(thinkingBlock);
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

    public static List<object> ConvertTools(IReadOnlyList<Tool> tools)
    {
        return tools.Select(t => (object)new Dictionary<string, object>
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["input_schema"] = t.ParameterSchema
        }).ToList();
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
