using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tau.Ai.Providers.Google;

/// <summary>
/// Converts between Tau message types and Google Gemini API format.
/// Gemini uses `contents` with role ("user"|"model") and `parts` arrays.
/// Tool results go into parts as functionResponse.
/// </summary>
internal static class GoogleMessageConverter
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
                    result.Add(BuildContent("user", ConvertUserParts(user)));
                    i++;
                    break;

                case AssistantMessage assistant:
                    result.Add(BuildContent("model", ConvertAssistantParts(assistant)));
                    i++;
                    break;

                case ToolResultMessage:
                    var parts = new List<object>();
                    while (i < messages.Count && messages[i] is ToolResultMessage tr)
                    {
                        parts.Add(BuildFunctionResponsePart(tr));
                        i++;
                    }
                    result.Add(BuildContent("user", parts));
                    break;

                default:
                    i++;
                    break;
            }
        }

        return result;
    }

    private static Dictionary<string, object> BuildContent(string role, List<object> parts) => new()
    {
        ["role"] = role,
        ["parts"] = parts
    };

    private static List<object> ConvertUserParts(UserMessage msg)
    {
        var parts = new List<object>();
        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case TextContent text:
                    parts.Add(new Dictionary<string, object> { ["text"] = text.Text });
                    break;
                case ImageContent image:
                    parts.Add(new Dictionary<string, object>
                    {
                        ["inlineData"] = new Dictionary<string, string>
                        {
                            ["mimeType"] = image.MimeType,
                            ["data"] = image.Data
                        }
                    });
                    break;
            }
        }
        return parts;
    }

    private static List<object> ConvertAssistantParts(AssistantMessage msg)
    {
        var parts = new List<object>();
        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    parts.Add(new Dictionary<string, object> { ["text"] = text.Text });
                    break;
                case ToolCallContent toolCall:
                    var fn = new Dictionary<string, object>
                    {
                        ["name"] = toolCall.Name,
                        ["args"] = ParseArgs(toolCall.Arguments)
                    };
                    parts.Add(new Dictionary<string, object> { ["functionCall"] = fn });
                    break;
            }
        }
        return parts;
    }

    private static Dictionary<string, object> BuildFunctionResponsePart(ToolResultMessage msg)
    {
        var content = msg.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
        return new Dictionary<string, object>
        {
            ["functionResponse"] = new Dictionary<string, object>
            {
                ["name"] = msg.ToolCallId,
                ["response"] = new Dictionary<string, object>
                {
                    ["content"] = content
                }
            }
        };
    }

    private static object ParseArgs(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return new Dictionary<string, object>();

        try
        {
            return JsonSerializer.Deserialize(arguments, GoogleJsonContext.Default.JsonElement);
        }
        catch
        {
            return new Dictionary<string, object>();
        }
    }

    public static List<object> ConvertTools(IReadOnlyList<Tool> tools)
    {
        var functionDeclarations = tools.Select(t => (object)new Dictionary<string, object>
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["parameters"] = SanitizeSchema(t.ParameterSchema)
        }).ToList();

        return [new Dictionary<string, object>
        {
            ["functionDeclarations"] = functionDeclarations
        }];
    }

    /// <summary>
    /// Gemini rejects JSON Schema keywords it doesn't recognize ($schema, additionalProperties).
    /// Strip them defensively.
    /// </summary>
    private static object SanitizeSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return schema;

        var result = new Dictionary<string, object>();
        foreach (var prop in schema.EnumerateObject())
        {
            if (prop.Name is "$schema" or "additionalProperties")
                continue;

            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Object => SanitizeSchema(prop.Value),
                JsonValueKind.Array => SanitizeArray(prop.Value),
                _ => prop.Value
            };
        }
        return result;
    }

    private static object SanitizeArray(JsonElement array)
    {
        var items = new List<object>();
        foreach (var item in array.EnumerateArray())
        {
            items.Add(item.ValueKind == JsonValueKind.Object ? SanitizeSchema(item) : item);
        }
        return items;
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
internal partial class GoogleJsonContext : JsonSerializerContext;
