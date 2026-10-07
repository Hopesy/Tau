// 作者：xxx
namespace Tau.Tui.Runtime;

public sealed partial class InteractiveConsoleSession
{
    private InteractiveInputEditor? _draftEditorOverride;
    private InteractiveInputEditor? DraftEditor => _draftEditorOverride ?? _editor;

    /// <summary>【Tui】【主编辑器】提供共享输入状态的主编辑器，只能在输入循环暂停点交接读取权。</summary>
    public InteractiveInputEditor? InputEditor => _editor;

    /// <summary>【Tui】【活动草稿】在另一个输入循环持有终端时，把应用层草稿操作路由到其编辑器。</summary>
    /// <param name="editor">暂时接管草稿的编辑器，调用方需在输入动作暂停点修改其状态。</param>
    /// <returns>释放后恢复原草稿编辑器的作用域。</returns>
    public IDisposable UseDraftEditor(InteractiveInputEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        lock (_stateSync)
        {
            var previous = _draftEditorOverride;
            _draftEditorOverride = editor;
            return new DraftEditorScope(this, previous);
        }
    }

    /// <summary>【Tui】【草稿作用域】恢复此前的活动编辑器，多次释放不改变后续作用域。</summary>
    /// <param name="owner">会话。</param><param name="previous">之前的覆盖。</param>
    private sealed class DraftEditorScope(InteractiveConsoleSession owner, InteractiveInputEditor? previous) : IDisposable
    {
        private InteractiveConsoleSession? _owner = owner;
        /// <summary>【Tui】【恢复草稿路由】在输入监听结束后恢复原编辑器。</summary>
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._stateSync) owner._draftEditorOverride = previous;
        }
    }
}
