// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime.Mcp.OAuth;

namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【CodingAgent】【MCP 服务选项】控制启动等待以及宿主提供的传输和认证。</summary>
public sealed record CodingAgentMcpServiceOptions
{
    public TimeSpan StartupWait { get; init; } = TimeSpan.FromSeconds(10);
    public Func<CodingAgentMcpServerEntry, string, CancellationToken, Task<ICodingAgentMcpTransport>>? TransportFactory { get; init; }
    public Func<CodingAgentMcpServerEntry, ICodingAgentMcpHttpAuth?>? AuthFactory { get; init; }
    /// <summary>【CodingAgent】【MCP 认证来源】读取 auth.provider 的会话令牌，宿主认证工厂优先。</summary>
    public Func<string, CancellationToken, Task<string?>>? ProviderToken { get; init; }
    /// <summary>【CodingAgent】【MCP HTTP 客户端】可选宿主 HTTP 客户端，覆盖 MCP 与 OAuth 请求，生命周期由宿主管理。</summary>
    public HttpClient? HttpClient { get; init; }
    /// <summary>【CodingAgent】【MCP 配置来源】可选宿主配置快照，用于独立管理命令隔离不相关服务器。</summary>
    public Func<CodingAgentMcpConfig>? ConfigurationLoader { get; init; }
    /// <summary>【MCP】【日志位置】服务器通知日志位置，默认使用 agent 目录下的 mcp.log。</summary>
    public string? LogPath { get; init; }
}

/// <summary>【CodingAgent】【MCP 状态】不包含配置凭据的服务器状态快照。</summary>
public sealed record CodingAgentMcpServerStatus(string Name, string State, int ToolCount, string? Error)
{
    public int ResourceCount { get; init; }
    public int ResourceTemplateCount { get; init; }
}

/// <summary>【CodingAgent】【MCP 会话服务】合并文件和扩展配置，管理连接、动态目录与会话关闭。</summary>
public sealed partial class CodingAgentMcpService : IAsyncDisposable
{
    private readonly string _agentDirectory, _cwd;
    private readonly Func<bool> _projectTrusted;
    private readonly CodingAgentMcpServerRegistry _registry;
    private readonly CodingAgentMcpServiceOptions _options;
    private readonly CodingAgentConfigValueResolver _resolver;
    private readonly CodingAgentMcpServerLog _serverLog;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private Task _reload = Task.CompletedTask;
    private Task? _disposeTask;
    private IReadOnlyList<string> _errors = [];
    private long _version;
    private bool _disposed;
    private int _waitedForDirectServers;
    public long Version => Interlocked.Read(ref _version);
    public bool AutoEnableCodemode { get; private set; } = true;
    public IReadOnlyList<string> ConfigurationErrors { get { lock (_gate) return _errors.ToArray(); } }

    /// <summary>【CodingAgent】【MCP 服务创建】订阅扩展配置变更，实际连接由 ReloadAsync 启动。</summary>
    /// <param name="agentDirectory">全局配置目录。</param><param name="cwd">当前会话目录。</param><param name="projectTrusted">项目可信状态读取器。</param>
    /// <param name="registry">扩展注册表。</param><param name="options">可选传输与等待参数。</param>
    public CodingAgentMcpService(string agentDirectory, string cwd, Func<bool> projectTrusted, CodingAgentMcpServerRegistry registry, CodingAgentMcpServiceOptions? options = null)
    {
        _agentDirectory = Path.GetFullPath(agentDirectory); _cwd = Path.GetFullPath(cwd); _projectTrusted = projectTrusted;
        _registry = registry; _options = options ?? new(); _resolver = new(_cwd);
        _serverLog = new(_options.LogPath ?? Path.Combine(_agentDirectory, "mcp.log"));
        if (_options.StartupWait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        registry.Changed += RegistryChanged;
    }

    /// <summary>【CodingAgent】【MCP 配置刷新】串行重读配置，保留未变化的连接，启动新增项并关闭移除项。</summary>
    /// <param name="token">等待取消信号，不中止共享刷新。</param><returns>定义合并完成的任务。</returns>
    public Task ReloadAsync(CancellationToken token = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _reload = ReloadAfterAsync(_reload);
            return _reload.WaitAsync(token);
        }
    }

    /// <summary>【CodingAgent】【MCP 注册变更】仅排队后台刷新，避免在 Node 同步桥接回调中等待连接。</summary>
    /// <param name="entries">注册表快照。</param>
    private void RegistryChanged(IReadOnlyList<CodingAgentMcpServerEntry> entries)
    { lock (_gate) { if (!_disposed) _reload = ReloadAfterAsync(_reload); } }

    /// <summary>【CodingAgent】【MCP 合并】文件定义优先于扩展；命名冲突报告诊断且不影响其他服务器。</summary>
    /// <param name="previous">前一轮刷新。</param><returns>定义更新完成任务。</returns>
    private async Task ReloadAfterAsync(Task previous)
    {
        await Task.Yield();
        await previous.ConfigureAwait(false);
        if (_lifetime.IsCancellationRequested) return;
        try
        {
            var config = _options.ConfigurationLoader?.Invoke() ?? CodingAgentMcpConfiguration.Load(_agentDirectory, _cwd, _projectTrusted());
            var entries = config.Servers.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
            var errors = config.Errors.ToList();
            var registered = _registry.List();
            foreach (var entry in registered)
            {
                if (entries.ContainsKey(entry.Name)) continue;
                if (entries.Keys.Any(name => CodingAgentMcpServers.Namespace(name) == CodingAgentMcpServers.Namespace(entry.Name)))
                { errors.Add($"MCP server \"{entry.Name}\" conflicts with a configured namespace"); continue; }
                entries.Add(entry.Name, entry);
            }
            var retired = new List<Connection>(); var added = new List<Connection>();
            lock (_gate)
            {
                if (_disposed) return;
                ApplySessionOverrides(entries, registered);
                AutoEnableCodemode = config.AutoEnableCodemode != false; _errors = errors;
                foreach (var pair in _connections.ToArray())
                {
                    if (entries.TryGetValue(pair.Key, out var next) && CanKeepConnection(pair.Value.Entry, next))
                    { pair.Value.UpdateEntry(next); continue; }
                    retired.Add(pair.Value); _connections.Remove(pair.Key);
                }
                foreach (var entry in entries.Values)
                    if (!_connections.ContainsKey(entry.Name)) { var connection = new Connection(this, entry); _connections.Add(entry.Name, connection); added.Add(connection); }
                Interlocked.Increment(ref _version);
            }
            // 1. 【CodingAgent】【MCP 配置应用】先关闭旧连接，再启动新连接，禁用项只保留状态
            await Task.WhenAll(retired.Select(connection => connection.DisposeAsync().AsTask())).ConfigureAwait(false);
            foreach (var connection in added) connection.Start();
            QueueProblemReport(added, errors);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            IReadOnlyList<string> errors = [$"MCP configuration reload failed: {error.Message}"];
            lock (_gate) _errors = errors;
            QueueProblemReport([], errors);
        }
    }

    /// <summary>【CodingAgent】【MCP 启动等待】首轮请求只等待可能含 direct 工具的服务器，等待上限不终止后台连接。</summary>
    /// <param name="token">当前请求取消信号。</param><returns>启动等待任务。</returns>
    public async Task WaitForDirectServersAsync(CancellationToken token = default)
    {
        if (Interlocked.Exchange(ref _waitedForDirectServers, 1) != 0) return;
        Task reload; lock (_gate) reload = _reload;
        await reload.WaitAsync(token).ConfigureAwait(false);
        Connection[] connections; lock (_gate) connections = _connections.Values.Where(connection => connection.HasDirectTools).ToArray();
        try { await Task.WhenAll(connections.Select(connection => connection.Initialization)).WaitAsync(_options.StartupWait, token).ConfigureAwait(false); }
        catch (TimeoutException) { NotifyStillConnecting(); }
    }

    /// <summary>【CodingAgent】【MCP 全部就绪】供工具搜索和宿主等待当前所有服务器完成一次连接尝试。</summary>
    /// <param name="token">等待取消信号。</param><returns>连接尝试完成任务。</returns>
    public Task WaitForServersAsync(CancellationToken token = default) => WaitForMatchingServersAsync(_ => true, token);

    /// <summary>【CodingAgent】【MCP 工具快照】按服务器顺序生成当前可用的工具定义，同名规范化冲突添加摘要。</summary>
    /// <returns>独立工具列表。</returns>
    public IReadOnlyList<CodingAgentMcpTool> GetTools()
    {
        var result = new List<CodingAgentMcpTool>(); var names = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            var collisions = _connections.Values.Where(connection => connection.Enabled).SelectMany(connection => connection.Tools
                .Select(tool => tool.GetProperty("name").GetString()!).Distinct(StringComparer.Ordinal).Select(name => CodingAgentMcpTool.CreateName(connection.Entry.Name, name)))
                .GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var connection in _connections.Values.Where(connection => connection.Enabled))
                foreach (var tool in connection.Tools)
                {
                    var original = tool.GetProperty("name").GetString()!;
                    var name = CodingAgentMcpTool.CreateName(connection.Entry.Name, original, candidate => names.Contains(candidate) || collisions.Contains(candidate)); names.Add(name);
                    var ns = new JsonObject { ["name"] = CodingAgentMcpServers.Namespace(connection.Entry.Name) };
                    if (connection.Entry.Config["description"] is { } description) ns["description"] = description.DeepClone();
                    if (!string.IsNullOrWhiteSpace(connection.Instructions)) ns["instructions"] = connection.Instructions.Trim();
                    result.Add(new(connection.Entry.Name, tool, name, CodingAgentMcpServers.GetToolExposure(connection.Entry.Config, original),
                        JsonSerializer.SerializeToElement(ns), connection.GetClientAsync, connection.Timeout,
                        readableResources: () => GetResourceServers().Any(server => server.Name == connection.Entry.Name),
                        source: new(connection.Entry.Source, "mcp", connection.Entry.Scope), executor: connection)
                        { DeclarationResetVersion = connection.DeclarationResetVersion });
                }
        }
        return result;
    }

    /// <summary>【CodingAgent】【MCP 状态查询】复制所有服务器当前状态，不返回凭据。</summary><returns>状态列表。</returns>
    public IReadOnlyList<CodingAgentMcpServerStatus> GetStatus()
    { lock (_gate) return _connections.Values.Select(connection => new CodingAgentMcpServerStatus(connection.Entry.Name, connection.State, connection.Tools.Count, connection.Error)
        { ResourceCount = connection.Resources.Count, ResourceTemplateCount = connection.ResourceTemplates.Count }).ToArray(); }

    /// <summary>【CodingAgent】【资源服务器】返回可读取资源的连接，保留断线连接以便下次请求重连。</summary><returns>启用且非隐藏的服务器。</returns>
    internal IReadOnlyList<CodingAgentMcpResourceServer> GetResourceServers()
    {
        lock (_gate) return _connections.Values.Where(connection => connection.Enabled && connection.HasResources && connection.Entry.Config["exposure"]?.GetValue<string>() != "hidden")
            .Select(connection => new CodingAgentMcpResourceServer(connection.Entry.Name, connection)).ToArray();
    }

    /// <summary>【CodingAgent】【资源工具】按可用资源服务器最宽策略创建三种标准工具，暂无资源时不注册。</summary><returns>资源工具目录。</returns>
    public IReadOnlyList<CodingAgentMcpResourceTool> GetResourceTools()
    {
        lock (_gate)
        {
            var exposures = _connections.Values.Where(connection => connection.Enabled && connection.HasResources).Select(connection => connection.Entry.Config["exposure"]?.GetValue<string>() ?? "codemode").ToHashSet(StringComparer.Ordinal);
            var exposure = new[] { "direct", "codemode", "deferred" }.FirstOrDefault(exposures.Contains);
            return exposure is null ? [] : new[] { "list_mcp_resources", "list_mcp_resource_templates", "read_mcp_resource" }.Select(name => new CodingAgentMcpResourceTool(this, name, exposure)).ToArray();
        }
    }

    /// <summary>【CodingAgent】【MCP 配置策略】在后台连接完成前判断是否需要某种工具发现能力。</summary>
    /// <param name="exposure">暴露策略。</param><returns>是否有启用服务器配置该策略。</returns>
    public bool HasConfiguredExposure(string exposure)
    {
        lock (_gate) return _connections.Values.Any(connection => connection.Enabled &&
            ((connection.Entry.Config["exposure"]?.GetValue<string>() ?? "codemode") == exposure ||
            (connection.Entry.Config["toolExposure"] as JsonObject)?.Any(pair => pair.Value?.GetValue<string>() == exposure) == true));
    }

    /// <summary>【CodingAgent】【MCP 传输创建】解析环境和请求头引用，展开用户目录，并应用宿主认证。</summary>
    /// <param name="entry">已校验的服务器。</param><param name="token">生命周期取消信号。</param><param name="onChallenge">服务器认证质询回调。</param><returns>尚未连接的传输。</returns>
    private async Task<ICodingAgentMcpTransport> CreateTransportAsync(CodingAgentMcpServerEntry entry, CancellationToken token, Action<CodingAgentMcpOAuthChallenge> onChallenge)
    {
        if (_options.TransportFactory is { } factory) return await factory(entry, _cwd, token).ConfigureAwait(false);
        var config = entry.Config;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var map = config.ContainsKey("url") ? "headers" : "env";
        if (config[map] is JsonObject configured)
            foreach (var pair in configured) values[pair.Key] = await _resolver.ResolveOrThrowAsync(pair.Value!.GetValue<string>(), $"MCP server \"{entry.Name}\" {map} \"{pair.Key}\"", token: token).ConfigureAwait(false);
        if (config["url"] is { } url)
        {
            var auth = _options.AuthFactory?.Invoke(entry);
            if (auth is null && config["auth"] is JsonObject providerAuth && _options.ProviderToken is { } resolveToken)
                auth = new CodingAgentMcpProviderAuth(providerAuth["provider"]!.GetValue<string>(), resolveToken);
            if (config.ContainsKey("auth") && auth is null) throw new InvalidOperationException($"MCP server \"{entry.Name}\" requires a provider authentication handler");
            var ownsAuth = auth is null && UsesOAuth(entry);
            if (ownsAuth) auth = new CodingAgentMcpOAuthHttpAuth(url.GetValue<string>(),
                new CodingAgentMcpOAuthCredentialStore(_agentDirectory).ForServer(entry.Name, url.GetValue<string>()),
                cancellation => ResolveOAuthSettingsAsync(entry, cancellation), onChallenge, _options.HttpClient);
            return new CodingAgentMcpHttpTransport(new Uri(url.GetValue<string>()), _options.HttpClient, headers: values, auth: auth, ownsAuth: ownsAuth);
        }
        var cwd = Path.GetFullPath(ExpandHome(config["cwd"]?.GetValue<string>() ?? "."), _cwd);
        return new CodingAgentMcpStdioTransport(ExpandHome(config["command"]!.GetValue<string>()),
            (config["args"] as JsonArray)?.Select(value => ExpandHome(value!.GetValue<string>())).ToArray(), cwd, values);
    }

    /// <summary>【CodingAgent】【MCP 用户目录】展开独立的波浪号和平台目录前缀，不对普通参数执行 shell 插值。</summary>
    /// <param name="value">路径或参数。</param><returns>展开后的文本。</returns>
    private static string ExpandHome(string value) => value == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) :
        value.StartsWith("~/", StringComparison.Ordinal) || OperatingSystem.IsWindows() && value.StartsWith("~\\", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]) : value;

    /// <summary>【CodingAgent】【MCP 服务关闭】取消连接任务，排空配置刷新，关闭所有子进程和 HTTP 会话。</summary><returns>释放任务。</returns>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true; _registry.Changed -= RegistryChanged;
            _notifications.Writer.TryComplete();
            return new(_disposeTask = DisposeCoreAsync(_reload, _authTasks.ToArray(), _notificationReports.ToArray()));
        }
    }

    /// <summary>【CodingAgent】【MCP 服务排空】等待关闭前捕获的登录、配置与连接任务，重复关闭共享此任务。</summary>
    /// <param name="reload">配置刷新任务。</param><param name="authentication">认证操作。</param><param name="reports">启动问题报告任务。</param><returns>关闭任务。</returns>
    private async Task DisposeCoreAsync(Task reload, Task[] authentication, Task[] reports)
    {
        await Task.Yield();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var task in authentication) try { await task.ConfigureAwait(false); } catch (Exception) { }
        await reload.ConfigureAwait(false);
        Connection[] connections;
        lock (_gate) { connections = _connections.Values.ToArray(); _connections.Clear(); Interlocked.Increment(ref _version); }
        await Task.WhenAll(connections.Select(connection => connection.DisposeAsync().AsTask())).ConfigureAwait(false);
        await Task.WhenAll(reports).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    /// <summary>【CodingAgent】【MCP 单连接】合并并发重连，保留断线前目录，列表变更按顺序刷新。</summary>
    private sealed partial class Connection : IAsyncDisposable
    {
        private readonly CodingAgentMcpService _owner;
        private readonly CancellationTokenSource _lifetime;
        private readonly SemaphoreSlim _opening = new(1, 1);
        private readonly object _tasksGate = new();
        private Task _refresh = Task.CompletedTask;
        private Task? _disposeTask;
        private Task<CodingAgentMcpClient>? _connecting;
        private CodingAgentMcpClient? _client;
        private bool _closed;
        internal CodingAgentMcpServerEntry Entry { get; private set; }
        internal long DeclarationResetVersion { get; set; }
        internal bool Enabled => Entry.Config["enabled"]?.GetValue<bool>() != false;
        internal bool HasDirectTools => Enabled && (Entry.Config["exposure"]?.GetValue<string>() == "direct" ||
            (Entry.Config["toolExposure"] as JsonObject)?.Any(pair => pair.Value?.GetValue<string>() == "direct") == true);
        internal TimeSpan Timeout => TimeSpan.FromSeconds(Math.Max(0.001, Entry.Config["timeout"]?.GetValue<double>() ?? 60));
        internal Task Initialization { get; private set; } = Task.CompletedTask;
        internal IReadOnlyList<JsonElement> Tools { get; private set; } = [];
        internal string? Instructions { get; private set; }
        internal bool HasResources { get; private set; }
        internal string State { get; private set; }
        internal string? Error { get; private set; }
        internal CodingAgentMcpOAuthChallenge? Challenge { get; private set; }

        /// <summary>【CodingAgent】【MCP 连接创建】绑定会话取消，不执行服务器程序。</summary><param name="owner">所属服务。</param><param name="entry">定义。</param>
        internal Connection(CodingAgentMcpService owner, CodingAgentMcpServerEntry entry)
        { _owner = owner; Entry = entry; _lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner._lifetime.Token); State = Enabled ? "connecting" : "disabled"; }

        /// <summary>【CodingAgent】【MCP 呈现更新】调用方持有服务锁时发布新定义，保留现有连接及目录。</summary><param name="entry">传输身份相同的新定义。</param>
        internal void UpdateEntry(CodingAgentMcpServerEntry entry) => Entry = entry;

        /// <summary>【CodingAgent】【MCP 后台启动】开始一次受生命周期约束的初始化，失败记录状态供宿主查询。</summary>
        internal void Start() { if (Enabled) Initialization = InitializeAsync(); }

        /// <summary>【CodingAgent】【MCP 初始化隔离】单台服务器失败不阻断会话和其他服务器。</summary><returns>首次尝试结束任务。</returns>
        private async Task InitializeAsync() { try { await GetClientAsync(_lifetime.Token).ConfigureAwait(false); } catch (Exception) { } }

        /// <summary>【CodingAgent】【MCP 按需连接】连接共享给并发调用，调用方取消只取消自己的等待。</summary>
        /// <param name="token">调用取消信号。</param><returns>可请求的客户端。</returns>
        internal Task<CodingAgentMcpClient> GetClientAsync(CancellationToken token)
        {
            lock (_tasksGate)
            {
                if (_closed || !Enabled) throw new InvalidOperationException($"MCP server \"{Entry.Name}\" is closed or disabled");
                if (_client?.IsConnected == true) return Task.FromResult(_client);
                if (_connecting is null || _connecting.IsCompleted) _connecting = ConnectAsync();
                return _connecting.WaitAsync(token);
            }
        }

        /// <summary>【CodingAgent】【MCP 共享连接】由会话生命周期管理底层连接，避免单个调用取消其他调用的连接。</summary>
        /// <returns>完成初始化的客户端。</returns>
        private async Task<CodingAgentMcpClient> ConnectOnceAsync()
        {
            await Task.Yield();
            await _opening.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (_closed || !Enabled) throw new InvalidOperationException($"MCP server \"{Entry.Name}\" is closed or disabled");
                if (_client?.IsConnected == true) return _client;
                if (_client is { } previous) { _client = null; await previous.DisposeAsync().ConfigureAwait(false); }
                lock (_owner._gate)
                {
                    // 1. 【MCP】【连接代次】开始新连接即使旧认证快照失效，避免其覆盖新连接或新失败原因
                    lock (_tasksGate) _connectionGeneration++;
                    State = "connecting"; Error = null;
                }
                var client = new CodingAgentMcpClient(timeout: Timeout, roots: () => Task.FromResult(JsonSerializer.SerializeToElement(new JsonArray(
                    new JsonObject { ["uri"] = new Uri(_owner._cwd).AbsoluteUri, ["name"] = Path.GetFileName(_owner._cwd) }))));
                ICodingAgentMcpTransport? transport = null;
                try
                {
                    transport = await _owner.CreateTransportAsync(Entry, _lifetime.Token,
                        challenge => { lock (_owner._gate) Challenge = challenge; }).ConfigureAwait(false);
                    // 1. 【MCP】【初始化日志】在连接前订阅，保留服务器初始化期间发出的诊断通知
                    client.Notification += (method, parameters) =>
                    {
                        if (method == "notifications/message") _owner._serverLog.Write(Entry.Name, parameters);
                    };
                    await client.ConnectAsync(transport, _lifetime.Token).ConfigureAwait(false);
                    client.Notification += (method, _) =>
                    {
                        if (method == "notifications/tools/list_changed") QueueRefresh(client);
                        else if (method == "notifications/resources/list_changed") QueueResourceRefresh(client);
                    };
                    client.Closed += () => HandleClientClosed(client, transport);
                    // 2. 【MCP】【初始目录】工具和资源并行发现，资源目录失败不影响工具连接
                    var hasResources = client.ServerCapabilities!.Value.TryGetProperty("resources", out _);
                    var tools = client.ServerCapabilities.Value.TryGetProperty("tools", out _) ? client.ListToolsAsync(_lifetime.Token) : Task.FromResult<IReadOnlyList<JsonElement>>([]);
                    var resources = hasResources ? FetchResourceCatalogAsync(client, _lifetime.Token) : Task.FromResult(new ResourceCatalog([], []));
                    var discoveredTools = await tools.ConfigureAwait(false);
                    var discoveredResources = await resources.ConfigureAwait(false);
                    lock (_owner._gate)
                    {
                        _lifetime.Token.ThrowIfCancellationRequested();
                        if (!client.IsConnected) throw new IOException("MCP connection closed during setup");
                        lock (_tasksGate) _client = client;
                        Tools = discoveredTools; Instructions = client.Instructions; State = "connected"; Error = null; TokensAtSignIn = null;
                        HasResources = hasResources; Resources = discoveredResources.Resources; ResourceTemplates = discoveredResources.Templates;
                        Interlocked.Increment(ref _owner._version);
                    }
                    return client;
                }
                catch (Exception error)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    if (IsAuthenticationFailure(error)) await MarkSignInRequiredAsync(error.Message).ConfigureAwait(false);
                    else lock (_owner._gate)
                    {
                        State = _closed || _lifetime.IsCancellationRequested ? "closed" : "failed";
                        Error = WithStandardError(error.Message, transport);
                        Interlocked.Increment(ref _owner._version);
                    }
                    throw;
                }
            }
            finally { _opening.Release(); }
        }

        /// <summary>【MCP】【断线诊断】只更新当前连接，保留服务器错误输出尾部并通知宿主状态变化。</summary>
        /// <param name="client">断线客户端。</param><param name="transport">所属传输。</param>
        private void HandleClientClosed(CodingAgentMcpClient client, ICodingAgentMcpTransport transport)
        {
            lock (_owner._gate)
            {
                if (!ReferenceEquals(_client, client) || _closed) return;
                State = "disconnected"; Error = WithStandardError("Connection closed", transport);
                Interlocked.Increment(ref _owner._version);
            }
        }

        /// <summary>【MCP】【标准错误摘要】连接失败或断开时附带最近 2000 个字符，空输出和 HTTP 错误保持原样。</summary>
        /// <param name="message">原始错误。</param><param name="transport">可选标准管道传输。</param><returns>包含受限错误尾部的诊断。</returns>
        private static string WithStandardError(string message, ICodingAgentMcpTransport? transport)
        {
            var tail = (transport as CodingAgentMcpStdioTransport)?.StandardError.Trim();
            if (string.IsNullOrEmpty(tail)) return message;
            return message + "\n" + tail[Math.Max(0, tail.Length - 2000)..];
        }

        /// <summary>【CodingAgent】【MCP 目录通知】串行排队服务器目录刷新，不阻塞接收报文。</summary><param name="client">通知来源。</param>
        private void QueueRefresh(CodingAgentMcpClient client)
        { lock (_tasksGate) { if (!_closed) _refresh = RefreshAfterAsync(_refresh, client); } }

        /// <summary>【CodingAgent】【MCP 列表刷新】旧连接返回的数据不能覆盖已重连或已关闭的服务器。</summary>
        /// <param name="previous">前一次刷新。</param><param name="client">通知来源。</param><returns>目录刷新任务。</returns>
        private async Task RefreshAfterAsync(Task previous, CodingAgentMcpClient client)
        {
            await Task.Yield(); await previous.ConfigureAwait(false);
            try
            {
                var tools = await client.ListToolsAsync(_lifetime.Token).ConfigureAwait(false);
                lock (_owner._gate)
                {
                    if (_closed || !ReferenceEquals(client, _client)) return;
                    Tools = tools; Interlocked.Increment(ref _owner._version);
                }
            }
            catch (Exception error)
            {
                lock (_owner._gate)
                    if (!_closed && ReferenceEquals(client, _client)) Error = "Failed to refresh tools: " + error.Message;
            }
        }

        /// <summary>【CodingAgent】【MCP 连接关闭】中止初始化，等待目录刷新退出，再释放客户端。</summary><returns>释放任务。</returns>
        public ValueTask DisposeAsync()
        {
            lock (_tasksGate)
            {
                if (_disposeTask is not null) return new(_disposeTask);
                _closed = true;
                return new(_disposeTask = DisposeCoreAsync(_refresh, _connecting));
            }
        }

        /// <summary>【CodingAgent】【MCP 连接排空】先取消请求，再等待连接、退役客户端、刷新与目录任务全部退出。</summary>
        /// <param name="refresh">已排队目录刷新。</param><param name="connecting">当前建连。</param><returns>关闭任务。</returns>
        private async Task DisposeCoreAsync(Task refresh, Task? connecting)
        {
            await Task.Yield();
            await _lifetime.CancelAsync().ConfigureAwait(false);
            if (connecting is not null) try { await connecting.ConfigureAwait(false); } catch (Exception) { }
            await _opening.WaitAsync().ConfigureAwait(false);
            try
            {
                CodingAgentMcpClient[] clients; Task[] retiring;
                lock (_tasksGate) { clients = _retired.Concat(_client is { } current ? [current] : []).Distinct().ToArray(); _retired.Clear(); _client = null; retiring = _retiring.ToArray(); }
                await Task.WhenAll(clients.Select(client => client.DisposeAsync().AsTask())).ConfigureAwait(false);
                await Task.WhenAll(retiring).ConfigureAwait(false);
            }
            finally { _opening.Release(); }
            await Initialization.ConfigureAwait(false); await refresh.ConfigureAwait(false);
            lock (_owner._gate) { State = "closed"; Tools = []; Resources = []; ResourceTemplates = []; }
            _lifetime.Dispose();
        }
    }
}
