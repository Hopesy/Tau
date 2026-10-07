// 作者：xxx
using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

internal sealed partial class CodingAgentExtensionSessionBridge
{
    /// <summary>【CodingAgent】【树导航】在同一会话内选择分支，支持取消、扩展摘要、默认摘要和目标标签。</summary>
    /// <param name="request">完整目标标识及摘要选项。</param>
    /// <param name="token">命令取消信号。</param>
    /// <returns>实际导航结果及待放回编辑器的用户输入。</returns>
    internal async Task<CodingAgentTreeNavigationResult> NavigateTreeAsync(JsonElement request, CancellationToken token)
    {
        if (_runner is not RuntimeCodingAgentRunner runner) throw new InvalidOperationException("The runner does not support tree navigation.");
        return await runner.NavigateSessionAsync(async navigationToken =>
        {
            var snapshot = Snapshot();
            var targetId = ReadReplacementString(request, "targetId") ?? throw new ArgumentException("Target entry ID is required.");
            if (targetId == snapshot.LeafId) return new(false);
            var target = snapshot.Entries.FirstOrDefault(entry => entry.Id == targetId) ?? throw new ArgumentException("Target session entry not found.");
            var oldBranch = GetSnapshotBranch(snapshot);
            var targetBranch = ReadReplacementBranch(snapshot.Entries, targetId);
            var oldIds = oldBranch.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            var ancestor = targetBranch.LastOrDefault(entry => oldIds.Contains(entry.Id))?.Id;
            var departing = oldBranch.SkipWhile(entry => ancestor is not null && entry.Id != ancestor).Skip(ancestor is null ? 0 : 1).ToArray();
            var summarize = request.TryGetProperty("summarize", out var wantsSummary) && wantsSummary.GetBoolean();
            var instructions = ReadReplacementString(request, "customInstructions");
            var replace = request.TryGetProperty("replaceInstructions", out var replacement) && replacement.GetBoolean();
            var label = ReadReplacementString(request, "label");
            var commands = _extensions?.SessionCommands;
            var before = JsonSerializer.SerializeToElement(new
            {
                type = "session_before_tree",
                preparation = new { targetId, oldLeafId = snapshot.LeafId, commonAncestorId = ancestor,
                    entriesToSummarize = departing.Select(entry => JsonSerializer.SerializeToElement(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry)).ToArray(),
                    userWantsSummary = summarize, customInstructions = instructions, replaceInstructions = replace, label }
            });
            if (commands is not null) before = await commands.PublishMutableSessionEventAsync(before, runner, navigationToken).ConfigureAwait(false);
            JsonElement? extensionSummary = null;
            if (before.TryGetProperty("handlerResult", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                if (result.TryGetProperty("cancel", out var cancel) && cancel.ValueKind == JsonValueKind.True) return new(true);
                if (summarize && result.TryGetProperty("summary", out var summaryValue) && summaryValue.ValueKind == JsonValueKind.Object) extensionSummary = summaryValue;
                if (result.TryGetProperty("customInstructions", out var changedInstructions)) instructions = changedInstructions.GetString();
                if (result.TryGetProperty("replaceInstructions", out var changedReplace)) replace = changedReplace.GetBoolean();
                if (result.TryGetProperty("label", out var changedLabel)) label = changedLabel.GetString();
            }
            // 1. 【CodingAgent】【离开分支】扩展摘要优先，默认摘要仅处理共同祖先之后的旧分支
            string? summary = null;
            JsonElement? details = null, usage = null;
            if (extensionSummary is { } provided)
            {
                summary = provided.GetProperty("summary").GetString();
                if (provided.TryGetProperty("details", out var data)) details = data.Clone();
                if (provided.TryGetProperty("usage", out var consumed)) usage = consumed.Clone();
            }
            else if (summarize && departing.Length > 0)
            {
                var messages = CodingAgentTreeSessionStore.ProjectBranch(departing, legacyMessages: true).Messages.Where(message => message is not SystemMessage).ToArray();
                var generated = await runner.SummarizeBranchAsync(messages, instructions, replace, navigationToken).ConfigureAwait(false);
                summary = generated.Summary;
                usage = generated.Usage;
                details = JsonSerializer.SerializeToElement(new { readFiles = generated.ReadFiles ?? [], modifiedFiles = generated.ModifiedFiles ?? [] });
            }
            navigationToken.ThrowIfCancellationRequested();
            var user = target.Type == "message" && target.Message?.Role == "user";
            var newLeaf = user || target.Type == "custom_message" ? target.ParentId : targetId;
            var editorText = user ? string.Concat(((UserMessage)CodingAgentSessionStore.ToMessage(target.Message!)!).Content.OfType<TextContent>().Select(text => text.Text))
                : target.Type == "custom_message" ? ReadNavigationContentText(target.Content) : null;
            CodingAgentTreeSessionEntry? summaryEntry = null;
            // 2. 【CodingAgent】【导航提交】保持会话标识，只有摘要或标签产生新条目，纯导航仅移动叶节点
            lock (_gate)
            {
                if (_tree is not null) _tree.Store.NavigateNativeLeaf(newLeaf);
                else _memoryLeafId = newLeaf;
                if (!string.IsNullOrEmpty(summary))
                {
                    summaryEntry = new() { Type = "branch_summary", Id = Guid.NewGuid().ToString("N"), ParentId = newLeaf,
                        Timestamp = DateTimeOffset.UtcNow, FromId = snapshot.LeafId ?? "root", Summary = summary,
                        Details = details, Usage = usage, FromHook = extensionSummary is not null };
                    AppendNavigationEntry(summaryEntry);
                    newLeaf = summaryEntry.Id;
                }
                if (!string.IsNullOrEmpty(label))
                {
                    var labelEntry = new CodingAgentTreeSessionEntry { Type = "label", Id = Guid.NewGuid().ToString("N"), ParentId = newLeaf,
                        Timestamp = DateTimeOffset.UtcNow, TargetId = summaryEntry?.Id ?? targetId, Label = label };
                    AppendNavigationEntry(labelEntry);
                }
                RefreshReplacementContext();
            }
            var completed = Snapshot();
            var serializedSummary = summaryEntry is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(summaryEntry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry);
            if (commands is not null) await commands.PublishMutableSessionEventAsync(JsonSerializer.SerializeToElement(new
            {
                type = "session_tree", oldLeafId = snapshot.LeafId, newLeafId = completed.LeafId, summaryEntry = serializedSummary,
                fromExtension = summaryEntry is null ? (bool?)null : extensionSummary is not null
            }), runner, navigationToken).ConfigureAwait(false);
            return new(false, editorText, serializedSummary);
        }, token).ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【导航条目】向当前持久化或内存树写入已准备的条目。</summary>
    /// <param name="entry">摘要或标签。</param>
    private void AppendNavigationEntry(CodingAgentTreeSessionEntry entry)
    {
        if (_tree is not null) _tree.Store.AppendNavigationEntry(entry);
        else AppendMemoryEntry(entry);
    }

    /// <summary>【CodingAgent】【编辑器恢复】提取自定义消息中的文本块，不包含图片占位。</summary>
    /// <param name="content">字符串或内容块数组。</param>
    /// <returns>原始拼接文本。</returns>
    private static string ReadNavigationContentText(JsonElement? content) => content is not { } value ? "" : value.ValueKind == JsonValueKind.String
        ? value.GetString()! : value.ValueKind == JsonValueKind.Array ? string.Concat(value.EnumerateArray()
            .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "text").Select(item => item.GetProperty("text").GetString())) : "";
}
