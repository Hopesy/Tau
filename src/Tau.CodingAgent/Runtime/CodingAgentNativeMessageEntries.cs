// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentTreeSessionStore
{
    /// <summary>【CodingAgent】【原生消息条目】将普通消息和扩展消息分别写为 message 与 custom_message，保留私有详情和可见性。</summary>
    /// <param name="message">已提交到会话的消息。</param>
    /// <param name="id">新条目标识。</param>
    /// <param name="parentId">当前分支叶节点。</param>
    /// <returns>按原生会话格式构造的条目。</returns>
    internal static CodingAgentTreeSessionEntry CreateMessageEntry(ChatMessage message, string id, string? parentId)
    {
        var stored = CodingAgentSessionStore.FromMessage(message);
        if (message is AgentCustomMessage custom)
        {
            var native = JsonSerializer.SerializeToElement(stored, CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage);
            return new()
            {
                Type = "custom_message", Id = id, ParentId = parentId, Timestamp = custom.Timestamp ?? DateTimeOffset.UtcNow,
                CustomType = custom.CustomType, Content = native.GetProperty("content").Clone(), Display = custom.Display, Details = stored.Details
            };
        }
        return new() { Type = "message", Id = id, ParentId = parentId, Timestamp = DateTimeOffset.UtcNow, Message = stored };
    }

    /// <summary>【CodingAgent】【自定义消息预览】恢复原生扩展消息并复用文本及图片块预览格式。</summary>
    /// <param name="entry">原生 custom_message 条目。</param>
    /// <returns>适合树列表显示的文本。</returns>
    private static string PreviewCustomMessage(CodingAgentTreeSessionEntry entry) => entry.Content is { } content
        ? PreviewText(FormatContentPreview(ReadProjectedContent(content))) : "";
}
