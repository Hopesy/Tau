// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime.Mcp.OAuth;

namespace Tau.CodingAgent.Runtime.Mcp;

public sealed partial class CodingAgentMcpService
{
    /// <summary>【CodingAgent】【MCP 外部登录】在回合边界检查待登录服务器，仅在持久化令牌改变后重新连接。</summary>
    /// <param name="token">本次扫描取消信号。</param><returns>扫描及必要重连完成任务。</returns>
    public Task ReconnectSignedInServersAsync(CancellationToken token = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return TrackAuthenticationTask(ReconnectSignedInCoreAsync(token, _lifetime.Token));
        }
    }

    /// <summary>【CodingAgent】【MCP 登录扫描】并行观察独立账号，服务关闭同时取消扫描和重连。</summary>
    /// <param name="token">调用取消。</param><param name="lifetime">服务生命周期。</param><returns>扫描任务。</returns>
    private async Task ReconnectSignedInCoreAsync(CancellationToken token, CancellationToken lifetime)
    {
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        linked.Token.ThrowIfCancellationRequested();
        Connection[] waiting;
        lock (_gate) waiting = _connections.Values.Where(connection => connection.Enabled && connection.State == "needs-auth" && UsesOAuth(connection.Entry)).ToArray();
        await Task.WhenAll(waiting.Select(connection => ReconnectIfTokensChangedAsync(connection, linked.Token))).ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【MCP 登录恢复】仅恢复观察到的同一连接，排队期间配置被替换时不操作新服务器。</summary>
    /// <param name="connection">待登录连接快照。</param><param name="token">扫描取消。</param><returns>必要的恢复任务。</returns>
    private async Task ReconnectIfTokensChangedAsync(Connection connection, CancellationToken token)
    {
        JsonObject? tokens;
        try { tokens = await ReadSignInTokensAsync(connection.Entry, token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or TimeoutException) { return; }
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_connections.GetValueOrDefault(connection.Entry.Name), connection) ||
                connection.State != "needs-auth" || JsonNode.DeepEquals(tokens, connection.TokensAtSignIn)) return;
        }
        token.ThrowIfCancellationRequested();
        try
        {
            await AuthenticationActionAsync(connection.Entry.Name, (_, _, _) => Task.CompletedTask, token,
                requiresOAuth: false, expected: connection).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { token.ThrowIfCancellationRequested(); }
    }

    /// <summary>【CodingAgent】【MCP 登录令牌读取】按名称和 URL 读取令牌，不因客户端注册或发现缓存变化重连。</summary>
    /// <param name="entry">服务器身份。</param><param name="token">读取取消。</param><returns>令牌副本或空值。</returns>
    private Task<JsonObject?> ReadSignInTokensAsync(CodingAgentMcpServerEntry entry, CancellationToken token) =>
        UsesOAuth(entry) ? new CodingAgentMcpOAuthCredentialStore(_agentDirectory).TokensAsync(entry.Name, entry.Config["url"]!.GetValue<string>(), token) :
        Task.FromResult<JsonObject?>(null);

    /// <summary>【CodingAgent】【MCP 认证任务登记】调用方持有服务锁，关闭时等待此前所有认证任务排空。</summary>
    /// <param name="task">认证或扫描任务。</param><returns>同一任务。</returns>
    private Task TrackAuthenticationTask(Task task)
    {
        _authTasks.Add(task);
        _ = task.ContinueWith(completed => { lock (_gate) _authTasks.Remove(completed); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private sealed partial class Connection
    {
        internal JsonObject? TokensAtSignIn { get; private set; }

        /// <summary>【CodingAgent】【MCP 认证失败快照】记录进入待登录状态时的令牌；存储不可读不覆盖原连接错误。</summary>
        /// <param name="error">原认证错误，普通工具认证失败可为空。</param>
        /// <param name="generation">运行期失败时摘除的连接代次，初始化失败不需要额外校验。</param><returns>状态更新任务。</returns>
        private async Task MarkSignInRequiredAsync(string? error, long? generation = null)
        {
            lock (_tasksGate)
                if (_closed || generation is { } expected && (_connectionGeneration != expected || _client is not null)) return;
            JsonObject? tokens = null;
            try { tokens = await _owner.ReadSignInTokensAsync(Entry, _lifetime.Token).ConfigureAwait(false); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or TimeoutException) { }
            lock (_owner._gate)
            {
                // 1. 【MCP】【认证快照发布】凭据读取可能等待文件锁，写回时重新验证连接代次
                lock (_tasksGate)
                {
                    if (_closed || _lifetime.IsCancellationRequested ||
                        generation is { } expected && (_connectionGeneration != expected || _client is not null)) return;
                    TokensAtSignIn = tokens; State = "needs-auth"; Error = error;
                    Interlocked.Increment(ref _owner._version);
                }
            }
        }
    }
}
