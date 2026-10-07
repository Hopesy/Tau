// 作者：xxx
using System.Net;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime.Mcp.OAuth;

namespace Tau.CodingAgent.Runtime.Mcp;

public sealed partial class CodingAgentMcpService
{
    private readonly Dictionary<string, SemaphoreSlim> _authActions = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _authTasks = [];

    /// <summary>【CodingAgent】【MCP 显式登录】先停止旧连接及刷新，登录后重建连接，配置变化时不恢复已被替换的旧服务器。</summary>
    /// <param name="name">服务器名称。</param><param name="prompt">宿主登录交互。</param><param name="token">取消登录。</param><returns>登录和重连任务。</returns>
    public Task SignInAsync(string name, ICodingAgentMcpSignInPrompt prompt, CancellationToken token = default) =>
        AuthenticationActionAsync(name, async (entry, challenge, cancellation) =>
        {
            var settings = await ResolveOAuthSettingsAsync(entry, cancellation).ConfigureAwait(false);
            var url = entry.Config["url"]!.GetValue<string>();
            await CodingAgentMcpOAuthSignIn.SignInAsync(url, new CodingAgentMcpOAuthCredentialStore(_agentDirectory).ForServer(name, url),
                settings, prompt, challenge, _options.HttpClient, cancellation).ConfigureAwait(false);
        }, token);

    /// <summary>【CodingAgent】【MCP 注销】排空旧刷新后删除本服务器凭据，保留目录并停留在待登录状态。</summary>
    /// <param name="name">服务器名称。</param><param name="token">取消。</param><returns>关闭旧连接并删除凭据的任务。</returns>
    public Task SignOutAsync(string name, CancellationToken token = default) =>
        AuthenticationActionAsync(name, async (entry, _, cancellation) =>
        {
            await new CodingAgentMcpOAuthCredentialStore(_agentDirectory).RemoveAsync(name, entry.Config["url"]!.GetValue<string>(), cancellation).ConfigureAwait(false);
        }, token, restartAfterAction: false);

    /// <summary>【CodingAgent】【MCP 认证操作跟踪】跟踪登录生命周期，服务关闭会取消并等待登录退出。</summary>
    /// <param name="name">服务器。</param><param name="action">认证修改。</param><param name="token">调用取消。</param><param name="requiresOAuth">是否为认证操作。</param>
    /// <param name="expected">可选预期连接，外部登录扫描使用它防止操作新配置。</param><param name="restartAfterAction">操作成功后是否立即重连。</param><returns>跟踪后的任务。</returns>
    private Task AuthenticationActionAsync(string name, Func<CodingAgentMcpServerEntry, CodingAgentMcpOAuthChallenge?, CancellationToken, Task> action, CancellationToken token, bool requiresOAuth = true, Connection? expected = null, bool restartAfterAction = true)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_authActions.TryGetValue(name, out var serial)) _authActions[name] = serial = new(1, 1);
            return TrackAuthenticationTask(AuthenticationCoreAsync(name, serial, action, token, _lifetime.Token, requiresOAuth, expected, restartAfterAction));
        }
    }

    /// <summary>【CodingAgent】【MCP 认证串行操作】同服务器登录和注销互斥，避免晚到的刷新覆盖用户操作。</summary>
    /// <param name="name">服务器。</param><param name="serial">服务器操作锁。</param><param name="action">认证修改。</param><param name="token">调用取消。</param><param name="lifetime">服务关闭信号。</param><param name="requiresOAuth">是否为认证操作。</param>
    /// <param name="expected">外部登录扫描时预期的旧连接。</param><param name="restartAfterAction">成功后是否立即重连。</param><returns>操作任务。</returns>
    private async Task AuthenticationCoreAsync(string name, SemaphoreSlim serial,
        Func<CodingAgentMcpServerEntry, CodingAgentMcpOAuthChallenge?, CancellationToken, Task> action, CancellationToken token, CancellationToken lifetime, bool requiresOAuth, Connection? expected, bool restartAfterAction)
    {
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        Connection? previous = null; Connection? replacement = null;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (expected is not null && !ReferenceEquals(_connections.GetValueOrDefault(name), expected)) return;
                previous = _connections.GetValueOrDefault(name) ?? throw new InvalidOperationException($"MCP server \"{name}\" was not found");
                if (!previous.Enabled) throw new InvalidOperationException("Enable this MCP server before changing authentication or reconnecting");
                if (requiresOAuth)
                {
                    if (previous.Entry.Config["url"] is null) throw new InvalidOperationException("MCP OAuth sign-in is only available for HTTP servers");
                    if (previous.Entry.Config["auth"] is JsonObject provider) throw new InvalidOperationException($"Use /login {provider["provider"]!.GetValue<string>()} to manage this MCP server's provider credentials");
                    if (!UsesOAuth(previous.Entry)) throw new InvalidOperationException("MCP servers with an Authorization header do not use MCP OAuth sign-in");
                }
            }
            // 1. 【MCP】【注销目录保留】关闭会清空旧连接目录，因此在关闭前捕获最后可用快照
            var catalog = (previous.Tools, previous.Instructions, previous.HasResources, previous.Resources, previous.ResourceTemplates);
            var completed = false;
            await previous.DisposeAsync().ConfigureAwait(false);
            try { await action(previous.Entry, previous.Challenge, linked.Token).ConfigureAwait(false); completed = true; }
            finally
            {
                lock (_gate)
                {
                    if (!_disposed && ReferenceEquals(_connections.GetValueOrDefault(name), previous))
                    {
                        replacement = new(this, previous.Entry);
                        _connections[name] = replacement;
                        if (completed && !restartAfterAction)
                            replacement.RestoreSignedOutCatalog(catalog.Tools, catalog.Instructions, catalog.HasResources, catalog.Resources, catalog.ResourceTemplates);
                        else replacement.Start();
                        Interlocked.Increment(ref _version);
                    }
                }
            }
            if (replacement is not null) await replacement.Initialization.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }

    private sealed partial class Connection
    {
        /// <summary>【MCP】【注销状态】保留旧目录和服务器说明，不发起匿名连接，外部新令牌仍可触发自动恢复。</summary>
        /// <param name="tools">工具快照。</param><param name="instructions">服务器说明。</param><param name="hasResources">资源能力。</param>
        /// <param name="resources">资源目录。</param><param name="templates">资源模板。</param>
        internal void RestoreSignedOutCatalog(IReadOnlyList<System.Text.Json.JsonElement> tools, string? instructions, bool hasResources,
            IReadOnlyList<System.Text.Json.JsonElement> resources, IReadOnlyList<System.Text.Json.JsonElement> templates)
        {
            Tools = tools; Instructions = instructions; HasResources = hasResources; Resources = resources; ResourceTemplates = templates;
            State = "needs-auth"; Error = null; TokensAtSignIn = null;
        }
    }

    /// <summary>【CodingAgent】【MCP 登录质询】读取最近一次权限和资源要求，供显式登录复用。</summary><param name="name">服务器名称。</param><returns>不可变质询或空值。</returns>
    public CodingAgentMcpOAuthChallenge? GetOAuthChallenge(string name)
    {
        lock (_gate) return _connections.GetValueOrDefault(name)?.Challenge;
    }

    /// <summary>【CodingAgent】【MCP OAuth 配置解析】在实际刷新或登录时解析客户端密钥，普通未认证连接无需预先执行命令。</summary>
    /// <param name="entry">服务器配置。</param><param name="token">取消。</param><returns>完整设置。</returns>
    private async Task<CodingAgentMcpOAuthSettings> ResolveOAuthSettingsAsync(CodingAgentMcpServerEntry entry, CancellationToken token)
    {
        var oauth = entry.Config["oauth"] as JsonObject;
        var secret = CodingAgentMcpOAuthJson.Text(oauth, "clientSecret");
        if (secret is not null) secret = await _resolver.ResolveOrThrowAsync(secret, $"MCP server \"{entry.Name}\" oauth.clientSecret", token: token).ConfigureAwait(false);
        return new()
        {
            ClientId = CodingAgentMcpOAuthJson.Text(oauth, "clientId"), ClientSecret = secret,
            CallbackUrl = CodingAgentMcpOAuthJson.Text(oauth, "callbackUrl"), CallbackPort = oauth?["callbackPort"]?.GetValue<int>(),
            Scope = CodingAgentMcpOAuthJson.Text(oauth, "scope"), ClientName = CodingAgentMcpOAuthJson.Text(oauth, "clientName"),
            ClientRegistration = CodingAgentMcpOAuthJson.Text(oauth, "clientRegistration"),
            AuthorizationServerMetadataUrl = CodingAgentMcpOAuthJson.Text(oauth, "authServerMetadataUrl") is { } url ? new(url) : null
        };
    }

    /// <summary>【CodingAgent】【MCP 认证分类】HTTP 认证拒绝和需要交互的 OAuth 失败都进入待登录状态。</summary><param name="error">连接或操作异常。</param><returns>是否需要登录。</returns>
    private static bool IsAuthenticationFailure(Exception error) => error is CodingAgentMcpOAuthAuthorizationRequiredException or
        CodingAgentMcpHttpException { Status: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };

    /// <summary>【CodingAgent】【MCP OAuth 选择】显式 Authorization 请求头或提供方认证优先，禁止旧 OAuth 令牌覆盖手动凭据。</summary>
    /// <param name="entry">服务器定义。</param><returns>是否使用独立 MCP OAuth。</returns>
    internal static bool UsesOAuth(CodingAgentMcpServerEntry entry) => entry.Config["url"] is not null && entry.Config["auth"] is null &&
        (entry.Config["headers"] is not JsonObject headers || !headers.Any(pair => pair.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)));
}
