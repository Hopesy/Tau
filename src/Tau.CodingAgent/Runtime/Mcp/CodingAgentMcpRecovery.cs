// 作者：xxx
using System.Net;

namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【CodingAgent】【MCP 请求恢复】在连接所有者内执行操作，使过期重连和只读重试采用一致规则。</summary>
public interface ICodingAgentMcpRequestExecutor
{
    /// <summary>【CodingAgent】【MCP 请求执行】调用当前连接，按操作性质决定是否允许重试。</summary>
    /// <typeparam name="T">协议结果类型。</typeparam><param name="request">使用当前客户端的操作。</param><param name="readOnly">是否为资源读取等只读操作。</param>
    /// <param name="token">取消信号。</param><returns>操作结果。</returns>
    Task<T> ExecuteAsync<T>(Func<CodingAgentMcpClient, CancellationToken, Task<T>> request, bool readOnly, CancellationToken token);
}

public sealed partial class CodingAgentMcpService
{
    private sealed partial class Connection : ICodingAgentMcpRequestExecutor
    {
        private readonly Dictionary<CodingAgentMcpClient, int> _requests = [];
        private readonly HashSet<CodingAgentMcpClient> _retired = [];
        private readonly List<Task> _retiring = [];
        private long _connectionGeneration;

        /// <summary>【CodingAgent】【MCP 连接重试】HTTP 瞬态连接错误按 250/1000 毫秒退避，stdio 和协议错误不重试。</summary>
        /// <returns>已完成初始化和工具发现的客户端。</returns>
        private async Task<CodingAgentMcpClient> ConnectAsync()
        {
            await Task.Yield();
            for (var attempt = 0; ; attempt++)
            {
                try { return await ConnectOnceAsync().ConfigureAwait(false); }
                catch (Exception error) when (Entry.Config.ContainsKey("url") && attempt < 2 && !_lifetime.IsCancellationRequested && IsTransient(error))
                {
                    lock (_owner._gate) { State = "connecting"; Error = error.Message; Interlocked.Increment(ref _owner._version); }
                    await Task.Delay(attempt == 0 ? 250 : 1000, _lifetime.Token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>【CodingAgent】【MCP 操作恢复】会话 404 只重试一次；瞬态错误仅重试只读操作，不重放可能已执行的工具。</summary>
        /// <typeparam name="T">结果类型。</typeparam><param name="request">操作。</param><param name="readOnly">只读标记。</param><param name="token">调用取消。</param><returns>操作结果。</returns>
        public async Task<T> ExecuteAsync<T>(Func<CodingAgentMcpClient, CancellationToken, Task<T>> request, bool readOnly, CancellationToken token)
        {
            CancellationToken lifetime;
            lock (_tasksGate)
            {
                if (_closed || !Enabled) throw new InvalidOperationException($"MCP server \"{Entry.Name}\" is closed or disabled");
                lifetime = _lifetime.Token;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
            var signal = linked.Token;
            for (var attempt = 0; ;)
            {
                signal.ThrowIfCancellationRequested();
                var client = await GetClientAsync(signal).ConfigureAwait(false);
                lock (_tasksGate)
                {
                    // 1. 【CodingAgent】【并发租用】连接若已被其他请求替换，尚未发送的请求直接采用新连接
                    if (_closed) throw new OperationCanceledException(signal);
                    if (!ReferenceEquals(client, _client)) continue;
                    _requests[client] = _requests.GetValueOrDefault(client) + 1;
                }
                try { return await request(client, signal).ConfigureAwait(false); }
                catch (Exception) when (signal.IsCancellationRequested)
                { signal.ThrowIfCancellationRequested(); throw; }
                catch (CodingAgentMcpHttpException error) when (error.SessionExpired && attempt == 0 && !signal.IsCancellationRequested)
                {
                    // 2. 【CodingAgent】【会话过期】旧连接保留到已发送请求完成，避免并发请求被提前关闭
                    RetireClient(client); attempt++;
                }
                catch (CodingAgentMcpHttpException error) when (readOnly && attempt == 0 && IsTransient(error) && !signal.IsCancellationRequested)
                { attempt++; await Task.Delay(250, signal).ConfigureAwait(false); }
                catch (Exception error) when (IsAuthenticationFailure(error))
                {
                    var generation = RetireClient(client);
                    if (generation is not null) await MarkSignInRequiredAsync(null, generation).ConfigureAwait(false);
                    var provider = Entry.Config["auth"]?["provider"]?.GetValue<string>();
                    throw new InvalidOperationException($"MCP server \"{Entry.Name}\" requires sign-in. Run {(provider is null ? "/mcp" : "/login " + provider)} to sign in.", error);
                }
                finally { await ReleaseClientAsync(client).ConfigureAwait(false); }
            }
        }

        /// <summary>【CodingAgent】【连接退役】只摘除实际失败的当前连接，旧请求不能摘除已经恢复的新连接。</summary>
        /// <param name="client">失败请求使用的客户端。</param><returns>摘除当前连接时的代次；过期请求或已关闭时为空。</returns>
        private long? RetireClient(CodingAgentMcpClient client)
        {
            lock (_tasksGate)
            {
                if (_closed) return null;
                var current = ReferenceEquals(_client, client);
                if (current) _client = null;
                _retired.Add(client);
                return current ? _connectionGeneration : null;
            }
        }

        /// <summary>【CodingAgent】【租用释放】最后一个请求结束后关闭已退役连接，并让宿主关闭流程等待清理。</summary>
        /// <param name="client">请求使用的连接。</param><returns>必要的连接清理任务。</returns>
        private async Task ReleaseClientAsync(CodingAgentMcpClient client)
        {
            TaskCompletionSource? completion = null;
            lock (_tasksGate)
            {
                if (_requests.GetValueOrDefault(client) > 1) _requests[client]--;
                else
                {
                    _requests.Remove(client);
                    if (_retired.Remove(client)) { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _retiring.Add(completion.Task); }
                }
            }
            if (completion is null) return;
            try { await client.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                completion.TrySetResult();
                lock (_tasksGate) _retiring.Remove(completion.Task);
            }
        }

        /// <summary>【CodingAgent】【瞬态判定】只识别网络失败和明确的服务器临时状态。</summary>
        /// <param name="error">原始异常。</param><returns>是否允许重连重试。</returns>
        private static bool IsTransient(Exception error) => error is HttpRequestException || error is CodingAgentMcpHttpException http &&
            ((int)http.Status is 408 or 429 || (int)http.Status >= 500 && (int)http.Status != 501);
    }
}
