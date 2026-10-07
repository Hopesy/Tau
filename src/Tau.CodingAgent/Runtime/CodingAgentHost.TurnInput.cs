// 作者：xxx
using Tau.Tui.Abstractions;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentHost
{
    /// <summary>【CodingAgent】【流式输入】将空闲编辑器草稿交给回合输入源，并共享仅当前回合的取消信号。</summary>
    /// <param name="turnCancellation">本次运行的取消源，不取消宿主生命周期。</param>
    /// <param name="hostCancellation">宿主生命周期信号，模态操作在生成结束后仍可完成。</param>
    /// <returns>监听任务；没有输入源时返回空引用。</returns>
    private Task? StartTurnInputListener(CancellationTokenSource turnCancellation, CancellationToken hostCancellation)
    {
        if (_turnInputSource is null) return null;
        if (!ReferenceEquals(_turnInputSource.InputEditor, _ui.InputEditor) || _ui.InputEditor is null)
            _turnInputSource.SetDraft(_ui.GetDraft());
        var draftScope = _turnInputSource.InputEditor is { } editor ? _ui.UseDraftEditor(editor) : null;
        return Task.Run(() => ConsumeTurnInputsAsync(turnCancellation, hostCancellation, draftScope), CancellationToken.None);
    }

    /// <summary>【CodingAgent】【流式动作】接收队列消息和中断动作，退出监听时交回完整未发送草稿。</summary>
    /// <param name="turnCancellation">当前回合及输入监听的共同取消源。</param>
    /// <param name="hostCancellation">宿主取消信号，独立于回合自然结束。</param>
    /// <param name="draftScope">把应用草稿操作路由到流式编辑器的作用域。</param>
    /// <returns>监听与草稿交接任务。</returns>
    private async Task ConsumeTurnInputsAsync(CancellationTokenSource turnCancellation, CancellationToken hostCancellation, IDisposable? draftScope)
    {
        var source = _turnInputSource!;
        try
        {
            await foreach (var input in source.ReadInputsAsync(turnCancellation.Token).ConfigureAwait(false))
            {
                if (input.Kind == CodingAgentTurnInputKind.ApplicationAction)
                {
                    // 1. 【CodingAgent】【模态输入】模型选择和外部编辑器持有输入权，生成结束不丢弃正在确认的操作
                    var actionToken = input.Action is EditorAction.SelectModel or EditorAction.OpenExternalEditor or
                        EditorAction.NewSession or EditorAction.OpenSessionTree or EditorAction.ForkSession or EditorAction.ResumeSession
                        ? hostCancellation : turnCancellation.Token;
                    try { await TryHandleEditorActionAsync(input.Action, actionToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (turnCancellation.IsCancellationRequested) { throw; }
                    catch (Exception error) { WriteRuntimeError($"editor action failed: {error.Message}"); }
                    if (_shutdownRendered) { await turnCancellation.CancelAsync().ConfigureAwait(false); break; }
                    continue;
                }
                if (input.Kind is CodingAgentTurnInputKind.Interrupt or CodingAgentTurnInputKind.RestoreQueuedMessages)
                {
                    // 1. 【CodingAgent】【消息恢复】只取尚未消费的消息，保持 steering、follow-up、当前草稿的顺序
                    var queued = _runner.DrainQueuedMessages();
                    var messages = queued.Steering.Concat(queued.FollowUp).ToArray();
                    var draft = messages.Length == 0 ? input.Text : string.Join("\n\n",
                        messages.Append(input.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
                    source.SetDraft(draft);
                    _ui.SetDraft(draft);
                    if (input.Kind == CodingAgentTurnInputKind.Interrupt)
                    {
                        // 2. 【CodingAgent】【回合中断】先恢复草稿再通知运行器，保留宿主以便继续下一次输入
                        await turnCancellation.CancelAsync().ConfigureAwait(false);
                        break;
                    }
                    if (messages.Length > 0)
                        WriteStatus($"Restored {messages.Length} queued message{(messages.Length == 1 ? string.Empty : "s")} to editor");
                    continue;
                }

                var text = input.Text.Trim();
                if (text.Length == 0) continue;
                if (TryStartDirectBash(text, hostCancellation)) continue;
                if (input.Kind == CodingAgentTurnInputKind.FollowUp) _runner.FollowUp(text);
                else if (input.Kind == CodingAgentTurnInputKind.Steering) _runner.Steer(text);
            }
        }
        catch (OperationCanceledException) when (turnCancellation.IsCancellationRequested)
        {
            // 3. 【CodingAgent】【监听结束】正常完成和用户中断均通过回合信号结束读取
        }
        catch (Exception ex)
        {
            WriteRuntimeError($"turn input listener failed: {ex.Message}");
        }
        finally
        {
            // 4. 【CodingAgent】【草稿交接】读取结束后再访问编辑状态，避免与活动按键处理并发修改
            var sharedEditor = source.InputEditor is not null && ReferenceEquals(source.InputEditor, _ui.InputEditor);
            var cursor = source.InputEditor?.GetExpandedCursorIndex();
            string? draft;
            try { draft = sharedEditor ? null : source.TakeDraft(); }
            finally { draftScope?.Dispose(); }
            if (draft is not null) _ui.SetDraft(draft, cursor);
        }
    }
}
