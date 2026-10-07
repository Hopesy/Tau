// 作者：xxx
namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentRpcHost
{
    private readonly bool _useSessionBash;
    private readonly Dictionary<CancellationTokenSource, Task> _sessionBashTasks = [];

    /// <summary>【CodingAgent】【RPC 命令接收】每个请求独立执行和取消，允许多个命令并行，输出通过会话事件关联请求。</summary>
    /// <param name="id">请求标识。</param><param name="command">原始命令。</param><param name="excluded">是否排除模型上下文。</param>
    /// <param name="token">宿主取消信号。</param>
    private void StartSessionBash(string? id, string command, bool excluded, CancellationToken token)
    {
        var runner = (RuntimeCodingAgentRunner)_runner;
        var generation = runner.BashContextGeneration;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_gate)
            _sessionBashTasks[cancellation] = Task.Run(() => RunSessionBashAsync(runner, id, command, excluded, generation, cancellation), CancellationToken.None);
    }

    /// <summary>【CodingAgent】【RPC 命令执行】先等待扩展接管决策，再通过会话运行器执行或记录，失败时仅回复错误。</summary>
    /// <param name="runner">原生运行器。</param><param name="id">请求标识。</param><param name="command">原始命令。</param>
    /// <param name="excluded">是否排除模型上下文。</param><param name="generation">接收时的会话代次。</param><param name="cancellation">命令取消源。</param>
    /// <returns>结果及协议输出完成任务。</returns>
    private async Task RunSessionBashAsync(RuntimeCodingAgentRunner runner, string? id, string command, bool excluded,
        long generation, CancellationTokenSource cancellation)
    {
        try
        {
            // 1. 【CodingAgent】【扩展接管】未返回或拒绝时都不能意外降级成本地执行
            cancellation.Token.ThrowIfCancellationRequested();
            await using var hook = _extensionCommandStore is not null
                ? await _extensionCommandStore.EmitUserBashAsync(command, excluded, runner, cancellation.Token).ConfigureAwait(false) : null;
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != runner.BashContextGeneration) throw new InvalidOperationException("Bash session context has changed.");
            CodingAgentShellResult result;
            if (hook?.Result is { } completed)
            {
                result = completed;
                runner.RecordBashResultForContext(command, result, excluded, generation);
            }
            else result = await runner.ExecuteBashAsync(command,
                options: new(excluded, hook?.Operations, id) { ContextGeneration = generation }, cancellationToken: cancellation.Token).ConfigureAwait(false);
            // 2. 【CodingAgent】【命令提交】运行器已保存结果并排空输出，最终响应之后客户端即可读取会话记录
            await WriteSuccessAsync(id, "bash", result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await WriteSuccessAsync(id, "bash", new CodingAgentShellResult("", null, true, false), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await WriteErrorAsync(id, "bash", error.Message, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _sessionBashTasks.Remove(cancellation);
            cancellation.Dispose();
        }
    }

    /// <summary>【CodingAgent】【RPC 命令取消】取消全部宿主命令和会话直接命令，取消回调在状态锁外执行。</summary>
    private void AbortSessionBash()
    {
        CancellationTokenSource[] cancellations;
        lock (_gate) cancellations = _sessionBashTasks.Keys.ToArray();
        foreach (var cancellation in cancellations)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        if (_useSessionBash) ((RuntimeCodingAgentRunner)_runner).AbortBash();
    }

    /// <summary>【CodingAgent】【RPC 命令收尾】等待已接收命令完成，保持扩展运行时和输出订阅有效。</summary><returns>全部命令收尾任务。</returns>
    private Task WaitForSessionBashAsync()
    {
        lock (_gate) return Task.WhenAll(_sessionBashTasks.Values.ToArray());
    }
}
