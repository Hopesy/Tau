// 作者：xxx
using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai;
namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【会话替换结果】携带取消状态及分叉前选中的输入。</summary>
/// <param name="Cancelled">扩展是否取消操作。</param>
/// <param name="SelectedText">before 分叉时返回的用户文本。</param>
public sealed record CodingAgentSessionReplacementResult(
    [property: JsonPropertyName("cancelled")] bool Cancelled,
    [property: JsonPropertyName("selectedText")] string? SelectedText = null);

public sealed partial class RuntimeCodingAgentRunner
{
    private Func<string, JsonElement, CancellationToken, Task<CodingAgentSessionReplacementResult>>? _replaceSession;
    private Func<CodingAgentTreeSessionController?>? _readTreeController;
    internal CodingAgentTreeSessionController? CurrentTreeSessionController => _readTreeController?.Invoke();
    /// <summary>【CodingAgent】【模型恢复】最近一次会话替换的模型回退说明。</summary>
    public string? SessionModelFallbackMessage { get; internal set; }

    /// <summary>【CodingAgent】【模型预检】在切换存储前解析模型，失效的旧模型回退到已配置模型。</summary>
    /// <param name="snapshot">待恢复会话快照。</param>
    /// <returns>可安全恢复的快照和可选回退说明。</returns>
    internal (CodingAgentSessionSnapshot Snapshot, string? Fallback) PrepareReplacementSnapshot(CodingAgentSessionSnapshot snapshot)
    {
        var branchSelection = CodingAgentBranchModelSelection.Resolve(snapshot, _modelCatalog);
        snapshot = snapshot with { Provider = branchSelection.Provider, Model = branchSelection.Model, BranchEntries = null };
        if (string.IsNullOrWhiteSpace(snapshot.Provider) && string.IsNullOrWhiteSpace(snapshot.Model)) return (snapshot, null);
        try
        {
            var selection = _modelCatalog.ResolveSelection(snapshot.Provider, snapshot.Model, Model.Provider);
            var selected = _modelCatalog.GetModel(selection.Provider, selection.ModelId);
            if (GetAuthStatus(selected.Provider).IsConfigured)
                return (snapshot with { Provider = selected.Provider, Model = selected.Id }, null);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) { }

        // 1. 【CodingAgent】【模型回退】优先采用设置中的可用模型，最后保留已经能运行的当前模型
        var fallback = Model;
        if (_sessionSettings?.Load() is { } settings && !string.IsNullOrWhiteSpace(settings.DefaultProvider) && !string.IsNullOrWhiteSpace(settings.DefaultModel))
        {
            var configured = _modelCatalog.TryGetModel(settings.DefaultProvider, settings.DefaultModel);
            if (configured is not null && GetAuthStatus(configured.Provider).IsConfigured) fallback = configured;
        }
        return (snapshot with { Provider = fallback.Provider, Model = fallback.Id },
            $"Could not restore model {snapshot.Provider}/{snapshot.Model}. Using {fallback.Provider}/{fallback.Id}");
    }

    /// <summary>【CodingAgent】【会话控制绑定】让 SDK、RPC、终端及扩展使用同一实际会话替换通道。</summary>
    /// <param name="replace">实际替换操作。</param>
    /// <param name="readTree">读取替换后的当前树控制器。</param>
    internal void ConfigureSessionReplacement(Func<string, JsonElement, CancellationToken, Task<CodingAgentSessionReplacementResult>> replace,
        Func<CodingAgentTreeSessionController?> readTree)
    {
        _replaceSession = replace;
        _readTreeController = readTree;
    }

    /// <summary>【CodingAgent】【新建会话】创建独立文件或内存会话，执行扩展取消与替换生命周期。</summary>
    /// <param name="parentSession">可选父会话路径。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>实际取消结果。</returns>
    public Task<CodingAgentSessionReplacementResult> NewSessionAsync(string? parentSession = null, CancellationToken cancellationToken = default) =>
        RunSessionReplacementAsync("newSession", JsonSerializer.SerializeToElement(new { parentSession }), cancellationToken);

    /// <summary>【CodingAgent】【恢复会话】加载目标原生会话文件，原会话关闭前先验证目标。</summary>
    /// <param name="sessionPath">目标 JSONL 路径。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>实际取消结果。</returns>
    public Task<CodingAgentSessionReplacementResult> SwitchSessionAsync(string sessionPath, CancellationToken cancellationToken = default) =>
        RunSessionReplacementAsync("switchSession", JsonSerializer.SerializeToElement(new { sessionPath }), cancellationToken);

    /// <summary>【CodingAgent】【分叉会话】从目标条目创建独立会话，before 排除用户输入，at 保留目标。</summary>
    /// <param name="entryId">完整目标标识。</param>
    /// <param name="position">before 或 at。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>实际取消状态和选中文本。</returns>
    public Task<CodingAgentSessionReplacementResult> ForkSessionAsync(string entryId, string position = "before", CancellationToken cancellationToken = default) =>
        RunSessionReplacementAsync("fork", JsonSerializer.SerializeToElement(new { entryId, position }), cancellationToken);

    /// <summary>【CodingAgent】【统一会话入口】独立使用运行器时初始化内存桥接，再调用共享操作。</summary>
    /// <param name="operation">替换类型。</param>
    /// <param name="request">操作参数。</param>
    /// <param name="token">取消信号。</param>
    /// <returns>实际替换结果。</returns>
    private Task<CodingAgentSessionReplacementResult> RunSessionReplacementAsync(string operation, JsonElement request, CancellationToken token)
    {
        EnsureSessionContext(null, null);
        return _replaceSession!(operation, request, token);
    }

    private Action? _reloadSessionResources;
    private bool _extensionToolsEnabled = true;
    private IReadOnlySet<string> _excludedExtensionTools = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>【CodingAgent】【重载工具约束】保留宿主明确禁用或排除的扩展工具，重载不能绕过创建选项。</summary>
    /// <param name="enabled">是否允许加载扩展工具。</param>
    /// <param name="excluded">需要持续排除的工具名称。</param>
    internal void ConfigureExtensionToolReload(bool enabled, IReadOnlyList<string>? excluded)
    {
        _extensionToolsEnabled = enabled;
        _excludedExtensionTools = (excluded ?? []).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>【CodingAgent】【资源重载】记录宿主资源读取器，重载扩展时同步刷新技能和项目提示。</summary>
    /// <param name="reload">重新读取资源并应用到当前运行器的回调。</param>
    internal void ConfigureResourceReload(Action reload) => _reloadSessionResources = reload;

    /// <summary>【CodingAgent】【扩展重建】更新工具、拦截器和事件订阅，保留原有普通工具及仍有效的活动工具。</summary>
    /// <param name="commands">重新发现注册表的扩展存储。</param>
    internal void RefreshExtensionBindings(CodingAgentExtensionCommandStore commands)
    {
        var active = GetActiveToolNames();
        _pendingToolNames.UnionWith(active.Where(IsToolAllowed));
        var defaults = CodingAgentToolSelection.ResolveDefaults(_sessionSettings?.Load().DefaultTools, _registeredTools);
        var addedDefaults = _usesDefaultToolSettings ? defaults.Except(_configuredDefaultToolNames, StringComparer.Ordinal).ToArray() : [];
        _configuredDefaultToolNames = defaults;
        var sdkNames = _baseRegisteredTools.Where(tool => CodingAgentSourceInfo.ForTool(tool).Source == "sdk").Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var extensionTools = commands.LoadTools().Where(tool => _extensionToolsEnabled && IsToolAllowed(tool.Name) && !sdkNames.Contains(tool.Name)).ToArray();
        var replacements = extensionTools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        _registeredTools = _baseRegisteredTools.Where(tool => IsToolAllowed(tool.Name) && !replacements.Contains(tool.Name)).Concat(extensionTools).ToArray();
        _mcpVersion = -1;
        _mcpToolNames.Clear();
        _extensionLifecycleEventSink = commands.LoadLifecycleEventSink();
        _config = _config with { Interceptors = _config.Interceptors.Where(item => item is not CodingAgentExtensionToolEventInterceptor).Concat(commands.LoadToolInterceptors()).ToArray() };
        InstallToolImageNormalization();
        _config = _config with { Tools = ApplyToolLoadout(active.Concat(addedDefaults).Concat(_registeredTools
            .Where(tool => _allowedToolNames?.Contains(tool.Name) == true && GetToolExposure(tool) is "direct" or "model-only" ||
                replacements.Contains(tool.Name) && IsToolActiveOnRegistration(tool)).Select(tool => tool.Name)).Concat(_pendingToolNames).ToArray()) };
        _pendingToolNames.ExceptWith(GetActiveToolNames());
        RefreshToolPromptMetadata();
        if (_sessionSettings is not null)
        {
            ConfigureSessionSettings(_sessionSettings);
            var settings = _sessionSettings.Load();
            SteeringMode = CodingAgentQueueModes.ToAgentQueueMode(settings.SteeringMode);
            FollowUpMode = CodingAgentQueueModes.ToAgentQueueMode(settings.FollowUpMode);
        }
        _reloadSessionResources?.Invoke();
    }
    /// <summary>【CodingAgent】【树导航互斥】仅允许空闲会话导航，摘要及扩展钩子共享可取消的独占事务。</summary>
    /// <param name="navigate">准备、摘要和提交操作。</param>
    /// <param name="token">命令取消信号。</param>
    /// <returns>导航结果。</returns>
    internal async Task<CodingAgentTreeNavigationResult> NavigateSessionAsync(Func<CancellationToken, Task<CodingAgentTreeNavigationResult>> navigate, CancellationToken token)
    {
        if (IsStreaming || IsCompacting || !await _runGate.WaitAsync(0, token).ConfigureAwait(false))
            throw new InvalidOperationException("Wait for the current response, compaction or tree navigation to finish.");
        if (Interlocked.CompareExchange(ref _compactionRequested, 1, 0) != 0)
        {
            _runGate.Release();
            throw new InvalidOperationException("Compaction is already in progress.");
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_backgroundGate) _compactionCancellation = cancellation;
        try { return await navigate(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return new(true, Aborted: true); }
        finally
        {
            lock (_backgroundGate) _compactionCancellation = null;
            Interlocked.Exchange(ref _compactionRequested, 0);
            try { FlushPendingBashMessages(); }
            finally { _runGate.Release(); ScheduleBackgroundDelivery(); }
        }
    }
    /// <summary>【CodingAgent】【会话替换】终止并等待旧回合，在独占运行锁内保存和替换会话。</summary>
    /// <param name="replace">保存旧会话、发布生命周期并恢复新会话的操作。</param>
    /// <param name="token">命令取消信号。</param>
    /// <returns>替换完成任务。</returns>
    internal async Task ReplaceSessionAsync(Func<Task> replace, CancellationToken token)
    {
        Abort();
        await _runGate.WaitAsync(token).ConfigureAwait(false);
        try { await replace().ConfigureAwait(false); }
        finally { _runGate.Release(); ScheduleBackgroundDelivery(); }
    }

    /// <summary>【CodingAgent】【切换诊断】按已有扩展诊断通道记录会话钩子错误。</summary>
    /// <param name="errors">失败的处理器列表。</param>
    internal void ReportSessionHookErrors(IReadOnlyList<CodingAgentExtensionLifecycleEventError> errors)
    {
        foreach (var error in errors) LogExtensionEventError(error, CreateRunLogContext());
    }

    /// <summary>【CodingAgent】【新会话投递】等待替换回调发起的消息完成，并通过宿主事件通道保存和显示结果。</summary>
    /// <param name="deliveries">新上下文发送的消息。</param>
    /// <param name="token">调用取消信号。</param>
    /// <returns>消息投递或回合完成任务。</returns>
    internal async Task DeliverReplacementMessagesAsync(IReadOnlyList<CodingAgentExtensionMessageDelivery> deliveries, CancellationToken token)
    {
        await foreach (var evt in DeliverExtensionMessagesAsync(deliveries, token).ConfigureAwait(false))
            await PublishBackgroundEventAsync(evt, token).ConfigureAwait(false);
    }
}
