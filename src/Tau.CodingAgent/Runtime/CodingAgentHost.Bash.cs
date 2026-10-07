// 作者：xxx
using Tau.AgentCore.Harness;
using Tau.CodingAgent.Tools;
using Tau.Tui.Components;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentHost
{
    private readonly object _directBashGate = new();
    private Task? _directBashTask;
    private CancellationTokenSource? _directBashCancellation;
    internal ICodingAgentShellOperations? DirectBashOperations { get; set; }

    /// <summary>【CodingAgent】【直接命令输入】识别 ! 和 !!，启动独立任务并继续接收编辑操作；重复执行时恢复输入。</summary>
    /// <param name="input">已提交的完整输入。</param><param name="hostToken">宿主生命周期信号。</param>
    /// <returns>是否作为直接 Shell 输入处理。</returns>
    private bool TryStartDirectBash(string input, CancellationToken hostToken)
    {
        if (!input.StartsWith('!')) return false;
        var excluded = input.StartsWith("!!", StringComparison.Ordinal);
        var command = input[(excluded ? 2 : 1)..].Trim();
        if (command.Length == 0) return false;
        lock (_directBashGate)
        {
            if (_directBashTask is { IsCompleted: false } || _runner is RuntimeCodingAgentRunner { IsBashRunning: true })
            {
                WriteStatus("A bash command is already running. Press Esc to cancel it first.");
                _ui.SetDraft(input);
                return true;
            }
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            _directBashCancellation = cancellation;
            var session = (_runner as RuntimeCodingAgentRunner)?.BashContextGeneration;
            _directBashTask = Task.Run(() => RunDirectBashAsync(command, excluded, cancellation, session), CancellationToken.None);
        }
        return true;
    }

    /// <summary>【CodingAgent】【直接命令展示】流式更新 Bash 组件，完成后保留结果并记录是否排除模型上下文。</summary>
    /// <param name="command">原始命令。</param><param name="excluded">是否不进入模型上下文。</param><param name="cancellation">独立命令取消源。</param>
    /// <param name="sessionId">接收命令时的会话内容代次。</param>
    /// <returns>执行和显示收尾任务。</returns>
    private async Task RunDirectBashAsync(string command, bool excluded, CancellationTokenSource cancellation, long? sessionId)
    {
        var key = "user-bash-" + Guid.NewGuid().ToString("N");
        var component = new TuiBashExecution(command, excluded, theme: _bashExecutionTheme);
        var displayed = false;
        try
        {
            if (!IsCurrentBashSession(sessionId)) return;
            await using var hook = _runner is RuntimeCodingAgentRunner hookRunner && _extensionCommandStore is not null
                ? await _extensionCommandStore.EmitUserBashAsync(command, excluded, hookRunner, cancellation.Token).ConfigureAwait(false) : null;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentBashSession(sessionId)) return;
            // 1. 【CodingAgent】【命令接管】只有钩子成功放行后才显示运行组件，失败时禁止本地回退
            lock (_eventRenderGate)
            {
                component.SetExpanded(_toolOutputExpanded); component.SetExpandKeyHint(ToolOutputExpandKeyHint());
                _activeBashExecutions[key] = component; _ui.WriteToolComponent(component, key: key);
                displayed = true;
            }
            var progress = new DirectBashProgress(update =>
            {
                lock (_eventRenderGate)
                {
                    if (!IsCurrentBashSession(sessionId)) return;
                    component.AppendOutput(update.Text); _ui.WriteToolComponent(component, key: key);
                }
            });
            CodingAgentShellResult result;
            if (hook?.Result is { } completed && _runner is RuntimeCodingAgentRunner completedRunner)
            {
                result = completed;
                completedRunner.RecordBashResultForContext(command, result, excluded, sessionId);
            }
            else if (_runner is RuntimeCodingAgentRunner runtime)
                result = await runtime.ExecuteBashAsync(command, progress,
                    new(excluded, hook?.Operations ?? DirectBashOperations) { ContextGeneration = sessionId }, cancellation.Token).ConfigureAwait(false);
            else
            {
                var shell = new SystemCodingAgentShellRunner(options: () => new() { Operations = DirectBashOperations });
                result = await shell.ExecuteAsync(command, progress, cancellation.Token).ConfigureAwait(false);
                _runner.AppendMessage(new AgentBashExecutionMessage(command, result.Output, result.ExitCode, result.Cancelled,
                    result.Truncated, result.FullOutputPath, DateTimeOffset.UtcNow, excluded));
            }
            if (!IsCurrentBashSession(sessionId)) return;
            lock (_eventRenderGate)
            {
                component.SetOutput(result.Output); component.SetComplete(result.ExitCode, result.Cancelled, result.Truncated, result.FullOutputPath);
                _ui.WriteToolComponent(component, key: key);
            }
            if (!_runner.IsStreaming) PersistSession(refreshStatus: false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!IsCurrentBashSession(sessionId)) return;
            if (displayed) lock (_eventRenderGate) { component.SetComplete(null, true); _ui.WriteToolComponent(component, key: key); }
        }
        catch (Exception error)
        {
            if (!IsCurrentBashSession(sessionId)) return;
            if (displayed) lock (_eventRenderGate) { component.SetComplete(null, false); _ui.WriteToolComponent(component, key: key); }
            WriteRuntimeError($"Bash command failed: {error.Message}");
        }
        finally
        {
            lock (_directBashGate)
            {
                if (ReferenceEquals(_directBashCancellation, cancellation)) _directBashCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    /// <summary>【CodingAgent】【直接命令归属】拒绝旧会话的延迟输出和钩子返回结果。</summary>
    /// <param name="sessionId">提交命令时的会话内容代次。</param><returns>是否仍属于当前会话。</returns>
    private bool IsCurrentBashSession(long? sessionId) => _runner is not RuntimeCodingAgentRunner runtime || runtime.BashContextGeneration == sessionId;

    /// <summary>【CodingAgent】【直接命令中断】只取消直接命令，不清除草稿或取消宿主。</summary>
    /// <returns>是否有正在运行的命令。</returns>
    private bool AbortDirectBash()
    {
        var active = false;
        CancellationTokenSource? cancellation;
        lock (_directBashGate) cancellation = _directBashCancellation;
        if (cancellation is not null)
        {
            // 1. 【CodingAgent】【取消锁序】取消回调可以同步执行，必须在宿主状态锁外触发
            try { cancellation.Cancel(); active = true; }
            catch (ObjectDisposedException) { }
        }
        if (_runner is RuntimeCodingAgentRunner { IsBashRunning: true } runtime) { runtime.AbortBash(); active = true; }
        return active;
    }

    /// <summary>【CodingAgent】【直接命令收尾】宿主退出前取消并等待自己启动的命令，避免后台任务继续写入终端。</summary>
    /// <returns>任务及进程已经结束。</returns>
    private async Task StopDirectBashAsync()
    {
        AbortDirectBash();
        Task? pending;
        lock (_directBashGate) pending = _directBashTask;
        if (pending is not null) await pending.ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【直接命令进度】在接收线程同步更新组件，保持输出顺序。</summary><param name="receive">输出处理器。</param>
    private sealed class DirectBashProgress(Action<CodingAgentShellEvent> receive) : IProgress<CodingAgentShellEvent>
    {
        /// <summary>【CodingAgent】【输出分片】立即转交输出。</summary><param name="value">输出事件。</param>
        public void Report(CodingAgentShellEvent value) => receive(value);
    }
}
