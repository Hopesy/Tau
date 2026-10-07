// 作者：xxx
// 【MCP】【OAuth 移植】参考 pi 与 MCP TypeScript SDK，原始 MIT 许可见同目录 LICENSE.txt
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Globalization;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime.Mcp.OAuth;

/// <summary>【CodingAgent】【MCP OAuth 连接认证】复用存储令牌，提前刷新即将过期令牌，并将并发认证失败合并为一次刷新。</summary>
public sealed class CodingAgentMcpOAuthHttpAuth : ICodingAgentMcpHttpAuth, IAsyncDisposable
{
    private readonly string _serverUrl;
    private readonly ICodingAgentMcpOAuthServerStore _store;
    private readonly Func<CancellationToken, Task<CodingAgentMcpOAuthSettings>> _settings;
    private readonly Action<CodingAgentMcpOAuthChallenge>? _onChallenge;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Task? _refresh;
    private Task? _dispose;
    private bool _closed;

    /// <summary>【CodingAgent】【OAuth 连接认证创建】设置仅在刷新时解析，默认 HTTP 请求超时十五秒。</summary>
    /// <param name="serverUrl">MCP 地址。</param><param name="store">带刷新锁的服务器存储。</param><param name="settings">延迟解析设置。</param>
    /// <param name="onChallenge">保存授权质询供显式登录使用。</param><param name="httpClient">宿主管理的可选 HTTP 客户端。</param><param name="timeProvider">测试时钟。</param>
    public CodingAgentMcpOAuthHttpAuth(string serverUrl, ICodingAgentMcpOAuthServerStore store,
        Func<CancellationToken, Task<CodingAgentMcpOAuthSettings>> settings, Action<CodingAgentMcpOAuthChallenge>? onChallenge = null,
        HttpClient? httpClient = null, TimeProvider? timeProvider = null)
    {
        _serverUrl = CodingAgentMcpOAuthCredentialStore.NormalizeUrl(serverUrl); _store = store; _settings = settings; _onChallenge = onChallenge;
        _http = httpClient ?? TauHttpClientFactory.Create(); _ownsHttp = httpClient is null; _time = timeProvider ?? TimeProvider.System;
        if (_ownsHttp) _http.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>【CodingAgent】【OAuth 连接令牌】等待已有刷新，三十秒内过期且可刷新时先刷新；失败仍交由实际 401 决定。</summary><param name="token">只取消本请求等待。</param><returns>当前访问令牌。</returns>
    public async Task<string?> GetTokenAsync(CancellationToken token)
    {
        Task? pending;
        lock (_gate) { ObjectDisposedException.ThrowIf(_closed, this); pending = _refresh; }
        if (pending is not null) await IgnoreFailureAsync(pending, token).ConfigureAwait(false);
        var state = await LoadOwnAsync(token).ConfigureAwait(false);
        var current = CodingAgentMcpOAuthJson.Text(state?["tokens"] as JsonObject, "access_token");
        var expires = state?["tokensExpireAt"]?.GetValueKind() == JsonValueKind.Number ? double.Parse(state["tokensExpireAt"]!.ToJsonString(), CultureInfo.InvariantCulture) : double.PositiveInfinity;
        if (expires - 30_000 > _time.GetUtcNow().ToUnixTimeMilliseconds() || string.IsNullOrEmpty(CodingAgentMcpOAuthJson.Text(state?["tokens"] as JsonObject, "refresh_token"))) return current;
        await IgnoreFailureAsync(RefreshAsync(current, null), token).ConfigureAwait(false);
        return CodingAgentMcpOAuthJson.Text((await LoadOwnAsync(token).ConfigureAwait(false))?["tokens"] as JsonObject, "access_token");
    }

    /// <summary>【CodingAgent】【OAuth 连接质询】权限不足要求重新登录；普通 401 共享刷新，已被其他进程替换的令牌不再次轮换。</summary>
    /// <param name="response">未释放的认证响应。</param><param name="usedToken">被拒绝的令牌。</param><param name="token">当前请求取消。</param><returns>刷新完成任务。</returns>
    public async Task OnUnauthorizedAsync(HttpResponseMessage response, string? usedToken, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var header = response.Headers.TryGetValues("WWW-Authenticate", out var values) ? string.Join(", ", values) : null;
        var challenge = CodingAgentMcpOAuthDiscovery.ParseChallenge(header);
        _onChallenge?.Invoke(challenge);
        if (challenge.Error == "insufficient_scope") throw new CodingAgentMcpOAuthAuthorizationRequiredException();
        await RefreshAsync(usedToken, challenge).WaitAsync(token).ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【OAuth 共享刷新】同一认证处理器只发布一个运行任务，完成后的新质询可以再次刷新。</summary><param name="staleToken">旧令牌。</param><param name="challenge">当前质询。</param><returns>共享任务。</returns>
    private Task RefreshAsync(string? staleToken, CodingAgentMcpOAuthChallenge? challenge)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_refresh is null || _refresh.IsCompleted) _refresh = RefreshCoreAsync(staleToken, challenge);
            return _refresh;
        }
    }

    /// <summary>【CodingAgent】【OAuth 刷新事务】跨进程锁覆盖读取、网络刷新和保存，调用方取消不会丢失已轮换令牌。</summary><param name="staleToken">旧令牌。</param><param name="challenge">当前质询。</param><returns>刷新任务。</returns>
    private async Task RefreshCoreAsync(string? staleToken, CodingAgentMcpOAuthChallenge? challenge)
    {
        await Task.Yield();
        await _store.WithRefreshLockAsync(async cancellation =>
        {
            // 1. 【MCP】【刷新去重】取得锁后再次读取，保留其他请求或进程刚保存的凭据
            var state = await LoadOwnAsync(cancellation).ConfigureAwait(false);
            var tokens = state?["tokens"] as JsonObject;
            if (CodingAgentMcpOAuthJson.Text(tokens, "access_token") != staleToken) return false;
            if (string.IsNullOrEmpty(CodingAgentMcpOAuthJson.Text(tokens, "refresh_token"))) throw new CodingAgentMcpOAuthAuthorizationRequiredException();
            var settings = await _settings(cancellation).ConfigureAwait(false);
            var redirect = settings.Callback().FixedUrl ?? CodingAgentMcpOAuthJson.List(state?["clientInformation"] as JsonObject, "redirect_uris").FirstOrDefault() ?? "http://127.0.0.1/callback";
            var provider = settings.CreateProvider(_serverUrl, _store, redirect, (_, _) => Task.CompletedTask, _time);
            // 2. 【MCP】【后台刷新】需要交互时只报告状态，关闭会等待令牌保存完成
            var result = await new CodingAgentMcpOAuthFlow(_http).AuthorizeAsync(provider, new()
            {
                ServerUrl = new(_serverUrl), ResourceMetadataUrl = challenge?.ResourceMetadataUrl,
                AuthorizationServerMetadataUrl = settings.AuthorizationServerMetadataUrl, Scope = challenge?.Scope
            }, cancellation).ConfigureAwait(false);
            if (result == CodingAgentMcpOAuthFlowResult.Redirect) throw new CodingAgentMcpOAuthAuthorizationRequiredException();
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【OAuth 状态身份】忽略存储中其他服务器的凭据。</summary><param name="token">取消。</param><returns>本服务器状态或空值。</returns>
    private async Task<JsonObject?> LoadOwnAsync(CancellationToken token)
    {
        var state = await _store.LoadAsync(token).ConfigureAwait(false);
        return CodingAgentMcpOAuthJson.Text(state, "serverUrl") == _serverUrl ? state : null;
    }
    /// <summary>【CodingAgent】【OAuth 刷新失败降级】保留调用方取消，其他刷新失败交给后续认证响应处理。</summary><param name="task">共享任务。</param><param name="token">等待取消。</param><returns>等待任务。</returns>
    private static async Task IgnoreFailureAsync(Task task, CancellationToken token)
    {
        try { await task.WaitAsync(token).ConfigureAwait(false); }
        catch (Exception) when (!token.IsCancellationRequested) { }
    }
    /// <summary>【CodingAgent】【OAuth 刷新排空】等待已启动刷新及持久化完成，不向关闭流程传播认证失败。</summary><returns>排空任务。</returns>
    public Task SettledAsync()
    {
        Task? pending;
        lock (_gate) pending = _refresh;
        return pending is null ? Task.CompletedTask : IgnoreFailureAsync(pending, CancellationToken.None);
    }
    /// <summary>【CodingAgent】【OAuth 认证关闭】禁止新刷新，复用同一个关闭任务，并在保存轮换令牌后释放自有客户端。</summary><returns>关闭任务。</returns>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _closed = true;
            return new(_dispose ??= DisposeCoreAsync(_refresh));
        }
    }
    /// <summary>【CodingAgent】【OAuth 资源释放】完成正在运行的刷新，注入客户端仍由调用方管理。</summary><param name="refresh">关闭前捕获的刷新。</param><returns>释放任务。</returns>
    private async Task DisposeCoreAsync(Task? refresh)
    {
        if (refresh is not null) await IgnoreFailureAsync(refresh, CancellationToken.None).ConfigureAwait(false);
        if (_ownsHttp) _http.Dispose();
    }
}
