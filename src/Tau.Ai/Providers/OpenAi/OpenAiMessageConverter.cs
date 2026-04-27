using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tau.Ai.Providers.OpenAi;

/// <summary>
/// Converts between Tau message types and OpenAI API format.
/// </summary>
internal static class OpenAiMessageConverter
{
    public static JsonElement ConvertMessages(IReadOnlyList<ChatMessage> messages)
    {
        var array = new List<object>();
        foreach (var msg in messages)
        {
            switch (msg)
            {
                case UserMessage user:
                    array.Add(ConvertUserMessage(user));
                    break;
                case AssistantMessage assistant:
                    array.Add(ConvertAssistantMessage(assistant));
                    break;
                case ToolResultMessage toolResult:
                    array.Add(ConvertToolResultMessage(toolResult));
                    break;
            }
        }
        return JsonSerializer.SerializeToElement(array, OpenAiJsonContext.Default.ListObject);
    }

    private static object ConvertUserMessage(UserMessage msg)
    {
        var hasNonText = msg.Content.Any(c => c is not TextContent);
        if (!hasNonText)
        {
            var text = string.Join("", msg.Content.OfType<TextContent>().Select(t => t.Text));
            return new Dictionary<string, object> { ["role"] = "user", ["content"] = text };
        }

        var parts = new List<object>();
        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case TextContent text:
                    parts.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = text.Text });
                    break;
                case ImageContent image:
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
        return new Dictionary<string, object> { ["role"] = "user", ["content"] = parts };
    }

    private static object ConvertAssistantMessage(AssistantMessage msg)
    {
        var result = new Dictionary<string, object> { ["role"] = "assistant" };

        var toolCalls = msg.Content.OfType<ToolCallContent>().ToList();
        var textParts = msg.Content.OfType<TextContent>().ToList();

        if (textParts.Count > 0)
            result["content"] = string.Join("", textParts.Select(t => t.Text));

        if (toolCalls.Count > 0)
        {
            var serializedToolCalls = new List<object>();
            foreach (var tc in toolCalls)
            {
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

        return result;
    }

    private static object ConvertToolResultMessage(ToolResultMessage msg)
    {
        var text = string.Join("", msg.Content.OfType<TextContent>().Select(t => t.Text));
        return new Dictionary<string, object>
        {
            ["role"] = "tool",
            ["tool_call_id"] = msg.ToolCallId,
            ["content"] = text
        };
    }

    public static JsonElement ConvertTools(IReadOnlyList<Tool> tools)
    {
        var array = new List<object>();
        foreach (var tool in tools)
        {
            array.Add(new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.ParameterSchema
                }
            });
        }

        return JsonSerializer.SerializeToElement(array, OpenAiJsonContext.Default.ListObject);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
internal partial class OpenAiJsonContext : JsonSerializerContext;
