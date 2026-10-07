// 作者：xxx
namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentCommandRouter
{
    /// <summary>【CodingAgent】【分支选择】从用户消息选择分支起点，排除选中的输入并恢复为可编辑草稿。</summary>
    /// <param name="token">取消信号。</param><returns>分支结果或选择取消状态。</returns>
    private async Task<CodingAgentCommandResult> SelectForkMessageAsync(CancellationToken token)
    {
        if (_treeSessionController is null) return CodingAgentCommandResult.Error("tree sessions are not enabled");
        if (_treeNavigator is null) return CodingAgentCommandResult.Error("interactive fork selector is not available in this mode");
        _treeSessionController.SyncFromRunner(_runner);
        var messages = _treeSessionController.GetUserMessagesForForking();
        if (messages.Count == 0) return CodingAgentCommandResult.Status("No user messages to fork from");
        // 1. 【CodingAgent】【候选隔离】只暴露用户消息，选中结果必须仍属于本次候选集合
        var items = messages.Select(message => new CodingAgentTreeViewItem(message.EntryId, message.Text, false, true,
            EntryType: "message", SearchText: message.Text, MessageRole: "user", NavigationDraftText: message.Text)).ToArray();
        var choice = await _treeNavigator(items, messages[^1].EntryId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (choice.SelectedEntryId is null) return CodingAgentCommandResult.Status("fork cancelled");
        var message = messages.FirstOrDefault(item => item.EntryId == choice.SelectedEntryId);
        if (message is null) return CodingAgentCommandResult.Error("selected fork message is no longer available");
        // 2. 【CodingAgent】【分支提交】原生运行器执行生命周期检查，成功后才替换输入草稿
        if (_runner is RuntimeCodingAgentRunner native)
        {
            var result = await native.ForkSessionAsync(message.EntryId, "before", token).ConfigureAwait(false);
            if (result.Cancelled) return CodingAgentCommandResult.Status("fork cancelled");
            _inputDraftSetter?.Invoke(result.SelectedText ?? message.Text);
        }
        else
        {
            var target = _treeSessionController.GetForkTarget(message.EntryId);
            if (target is null) return CodingAgentCommandResult.Error("selected fork message is no longer available");
            var fork = target.ParentEntryId is { } parent
                ? _treeSessionController.Store.CreateBranchedSession(parent)
                : _treeSessionController.Store.CreateNewSession(_treeSessionController.Path);
            var snapshot = _treeSessionController.Adopt(fork);
            _runner.RestoreSession(snapshot.ToFlatSnapshot());
            _inputDraftSetter?.Invoke(target.Text);
        }
        return CodingAgentCommandResult.Status($"forked session before {message.EntryId}: {_treeSessionController.Path}");
    }
}
