using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tau.Ai.Providers.OpenAi;

/// <summary>
/// Converts between Tau message types and OpenAI API format.
/// </summary>
internal static class OpenAiMessageConverter
{
    public static JsonElement ConvertMessages(
        IReadOnlyList<ChatMessage> messages,
        bool supportsImages = true,
        bool requiresThinkingAsText = false,
        bool requiresToolResultName = false,
        bool requiresAssistantAfterToolResult = false,
        bool requiresReasoningContentOnAssistantMessages = false,
        bool modelReasoning = false)
    {
        var array = new List<object>();
        var lastRole = string.Empty;
        foreach (var msg in messages)
        {
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
                case UserMessage user:
                    array.Add(ConvertUserMessage(user, supportsImages));
                    break;
                case AssistantMessage assistant:
                    array.Add(ConvertAssistantMessage(
                        assistant,
                        requiresThinkingAsText,
                        requiresReasoningContentOnAssistantMessages,
                        modelReasoning));
                    break;
                case ToolResultMessage toolResult:
                    array.Add(ConvertToolResultMessage(toolResult, requiresToolResultName));
                    break;
            }

            lastRole = msg.Role;
        }
        return JsonSerializer.SerializeToElement(array, OpenAiJsonContext.Default.ListObject);
    }

    private static object ConvertUserMessage(UserMessage msg, bool supportsImages)
    {
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
                case TextContent text:
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

        return new Dictionary<string, object>
        {
            ["role"] = "user",
            ["content"] = parts.Count == 0 ? string.Empty : parts
        };
    }

    private static object ConvertAssistantMessage(
        AssistantMessage msg,
        bool requiresThinkingAsText,
        bool requiresReasoningContentOnAssistantMessages,
        bool modelReasoning)
    {
        var result = new Dictionary<string, object> { ["role"] = "assistant" };

        var toolCalls = msg.Content.OfType<ToolCallContent>().ToList();
        var textParts = msg.Content.OfType<TextContent>().ToList();
        var thinkingParts = requiresThinkingAsText
            ? msg.Content.OfType<ThinkingContent>().Where(t => !string.IsNullOrWhiteSpace(t.Thinking)).ToList()
            : [];

        if (textParts.Count > 0 || thinkingParts.Count > 0)
            result["content"] = SanitizeText(string.Concat(
                thinkingParts.Select(t => t.Thinking).Concat(textParts.Select(t => t.Text))));

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

        if (requiresReasoningContentOnAssistantMessages && modelReasoning)
        {
            result["reasoning_content"] = string.Empty;
        }

        return result;
    }

    private static object ConvertToolResultMessage(ToolResultMessage msg, bool requiresToolResultName)
    {
        var text = SanitizeText(string.Join("", msg.Content.OfType<TextContent>().Select(t => t.Text)));
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

    public static JsonElement ConvertTools(IReadOnlyList<Tool> tools, bool supportsStrictMode = false)
    {
        var array = new List<object>();
        foreach (var tool in tools)
        {
            var function = new Dictionary<string, object>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.ParameterSchema
            };
            if (supportsStrictMode)
            {
                function["strict"] = false;
            }

            array.Add(new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = function
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
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(JsonElement))]
internal partial class OpenAiJsonContext : JsonSerializerContext;
