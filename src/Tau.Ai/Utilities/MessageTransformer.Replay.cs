// 作者：xxx
using Tau.Ai.Registry;

namespace Tau.Ai.Utilities;

public static partial class MessageTransformer
{
    /// <summary>【AI】【历史重放】转换跨模型签名和工具标识，跳过失败响应并补齐未完成的工具调用。</summary>
    /// <param name="messages">原始历史，不会被修改。</param><param name="model">接收历史的目标模型。</param>
    /// <param name="normalizeToolCallId">跨模型工具 ID 转换器，参数为 ID、目标模型与来源助手。</param>
    /// <returns>可供目标协议重放的独立消息序列。</returns>
    public static IReadOnlyList<ChatMessage> TransformMessages(IReadOnlyList<ChatMessage> messages, Model model,
        Func<string, Model, AssistantMessage, string>? normalizeToolCallId = null)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var transformed = new List<ChatMessage>();
        // 1. 【AI】【来源转换】同模型保留签名，跨模型思考降为文本，调用与结果同步更新 ID
        foreach (var message in DowngradeUnsupportedImages(messages, model))
        {
            if (message is ToolResultMessage tool && ids.TryGetValue(tool.ToolCallId, out var normalizedId))
            {
                transformed.Add(tool with { ToolCallId = normalizedId });
                continue;
            }
            if (message is not AssistantMessage assistant)
            {
                transformed.Add(message);
                continue;
            }
            var sameModel = assistant.Provider == model.Provider && assistant.Model == model.Id &&
                ModelApiNames.Normalize(assistant.Api) == ModelApiNames.Normalize(model.Api);
            var content = new List<ContentBlock>();
            foreach (var block in assistant.Content)
            {
                switch (block)
                {
                    case ThinkingContent { Redacted: true } redacted:
                        if (sameModel) content.Add(redacted);
                        break;
                    case ThinkingContent thinking:
                        if (sameModel && !string.IsNullOrEmpty(thinking.ThinkingSignature)) content.Add(thinking);
                        else if (!string.IsNullOrWhiteSpace(thinking.Thinking))
                            content.Add(sameModel ? thinking : new TextContent(thinking.Thinking));
                        break;
                    case TextContent text:
                        content.Add(sameModel ? text : new TextContent(text.Text));
                        break;
                    case ToolCallContent call:
                        var next = sameModel ? call : call with { ThoughtSignature = null };
                        if (!sameModel && normalizeToolCallId is not null)
                        {
                            var id = normalizeToolCallId(call.Id, model, assistant);
                            if (id != call.Id)
                            {
                                ids[call.Id] = id;
                                next = next with { Id = id };
                            }
                        }
                        content.Add(next);
                        break;
                    default:
                        content.Add(block);
                        break;
                }
            }
            transformed.Add(assistant with { Content = content });
        }

        var result = new List<ChatMessage>();
        IReadOnlyList<ToolCallContent> pending = [];
        var answered = new HashSet<string>(StringComparer.Ordinal);
        var heldSystems = new List<ChatMessage>();
        /// <summary>【AI】【工具收尾】补齐缺少结果的调用，再释放期间暂存的系统消息。</summary>
        void ClosePending()
        {
            foreach (var call in pending)
                if (!answered.Contains(call.Id))
                    result.Add(new ToolResultMessage(call.Id, [new TextContent("No result provided")], IsError: true)
                    {
                        ToolName = call.Name, Timestamp = DateTimeOffset.UtcNow
                    });
            pending = [];
            answered.Clear();
            result.AddRange(heldSystems);
            heldSystems.Clear();
        }
        // 2. 【AI】【工具配对】系统更新不打断工具结果配对，用户或下一助手开始前关闭上一组调用
        foreach (var message in transformed)
        {
            if (message is AssistantMessage assistant)
            {
                ClosePending();
                if (assistant.StopReason is StopReason.Error or StopReason.Aborted) continue;
                pending = assistant.Content.OfType<ToolCallContent>().ToArray();
                result.Add(assistant);
            }
            else if (message is ToolResultMessage tool)
            {
                answered.Add(tool.ToolCallId);
                result.Add(tool);
            }
            else if (message is SystemMessage && pending.Count > 0) heldSystems.Add(message);
            else
            {
                if (message is UserMessage) ClosePending();
                result.Add(message);
            }
        }
        ClosePending();
        return result;
    }

    /// <summary>【AI】【旧历史兼容】将未类型化调用方提供的 null 内容归一为空集合，其余消息保持引用。</summary>
    /// <param name="messages">原消息序列。</param><returns>有缺失内容时返回副本，否则返回原集合。</returns>
    private static IReadOnlyList<ChatMessage> NormalizeMissingContent(IReadOnlyList<ChatMessage> messages)
    {
        List<ChatMessage>? copy = null;
        for (var index = 0; index < messages.Count; index++)
        {
            var original = messages[index];
            var normalized = original switch
            {
                UserMessage { Content: null } user => (ChatMessage)(user with { Content = [] }),
                AssistantMessage { Content: null } assistant => assistant with { Content = [] },
                ToolResultMessage { Content: null } tool => tool with { Content = [] },
                _ => original
            };
            if (!ReferenceEquals(original, normalized)) copy ??= messages.Take(index).ToList();
            copy?.Add(normalized);
        }
        return copy is null ? messages : copy;
    }
}
