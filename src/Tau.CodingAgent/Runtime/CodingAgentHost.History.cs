// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.Tui.Components;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【显示快照】保存当前压缩窗口的原始消息和全历史缓存损耗。</summary>
internal sealed record CodingAgentHistoryView(string? SessionId, long Revision,
    IReadOnlyList<CodingAgentProjectedSessionEntry> Entries, IReadOnlyDictionary<string, CodingAgentCacheMiss> CacheMisses);

public sealed partial class RuntimeCodingAgentRunner
{
    private long _historyRevision;

    /// <summary>【CodingAgent】【显示版本】仅在上下文替换时变化，正常追加无需重复绘制历史。</summary>
    internal long HistoryRevision => Interlocked.Read(ref _historyRevision);

    /// <summary>【CodingAgent】【显示快照】读取原始会话显示内容，上下文编辑不改写用户看到的历史。</summary>
    /// <param name="includeCacheMisses">是否计算费用提示。</param><returns>当前会话显示快照。</returns>
    internal CodingAgentHistoryView ReadHistoryView(bool includeCacheMisses)
    {
        if (_boundarySession is not null) return _boundarySession.ReadHistoryView(HistoryRevision, includeCacheMisses ? _modelCatalog.TryGetModel : null);
        var entries = Messages.Select((message, index) =>
        {
            var source = JsonSerializer.SerializeToElement(new CodingAgentTreeSessionEntry
            { Type = "message", Id = index.ToString(System.Globalization.CultureInfo.InvariantCulture), Message = CodingAgentSessionStore.FromMessage(message) },
                CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry);
            return new CodingAgentProjectedSessionEntry(source, [message]);
        }).ToArray();
        return new(SessionId, HistoryRevision, entries, includeCacheMisses
            ? CodingAgentCacheStats.CollectCacheMisses(ReadCacheEntries(), _modelCatalog.TryGetModel) : new Dictionary<string, CodingAgentCacheMiss>());
    }
}

internal sealed partial class CodingAgentExtensionSessionBridge
{
    /// <summary>【CodingAgent】【历史读取】在同一快照中选择当前分支和费用基线，保留独立用量条目。</summary>
    /// <param name="revision">运行器上下文版本。</param><param name="prices">价格查询；为空时跳过缓存统计。</param>
    /// <returns>原文显示快照。</returns>
    internal CodingAgentHistoryView ReadHistoryView(long revision, Func<string, string, Model?>? prices)
    {
        lock (_gate)
        {
            var snapshot = Snapshot();
            var projection = CodingAgentTreeSessionStore.ProjectBranch(GetSnapshotBranch(snapshot), applyContextEdits: false);
            return new(snapshot.Header.Id, revision, projection.Entries, prices is null
                ? new Dictionary<string, CodingAgentCacheMiss>() : CodingAgentCacheStats.CollectCacheMisses(snapshot.Entries, prices));
        }
    }
}

public sealed partial class CodingAgentHost
{
    private long _displayedHistoryRevision = -1;
    private string? _displayedHistorySession;
    private bool _displayedCacheNotices;
    private bool _displayedHideThinking;

    /// <summary>【CodingAgent】【历史重建】首次启动、上下文替换或显示偏好变更后重建会话，正常追加保持流式显示。</summary>
    /// <param name="initial">是否为首次启动；首次不清空欢迎区并恢复编辑器历史。</param>
    /// <returns>是否执行了历史重建。</returns>
    private bool RefreshSessionHistory(bool initial = false)
    {
        if (_runner is not RuntimeCodingAgentRunner runtime) return false;
        // 1. 【CodingAgent】【运行显示】活动回合结束后再重建，保留尚未持久化的输入和正在更新的工具组件
        if (!initial && runtime.IsStreaming) return false;
        var showCosts = _settingsStore?.GetShowCacheMissNotices() == true;
        if (!initial && _displayedHistoryRevision == runtime.HistoryRevision && _displayedHistorySession == runtime.SessionId &&
            _displayedCacheNotices == showCosts && _displayedHideThinking == _hideThinkingBlock) return false;
        var view = runtime.ReadHistoryView(showCosts);
        var populateHistory = initial || _displayedHistorySession != view.SessionId;
        if (!initial) _ui.ResetTranscript();
        _activeToolExecutions.Clear();
        _activeBashExecutions.Clear();
        _displayedCustomEntries.Clear();
        _hiddenThinkingLabelRendered = false;
        // 1. 【CodingAgent】【原文显示】沿压缩窗口保留的条目顺序重建，元数据和用量不会变为模型消息
        foreach (var entry in view.Entries)
        {
            if (entry.SourceEntry.TryGetProperty("type", out var entryType) && entryType.GetString() == "custom")
                RenderCustomEntry(entry.SourceEntry);
            foreach (var message in entry.Messages) RenderHistoricalMessage(message, populateHistory);
            if (!showCosts) continue;
            var source = entry.SourceEntry;
            if (source.TryGetProperty("type", out var type) && type.GetString() == "usage" &&
                source.TryGetProperty("kind", out var kind) && kind.GetString() == "cache_warm")
                _ui.WriteStatus(CodingAgentCacheWarmingFormatter.FormatUsage(source));
            else if (type.GetString() is "compaction" or "branch_summary" && source.TryGetProperty("usage", out _))
                _ui.WriteStatus(CodingAgentCacheWarmingFormatter.FormatSummaryUsage(source));
            // 2. 【CodingAgent】【缓存历史】失败或取消回复不显示提示，统计仍忠实保留提供方实际计费
            if (entry.Messages.OfType<AssistantMessage>().Any(message => message.StopReason is not (StopReason.Error or StopReason.Aborted)) &&
                source.TryGetProperty("id", out var id) && view.CacheMisses.TryGetValue(id.GetString()!, out var miss) &&
                CodingAgentCacheStats.FormatNotice(miss) is { } notice) _ui.WriteStatus(notice);
        }
        _displayedHistoryRevision = view.Revision;
        _displayedHistorySession = view.SessionId;
        _displayedCacheNotices = showCosts;
        _displayedHideThinking = _hideThinkingBlock;
        RefreshCompositionStatus();
        return true;
    }

    /// <summary>【CodingAgent】【历史消息】使用现有组件恢复消息、摘要和工具结果，排除系统声明及隐藏自定义消息。</summary>
    /// <param name="message">历史原始消息。</param><param name="populateHistory">是否把用户输入加入编辑器回看列表。</param>
    private void RenderHistoricalMessage(ChatMessage message, bool populateHistory)
    {
        switch (message)
        {
            case UserMessage user:
                var text = string.Join("\n", user.Content.Select(block => CodingAgentMessageDisplayFormatter.FormatContentBlock(block)));
                RenderDisplayedMessages(CodingAgentMessageDisplayFormatter.FormatUserMessage(text));
                if (populateHistory) _ui.RestoreInputHistory(string.Join("\n", user.Content.OfType<TextContent>().Select(block => block.Text)));
                break;
            case AssistantMessage assistant:
                foreach (var block in assistant.Content)
                {
                    if (block is TextContent answer) { _hiddenThinkingLabelRendered = false; _ui.WriteAssistantText(answer.Text, isStreaming: false); }
                    else if (block is ThinkingContent thinking) WriteAssistantThinking(thinking.Thinking, isStreaming: false);
                }
                _ui.CompleteAssistantTurn();
                foreach (var call in assistant.Content.OfType<ToolCallContent>())
                {
                    HandleToolStart(new(call.Id, call.Name, call.Arguments));
                    if (assistant.StopReason is StopReason.Aborted or StopReason.Error)
                        HandleToolEnd(new(call.Id, new ToolResult([new TextContent(assistant.StopReason == StopReason.Aborted
                            ? "Operation aborted" : assistant.ErrorMessage ?? "Error")], IsError: true), call.Name));
                }
                if (assistant.StopReason is StopReason.Aborted or StopReason.Error)
                    _ui.WriteRuntimeError(assistant.ErrorMessage ?? (assistant.StopReason == StopReason.Aborted ? "Operation aborted" : "Error"));
                break;
            case ToolResultMessage result when _activeToolExecutions.ContainsKey(result.ToolCallId) || _activeBashExecutions.ContainsKey(result.ToolCallId):
                HandleToolEnd(new(result.ToolCallId, new ToolResult(result.Content, result.IsError, result.Details)
                { Usage = result.Usage }, result.ToolName));
                break;
            case AgentCustomMessage { Display: true } custom:
                RenderDisplayedMessage(_extensionCommandStore?.TryRenderCustomMessage(custom, out var rendered) == true && rendered is not null
                    ? rendered : CodingAgentMessageDisplayFormatter.FormatCustomMessage(custom));
                break;
            case AgentCompactionSummaryMessage summary:
                RenderDisplayedMessage(CodingAgentMessageDisplayFormatter.FormatCompactionSummary(new(summary.Summary, 0, 0, summary.TokensBefore)));
                break;
            case AgentBranchSummaryMessage summary:
                RenderDisplayedMessage(CodingAgentMessageDisplayFormatter.FormatBranchSummary(new(summary.Summary, 0)));
                break;
            case AgentBashExecutionMessage bash:
                var component = new TuiBashExecution(bash.Command, bash.ExcludeFromContext, theme: _bashExecutionTheme);
                component.SetExpanded(_toolOutputExpanded);
                component.SetExpandKeyHint(ToolOutputExpandKeyHint());
                component.SetOutput(bash.Output);
                component.SetComplete(bash.ExitCode, bash.Cancelled, bash.Truncated, bash.FullOutputPath);
                _ui.WriteToolComponent(component);
                break;
        }
    }
}
