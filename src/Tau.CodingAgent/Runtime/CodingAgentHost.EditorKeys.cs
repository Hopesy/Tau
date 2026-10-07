// 作者：xxx
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentHost
{
    internal TimeProvider EditorKeyTimeProvider { get; set; } = TimeProvider.System;
    private long? _lastEditorClearTime;
    private long? _lastEditorEscapeTime;

    /// <summary>【CodingAgent】【清空输入】第一次清空草稿，500 毫秒内再次触发时关闭当前交互会话。</summary>
    private void HandleEditorClear()
    {
        var now = EditorKeyTimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (_lastEditorClearTime is { } previous && now - previous < 500)
        {
            RenderCommandResult(CodingAgentCommandResult.Exit("Goodbye!"));
            return;
        }
        _ui.SetDraft(string.Empty);
        _lastEditorClearTime = now;
    }

    /// <summary>【CodingAgent】【空闲中断】清除 bash 草稿，空编辑器双击按设置打开树或分支选择，普通草稿保持原样。</summary>
    /// <param name="token">取消信号。</param><returns>中断动作任务。</returns>
    private async Task HandleEditorInterruptAsync(CancellationToken token)
    {
        if (AbortDirectBash()) return;
        var draft = _ui.GetDraft();
        if (draft.TrimStart().StartsWith('!')) { _ui.SetDraft(string.Empty); return; }
        if (!string.IsNullOrWhiteSpace(draft)) return;
        var setting = _settingsStore?.Load().AdditionalSettings?.GetValueOrDefault("doubleEscapeAction");
        var action = setting is { ValueKind: JsonValueKind.String } value ? value.GetString() : "tree";
        if (action == "none") return;
        var now = EditorKeyTimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (_lastEditorEscapeTime is { } previous && now - previous < 500)
        {
            _lastEditorEscapeTime = null;
            var result = await _commandRouter.TryHandleAsync(action == "tree" ? "/tree --interactive" : "/fork", token).ConfigureAwait(false);
            RenderCommandResult(result);
        }
        else _lastEditorEscapeTime = now;
    }
}
