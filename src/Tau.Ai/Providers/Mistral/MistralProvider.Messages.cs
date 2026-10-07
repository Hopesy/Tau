// 作者：xxx
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.Mistral;

public sealed partial class MistralProvider
{
    /// <summary>【Mistral】【消息转换】保留系统更新、正文及思考块，回放已归一的工具调用与结果。</summary>
    /// <param name="context">完成共享历史转换的上下文。</param><returns>协议消息。</returns>
    private static List<object> ConvertMessages(LlmContext context)
    {
        var messages = new List<object>();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < context.Messages.Count; index++)
        {
            switch (context.Messages[index])
            {
                case SystemMessage system:
                    var text = index == 0 ? Transcript.GetSystemMessageText(system) : Transcript.RenderSystemMessageUpdate(system);
                    if (text.Length > 0) messages.Add(new Dictionary<string, object> { ["role"] = "system", ["content"] = SanitizeText(text) });
                    break;
                case UserMessage user:
                    if (user.Content.Count == 0) break;
                    // 1. 【Mistral】【用户内容】单文本继续使用字符串，多块与图像按原顺序发送
                    object content = user.Content is [TextContent single]
                        ? SanitizeText(single.Text)
                        : user.Content.Select(ContentPart).Where(part => part is not null).Cast<object>().ToList();
                    messages.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = content });
                    break;
                case AssistantMessage assistant:
                    if (ConvertAssistantMessage(assistant) is { } converted) messages.Add(converted);
                    toolNames.Clear();
                    foreach (var call in assistant.Content.OfType<ToolCallContent>()) toolNames[call.Id] = call.Name;
                    break;
                case ToolResultMessage tool:
                    // 2. 【Mistral】【结果图文】统一返回内容数组，工具图片与摘要一起回传
                    var textResult = string.Join("\n", tool.Content.OfType<TextContent>().Select(part => SanitizeText(part.Text))).Trim();
                    var images = tool.Content.OfType<ImageContent>().ToArray();
                    var output = textResult.Length > 0 ? textResult : images.Length > 0 ? "(see attached image)" : "(no tool output)";
                    var parts = new List<object> { new Dictionary<string, object> { ["type"] = "text", ["text"] = tool.IsError ? "[tool error] " + output : output } };
                    parts.AddRange(images.Select(image => ContentPart(image)!));
                    var result = new Dictionary<string, object> { ["role"] = "tool", ["tool_call_id"] = tool.ToolCallId, ["content"] = parts };
                    if ((tool.ToolName ?? toolNames.GetValueOrDefault(tool.ToolCallId)) is { } name) result["name"] = name;
                    messages.Add(result);
                    break;
            }
        }
        return messages;
    }

    /// <summary>【Mistral】【助手内容】保留同模型思考块及正文边界，为工具调用添加固定 index。</summary>
    /// <param name="message">已完成来源过滤的助手。</param><returns>协议消息，无有效内容时为 null。</returns>
    private static Dictionary<string, object>? ConvertAssistantMessage(AssistantMessage message)
    {
        var parts = new List<object>();
        var calls = new List<object>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                    parts.Add(ContentPart(text)!);
                    break;
                case ThinkingContent thinking when !string.IsNullOrWhiteSpace(thinking.Thinking):
                    parts.Add(new Dictionary<string, object>
                    {
                        ["type"] = "thinking", ["thinking"] = new List<object>
                        { new Dictionary<string, object> { ["type"] = "text", ["text"] = SanitizeText(thinking.Thinking) } }
                    });
                    break;
                case ToolCallContent call:
                    calls.Add(new Dictionary<string, object>
                    {
                        ["id"] = call.Id, ["type"] = "function", ["index"] = 0,
                        ["function"] = new Dictionary<string, object> { ["name"] = call.Name, ["arguments"] = call.Arguments }
                    });
                    break;
            }
        }
        if (parts.Count == 0 && calls.Count == 0) return null;
        var result = new Dictionary<string, object> { ["role"] = "assistant", ["prefix"] = false };
        if (parts.Count > 0) result["content"] = parts;
        if (calls.Count > 0) result["tool_calls"] = calls;
        return result;
    }

    /// <summary>【Mistral】【内容块】转换正文和图像为 wire 格式，忽略用户或工具结果中的未知块。</summary>
    /// <param name="block">文本或图像。</param><returns>内容项，未知类型为空。</returns>
    private static object? ContentPart(ContentBlock block) => block switch
    {
        TextContent text => new Dictionary<string, object> { ["type"] = "text", ["text"] = SanitizeText(text.Text) },
        ImageContent image => new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = $"data:{image.MimeType};base64,{image.Data}" },
        _ => null
    };
}
