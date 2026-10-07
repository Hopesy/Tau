// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【投影来源】保留原始条目和该条目投影出的消息。</summary>
/// <param name="SourceEntry">未被上下文编辑修改的原始 JSON 条目。</param>
/// <param name="Messages">模型可见消息，状态条目或被省略条目为空。</param>
public sealed record CodingAgentProjectedSessionEntry(JsonElement SourceEntry, IReadOnlyList<ChatMessage> Messages);

/// <summary>【CodingAgent】【投影模型】当前分支的模型选择。</summary>
/// <param name="Provider">提供方。</param>
/// <param name="ModelId">模型标识。</param>
public sealed record CodingAgentProjectionModel(string Provider, string ModelId);

/// <summary>【CodingAgent】【会话投影】保留来源的模型上下文及分支设置。</summary>
/// <param name="Entries">参与上下文的来源条目。</param>
/// <param name="Messages">合并后的模型消息。</param>
/// <param name="ThinkingLevel">思考等级。</param>
/// <param name="Model">当前模型选择。</param>
public sealed record CodingAgentSessionProjection(IReadOnlyList<CodingAgentProjectedSessionEntry> Entries,
    IReadOnlyList<ChatMessage> Messages, string ThinkingLevel, CodingAgentProjectionModel? Model);

/// <summary>【CodingAgent】【上下文编辑】以追加记录保存编辑，并按分支和压缩边界投影上下文。</summary>
public sealed partial class CodingAgentTreeSessionStore
{
    /// <summary>【CodingAgent】【上下文编辑】替换或省略当前分支已有消息的模型内容，保留原始历史。</summary>
    /// <param name="targetId">当前分支上可编辑条目的完整标识。</param>
    /// <param name="replacement">含 content 的 JSON 对象；空值省略该消息。</param>
    /// <returns>新编辑条目的标识。</returns>
    public string AppendContextEdit(string targetId, JsonElement? replacement)
    {
        lock (SyncRoot)
        {
            // 1. 【CodingAgent】【编辑校验】只能修改当前分支中实际贡献用户、助手、工具或自定义消息的条目
            var state = ReadState();
            var target = state.Entries.FirstOrDefault(entry => entry.Id == targetId)
                ?? throw new ArgumentException("Context edit target does not exist.", nameof(targetId));
            if (!state.GetBranch(state.LeafId).Any(entry => entry.Id == targetId))
                throw new ArgumentException("Context edit target is not on the active branch.", nameof(targetId));
            if (target.Type != "custom_message" && (target.Type != "message" || target.Message?.Role is not ("user" or "assistant" or "toolResult" or "custom")))
                throw new ArgumentException("Context edit target does not contribute editable model content.", nameof(targetId));
            var normalized = NormalizeContextReplacement(replacement, target.Message?.Role);
            // 2. 【CodingAgent】【编辑保存】追加不可变记录，删除使用显式 JSON null，分支导出仍可恢复原始内容
            var id = CreateEntryId(state.EntryIds);
            AppendEntry(new CodingAgentTreeSessionEntry
            {
                Type = "context_edit", Id = id, ParentId = state.LeafId, Timestamp = DateTimeOffset.UtcNow,
                TargetId = targetId, Replacement = normalized
            });
            return id;
        }
    }

    /// <summary>【CodingAgent】【上下文读取】读取当前分支经过压缩保留规则筛选的原始条目。</summary>
    /// <returns>原始 JSON 条目副本。</returns>
    public IReadOnlyList<JsonElement> BuildContextEntries()
    {
        lock (SyncRoot)
        {
            var state = ReadState();
            return SelectContextEntries(state.GetBranch(state.LeafId))
                .Select(entry => JsonSerializer.SerializeToElement(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry)).ToArray();
        }
    }

    /// <summary>【CodingAgent】【上下文投影】生成当前分支的完整投影。</summary>
    /// <returns>带原始条目来源的上下文。</returns>
    public CodingAgentSessionProjection BuildSessionProjection()
    {
        lock (SyncRoot)
        {
            var state = ReadState();
            return ProjectBranch(state.GetBranch(state.LeafId));
        }
    }

    /// <summary>【CodingAgent】【分支投影】生成指定叶节点的投影；空标识返回空上下文。</summary>
    /// <param name="leafId">叶节点标识；空值表示根之前。</param>
    /// <returns>指定分支的投影。</returns>
    public CodingAgentSessionProjection BuildSessionProjection(string? leafId)
    {
        lock (SyncRoot) return ProjectBranch(ReadState().GetBranch(leafId));
    }

    /// <summary>【CodingAgent】【压缩投影】保留最新压缩检查点、指定尾部及压缩后的条目。</summary>
    /// <param name="branch">完整父链。</param>
    /// <returns>保留的条目列表。</returns>
    private static IReadOnlyList<CodingAgentTreeSessionEntry> SelectContextEntries(IReadOnlyList<CodingAgentTreeSessionEntry> branch)
    {
        var index = -1;
        for (var i = branch.Count - 1; i >= 0; i--)
            if (branch[i].Type == "compaction") { index = i; break; }
        if (index < 0) return branch;
        var selected = new List<CodingAgentTreeSessionEntry> { branch[index] };
        var keep = false;
        for (var i = 0; i < index; i++)
        {
            if (branch[i].Id == branch[index].FirstKeptEntryId) keep = true;
            if (keep && !(branch[i].Type == "message" && branch[i].Message?.Role == "system")) selected.Add(branch[i]);
        }
        selected.AddRange(branch.Skip(index + 1));
        return selected;
    }

    /// <summary>【CodingAgent】【上下文投影】应用每个目标的最新编辑，并独立保留消息元数据。</summary>
    /// <param name="branch">选中分支的完整父链。</param>
    /// <param name="legacyMessages">是否为旧运行器恢复保留文本摘要消息格式。</param>
    /// <param name="applyContextEdits">是否应用模型上下文编辑；历史显示关闭该选项以保留原文。</param>
    /// <returns>消息、来源和模型设置。</returns>
    internal static CodingAgentSessionProjection ProjectBranch(IReadOnlyList<CodingAgentTreeSessionEntry> branch, bool legacyMessages = false, bool applyContextEdits = true)
    {
        // 1. 【CodingAgent】【投影设置】设置来自完整分支，编辑只来自当前压缩窗口中保留的条目
        var thinking = "off";
        CodingAgentProjectionModel? model = null;
        foreach (var entry in branch)
        {
            if (entry.Type == "thinking_level_change") thinking = entry.ThinkingLevel ?? "off";
            if (entry.Type is "model_change" or "session_info" && entry.Provider is not null && entry.Model is not null)
                model = new(entry.Provider, entry.Model);
            if (entry.Type == "message" && entry.Message?.Role == "assistant" && entry.Message.Provider is not null && entry.Message.Model is not null)
                model = new(entry.Message.Provider, entry.Message.Model);
        }
        var selected = SelectContextEntries(branch);
        var edits = new Dictionary<string, CodingAgentTreeSessionEntry>(StringComparer.Ordinal);
        foreach (var entry in selected)
            if (applyContextEdits && entry.Type == "context_edit" && entry.TargetId is not null) edits[entry.TargetId] = entry;
        // 2. 【CodingAgent】【投影消息】元数据和被省略的条目仍在来源表中，只有其消息贡献为空
        var projected = new List<CodingAgentProjectedSessionEntry>();
        for (var index = 0; index < selected.Count; index++)
        {
            var entry = selected[index];
            var messages = new List<ChatMessage>();
            if (entry.Type == "compaction" && index == 0)
            {
                if (entry.SystemMessage is not null && CodingAgentSessionStore.ToMessage(entry.SystemMessage) is SystemMessage system) messages.Add(system);
                messages.Add(legacyMessages
                    ? CodingAgentCompactionMessages.CreateSummaryMessage(entry.Summary ?? "", entry.TurnPrefixSummary)
                    : new AgentCompactionSummaryMessage(entry.Summary ?? "", entry.TokensBefore ?? 0, entry.Timestamp));
            }
            else if (!legacyMessages && entry.Type == "branch_summary" && !string.IsNullOrWhiteSpace(entry.Summary))
                messages.Add(new AgentBranchSummaryMessage(entry.Summary, entry.FromId ?? "", entry.Timestamp));
            else if (entry.Type != "compaction") AppendSnapshotMessage(messages, entry);
            if (edits.TryGetValue(entry.Id, out var edit))
            {
                if (edit.Replacement is null || edit.Replacement.Value.ValueKind == JsonValueKind.Null) messages.Clear();
                else if (edit.Replacement.Value.TryGetProperty("content", out var content))
                    messages = messages.Select(message => ReplaceMessageContent(message, ReadProjectedContent(content))).ToList();
            }
            projected.Add(new(JsonSerializer.SerializeToElement(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry), messages));
        }
        return new(projected, projected.SelectMany(entry => entry.Messages).ToArray(), thinking, model);
    }

    /// <summary>【CodingAgent】【内容替换】只替换内容，保留消息角色、用量、签名及调用标识等元数据。</summary>
    /// <param name="message">原始消息。</param>
    /// <param name="content">替换内容。</param>
    /// <returns>新消息；不可编辑角色原样返回。</returns>
    private static ChatMessage ReplaceMessageContent(ChatMessage message, IReadOnlyList<ContentBlock> content) => message switch
    {
        UserMessage user => user with { Content = content },
        AssistantMessage assistant => assistant with { Content = content },
        ToolResultMessage tool => tool with { Content = content },
        AgentCustomMessage custom => custom with { Content = content },
        _ => message
    };

    /// <summary>【CodingAgent】【投影内容】把字符串或内容块数组转换为运行器内容。</summary>
    /// <param name="content">编辑后的 JSON 内容。</param>
    /// <returns>内容块列表。</returns>
    private static IReadOnlyList<ContentBlock> ReadProjectedContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return [new TextContent(content.GetString()!)];
        if (content.ValueKind != JsonValueKind.Array) return [];
        var envelope = """{"role":"user","content":""" + content.GetRawText() + "}";
        var stored = JsonSerializer.Deserialize(envelope, CodingAgentSessionJsonContext.Default.CodingAgentSessionMessage);
        return (stored?.Content ?? []).Select(CodingAgentSessionStore.ToContent).OfType<ContentBlock>().ToArray();
    }

    /// <summary>【CodingAgent】【编辑规范化】校验内容形状并规范化助手与工具结果的字符串内容。</summary>
    /// <param name="replacement">原始替换对象或空值。</param>
    /// <param name="role">目标消息角色。</param>
    /// <returns>独立的标准替换对象。</returns>
    internal static JsonElement NormalizeContextReplacement(JsonElement? replacement, string? role)
    {
        if (replacement is null || replacement.Value.ValueKind == JsonValueKind.Null)
        {
            using var empty = JsonDocument.Parse("null");
            return empty.RootElement.Clone();
        }
        var value = replacement.Value;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("content", out var content)
            || content.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
            throw new ArgumentException("Context edit replacement must contain string or array content.", nameof(replacement));
        if (content.ValueKind != JsonValueKind.String || role is not ("assistant" or "toolResult")) return value.Clone();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteStartArray("content"); writer.WriteStartObject();
            writer.WriteString("type", "text"); writer.WriteString("text", content.GetString());
            writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
