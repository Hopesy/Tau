// 作者：xxx
// 【MCP】【OAuth 移植】参考 pi 与 MCP TypeScript SDK，原始 MIT 许可见同目录 LICENSE.txt
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime.Mcp.OAuth;

/// <summary>【CodingAgent】【MCP OAuth 状态】保存客户端注册、令牌、PKCE 验证器及服务发现信息。</summary>
public interface ICodingAgentMcpOAuthStateStore
{
    /// <summary>【CodingAgent】【OAuth 状态读取】读取独立状态副本。</summary><param name="token">取消信号。</param><returns>状态或空值。</returns>
    Task<JsonObject?> LoadAsync(CancellationToken token = default);
    /// <summary>【CodingAgent】【OAuth 状态写入】保存独立状态副本。</summary><param name="state">完整状态。</param><param name="token">取消信号。</param><returns>写入任务。</returns>
    Task SaveAsync(JsonObject state, CancellationToken token = default);
}

/// <summary>【CodingAgent】【MCP OAuth 刷新锁】独立服务器的令牌刷新需在读改写期间保持排他。</summary>
public interface ICodingAgentMcpOAuthServerStore : ICodingAgentMcpOAuthStateStore
{
    /// <summary>【CodingAgent】【OAuth 刷新事务】等待其他进程释放该服务器的刷新锁。</summary>
    /// <typeparam name="T">操作结果。</typeparam><param name="operation">持锁执行的异步操作。</param><param name="token">等待取消。</param><returns>操作结果。</returns>
    Task<T> WithRefreshLockAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token = default);
}

/// <summary>【CodingAgent】【MCP OAuth 内存状态】测试及不持久化宿主使用的副本存储。</summary>
public sealed class CodingAgentMcpOAuthMemoryStore : ICodingAgentMcpOAuthServerStore
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private JsonObject? _state;
    /// <summary>【CodingAgent】【内存读取】返回独立副本，调用方修改不污染存储。</summary><param name="token">取消。</param><returns>状态。</returns>
    public Task<JsonObject?> LoadAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); lock (_gate) return Task.FromResult(_state?.DeepClone().AsObject()); }
    /// <summary>【CodingAgent】【内存保存】复制状态，避免后续修改影响已保存数据。</summary><param name="state">状态。</param><param name="token">取消。</param><returns>完成任务。</returns>
    public Task SaveAsync(JsonObject state, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); lock (_gate) _state = state.DeepClone().AsObject(); return Task.CompletedTask; }
    /// <summary>【CodingAgent】【内存刷新】同一存储的刷新串行执行。</summary><typeparam name="T">结果。</typeparam><param name="operation">操作。</param><param name="token">取消。</param><returns>结果。</returns>
    public async Task<T> WithRefreshLockAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token = default)
    { await _refresh.WaitAsync(token).ConfigureAwait(false); try { return await operation(token).ConfigureAwait(false); } finally { _refresh.Release(); } }
}

/// <summary>【CodingAgent】【MCP OAuth 凭据文件】按服务器名和规范 URL 隔离账号，所有修改在文件锁内重新读取并原子替换。</summary>
public sealed class CodingAgentMcpOAuthCredentialStore
{
    private readonly string _directory;
    public string Path { get; }

    /// <summary>【CodingAgent】【OAuth 存储创建】指定用户级代理目录，不提前创建凭据文件。</summary><param name="agentDirectory">用户代理目录。</param>
    public CodingAgentMcpOAuthCredentialStore(string agentDirectory)
    { _directory = System.IO.Path.GetFullPath(agentDirectory); Path = System.IO.Path.Combine(_directory, "mcp-auth.json"); }

    /// <summary>【CodingAgent】【服务器存储】同 URL 的不同名称保持独立，首次读取的服务器接管旧 URL 键。</summary>
    /// <param name="name">服务器配置名称。</param><param name="serverUrl">服务器 URL。</param><returns>绑定服务器的存储。</returns>
    public ICodingAgentMcpOAuthServerStore ForServer(string name, string serverUrl)
    {
        var url = NormalizeUrl(serverUrl);
        return new ServerStore(this, CodingAgentMcpServers.Namespace(name) + "|" + url, url);
    }

    /// <summary>【CodingAgent】【令牌观察】读取令牌副本，用于发现其他进程的登录，不接管旧键。</summary>
    /// <param name="name">服务器名。</param><param name="serverUrl">地址。</param><param name="token">取消。</param><returns>令牌对象或空值。</returns>
    public Task<JsonObject?> TokensAsync(string name, string serverUrl, CancellationToken token = default)
    {
        var url = NormalizeUrl(serverUrl); var key = CodingAgentMcpServers.Namespace(name) + "|" + url;
        return TransactAsync(states => (((states[key] ?? states[url]) as JsonObject)?["tokens"]?.DeepClone() as JsonObject, false), token);
    }

    /// <summary>【CodingAgent】【OAuth 注销】移除该服务器自己的状态，旧格式尚未迁移时移除旧 URL 键。</summary>
    /// <param name="name">服务器名。</param><param name="serverUrl">地址。</param><param name="token">取消。</param><returns>是否删除了状态。</returns>
    public Task<bool> RemoveAsync(string name, string serverUrl, CancellationToken token = default)
    {
        var url = NormalizeUrl(serverUrl); var key = CodingAgentMcpServers.Namespace(name) + "|" + url;
        return TransactAsync(states => { var removed = states.Remove(states.ContainsKey(key) ? key : url); return (removed, removed); }, token);
    }

    /// <summary>【CodingAgent】【凭据事务】持文件锁读取全量对象，只在发生变更时写回，保留其他服务器及未知字段。</summary>
    /// <typeparam name="T">返回类型。</typeparam><param name="operation">同步读改写。</param><param name="token">取消。</param><returns>操作结果。</returns>
    private async Task<T> TransactAsync<T>(Func<JsonObject, (T Result, bool Changed)> operation, CancellationToken token)
    {
        await using var lease = await AcquireLockAsync(Path + ".tau-lock", token).ConfigureAwait(false);
        JsonObject states;
        try
        {
            var text = await File.ReadAllTextAsync(Path, token).ConfigureAwait(false);
            states = string.IsNullOrWhiteSpace(text) ? new() : JsonNode.Parse(text) as JsonObject ?? new();
        }
        catch (FileNotFoundException) { states = new(); }
        var result = operation(states);
        if (result.Changed) await WriteAsync(states, token).ConfigureAwait(false);
        return result.Result;
    }

    /// <summary>【CodingAgent】【私有文件写入】使用同目录临时文件，写入后原子替换；取消或失败时清理临时文件。</summary>
    /// <param name="states">完整状态集合。</param><param name="token">取消。</param><returns>提交任务。</returns>
    private async Task WriteAsync(JsonObject states, CancellationToken token)
    {
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, options))
            {
                var content = Encoding.UTF8.GetBytes(states.ToJsonString(new() { WriteIndented = true }) + "\n");
                await file.WriteAsync(content, token).ConfigureAwait(false); await file.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>【CodingAgent】【跨进程锁】由操作系统释放崩溃进程的独占句柄，等待最多 25 秒且支持取消。</summary>
    /// <param name="path">专用锁文件。</param><param name="token">等待取消。</param><returns>释放即解锁的句柄。</returns>
    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var directory = System.IO.Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None, BufferSize = 1, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                return new FileStream(path, options);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11)
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(25)) throw new TimeoutException("Timed out waiting for MCP OAuth credential lock.", error);
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>【CodingAgent】【OAuth URL 身份】规范主机、默认端口及国际域名，禁止非 HTTP 地址用于凭据键。</summary>
    /// <param name="url">服务器地址。</param><returns>规范绝对地址。</returns>
    internal static string NormalizeUrl(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (uri.Scheme is not ("https" or "http")) throw new ArgumentException("MCP OAuth server URL must use HTTP or HTTPS.", nameof(url));
        return new UriBuilder(uri) { Host = uri.IdnHost }.Uri.AbsoluteUri;
    }

    /// <summary>【CodingAgent】【单服务器存储】文件事务和独立刷新锁共用同一个服务器身份。</summary>
    /// <param name="owner">凭据文件。</param><param name="key">服务器键。</param><param name="legacyKey">旧 URL 键。</param>
    private sealed class ServerStore(CodingAgentMcpOAuthCredentialStore owner, string key, string legacyKey) : ICodingAgentMcpOAuthServerStore
    {
        /// <summary>【CodingAgent】【旧键接管】锁内迁移旧状态，保证同 URL 的其他账号不会再次接管。</summary><param name="token">取消。</param><returns>独立状态。</returns>
        public Task<JsonObject?> LoadAsync(CancellationToken token = default) => owner.TransactAsync(states =>
        {
            var migrated = states[key] is null && states[legacyKey] is not null;
            if (migrated) { states[key] = states[legacyKey]!.DeepClone(); states.Remove(legacyKey); }
            return (states[key]?.DeepClone() as JsonObject, migrated);
        }, token);
        /// <summary>【CodingAgent】【服务器保存】保留文件中的其他账号，调用方对象不被收养。</summary><param name="state">状态。</param><param name="token">取消。</param><returns>写入任务。</returns>
        public Task SaveAsync(JsonObject state, CancellationToken token = default) => owner.TransactAsync(states => { states[key] = state.DeepClone(); return (true, true); }, token);
        /// <summary>【CodingAgent】【服务器刷新锁】同身份共享排他锁，不同服务器可以并行刷新。</summary><typeparam name="T">结果。</typeparam><param name="operation">操作。</param><param name="token">取消。</param><returns>操作结果。</returns>
        public async Task<T> WithRefreshLockAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token = default)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            await using var lease = await AcquireLockAsync(System.IO.Path.Combine(owner._directory, "mcp-auth-refresh-" + hash + ".tau-lock"), token).ConfigureAwait(false);
            return await operation(token).ConfigureAwait(false);
        }
    }
}
