using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.Tui.Abstractions;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentExtensionCommand(
    string Name,
    string InvocationName,
    string Description,
    string? ArgumentHint,
    string? Response,
    string? Prompt,
    bool SendToRunner,
    string FilePath,
    string Scope,
    string Runtime = "json")
{
    public CodingAgentSourceInfo SourceInfo { get; init; } = CodingAgentSourceInfo.ForResource(FilePath, Scope, Path.GetDirectoryName(FilePath));
}

public sealed record CodingAgentExtensionCustomMessageDelivery(
    AgentCustomMessage Message,
    bool TriggerTurn,
    string? DeliverAs);

public sealed record CodingAgentExtensionCommandInvocation(
    bool Handled,
    bool IsError,
    bool SendToRunner,
    string Message,
    CodingAgentExtensionCommand Command,
    IReadOnlyList<CodingAgentDisplayedMessage>? DisplayMessages = null,
    IReadOnlyList<AgentCustomMessage>? CustomMessages = null,
    IReadOnlyList<CodingAgentExtensionCustomMessageDelivery>? CustomMessageDeliveries = null)
{
    internal IReadOnlyList<CodingAgentExtensionMessageDelivery>? MessageActions { get; init; }

    public static CodingAgentExtensionCommandInvocation Status(
        CodingAgentExtensionCommand command,
        string message,
        IReadOnlyList<CodingAgentDisplayedMessage>? displayMessages = null,
        IReadOnlyList<AgentCustomMessage>? customMessages = null,
        IReadOnlyList<CodingAgentExtensionCustomMessageDelivery>? customMessageDeliveries = null) =>
        new(true, false, false, message, command, displayMessages, customMessages, customMessageDeliveries);

    public static CodingAgentExtensionCommandInvocation Runner(
        CodingAgentExtensionCommand command,
        string message,
        IReadOnlyList<CodingAgentDisplayedMessage>? displayMessages = null,
        IReadOnlyList<AgentCustomMessage>? customMessages = null,
        IReadOnlyList<CodingAgentExtensionCustomMessageDelivery>? customMessageDeliveries = null) =>
        new(true, false, true, message, command, displayMessages, customMessages, customMessageDeliveries);

    public static CodingAgentExtensionCommandInvocation Error(CodingAgentExtensionCommand command, string message) =>
        new(true, true, false, message, command);
}

public sealed record CodingAgentExtensionTool(
    string Name,
    string Label,
    string Description,
    JsonElement ParameterSchema,
    string FilePath,
    string Scope,
    string Runtime,
    bool HasPrepareArguments,
    string? ExecutionMode)
{
    public CodingAgentSourceInfo? SourceInfo { get; init; }
    public bool HasPrepareLoadout { get; init; }
    public string? PromptSnippet { get; init; }
    public IReadOnlyList<string> PromptGuidelines { get; init; } = [];
    public string Exposure { get; init; } = "direct";
    public bool? DefaultActive { get; init; }
    public JsonElement? OutputSchema { get; init; }
    public JsonElement? Namespace { get; init; }
    public JsonElement? Annotations { get; init; }
    public Tau.Ai.ConstrainedSamplingConfig? ConstrainedSampling { get; init; }
}

public sealed record CodingAgentExtensionFlag(
    string Name,
    string Description,
    string Type,
    JsonElement? DefaultValue,
    string FilePath,
    string Scope,
    string Runtime);

public sealed record CodingAgentExtensionShortcut(
    string Shortcut,
    string Description,
    bool HasHandler,
    string FilePath,
    string Scope,
    string Runtime);

public sealed record CodingAgentResolvedExtensionShortcut(
    KeyBinding KeyBinding,
    CodingAgentExtensionShortcut Shortcut);

public sealed record CodingAgentExtensionShortcutInvocation(
    bool Handled,
    bool IsError,
    bool SendToRunner,
    string Message,
    CodingAgentExtensionShortcut Shortcut,
    IReadOnlyList<CodingAgentDisplayedMessage>? DisplayMessages = null,
    IReadOnlyList<AgentCustomMessage>? CustomMessages = null,
    IReadOnlyList<CodingAgentExtensionCustomMessageDelivery>? CustomMessageDeliveries = null)
{
    internal IReadOnlyList<CodingAgentExtensionMessageDelivery>? MessageActions { get; init; }
    public static CodingAgentExtensionShortcutInvocation Status(
        CodingAgentExtensionShortcut shortcut,
        string message,
        IReadOnlyList<CodingAgentDisplayedMessage>? displayMessages = null,
        IReadOnlyList<AgentCustomMessage>? customMessages = null,
        IReadOnlyList<CodingAgentExtensionCustomMessageDelivery>? customMessageDeliveries = null) =>
        new(true, false, false, message, shortcut, displayMessages, customMessages, customMessageDeliveries);

    public static CodingAgentExtensionShortcutInvocation Runner(
        CodingAgentExtensionShortcut shortcut,
        string message,
        IReadOnlyList<CodingAgentDisplayedMessage>? displayMessages = null,
        IReadOnlyList<AgentCustomMessage>? customMessages = null,
        IReadOnlyList<CodingAgentExtensionCustomMessageDelivery>? customMessageDeliveries = null) =>
        new(true, false, true, message, shortcut, displayMessages, customMessages, customMessageDeliveries);

    public static CodingAgentExtensionShortcutInvocation Error(CodingAgentExtensionShortcut shortcut, string message) =>
        new(true, true, false, message, shortcut);
}

public sealed record CodingAgentExtensionResources(
    IReadOnlyList<string> SkillPaths,
    IReadOnlyList<string> PromptPaths,
    IReadOnlyList<string> ThemePaths)
{
    public IReadOnlyDictionary<string, CodingAgentSourceInfo> SourceInfos { get; init; } = new Dictionary<string, CodingAgentSourceInfo>();
    public CodingAgentExtensionResources(
        IReadOnlyList<string> skillPaths,
        IReadOnlyList<string> promptPaths)
        : this(skillPaths, promptPaths, [])
    {
    }
}

public sealed record CodingAgentExtensionFileStatus(
    string FilePath,
    string Scope,
    int CommandCount,
    IReadOnlyList<string> SkillPaths,
    IReadOnlyList<string> PromptPaths,
    IReadOnlyList<string> ThemePaths);

public sealed record CodingAgentExtensionModule(
    string FilePath,
    string Scope,
    string Runtime,
    string Status)
{
    public bool HasMarkdownTransformer { get; init; }
}

public sealed record CodingAgentExtensionEventHandler(
    string FilePath,
    string Scope,
    string Runtime,
    string EventType);

public sealed record CodingAgentExtensionMessageRenderer(
    string CustomType,
    string FilePath,
    string Scope,
    string Runtime);

/// <summary>【CodingAgent】【条目渲染器】保存类型与所属模块，跨模块冲突采用第一个有效注册。</summary>
public sealed record CodingAgentExtensionEntryRenderer(string CustomType, string FilePath, string Scope, string Runtime);

public sealed record CodingAgentExtensionDiagnostic(
    string Severity,
    string Message,
    string Path,
    string Scope);

public sealed record CodingAgentExtensionStatus(
    IReadOnlyList<CodingAgentExtensionCommand> Commands,
    IReadOnlyList<CodingAgentExtensionTool> Tools,
    IReadOnlyList<CodingAgentExtensionFlag> Flags,
    IReadOnlyList<CodingAgentExtensionShortcut> Shortcuts,
    CodingAgentExtensionResources Resources,
    IReadOnlyList<CodingAgentExtensionFileStatus> Files,
    IReadOnlyList<CodingAgentExtensionModule> Modules,
    IReadOnlyList<CodingAgentExtensionEventHandler> EventHandlers,
    IReadOnlyList<CodingAgentExtensionMessageRenderer> MessageRenderers,
    IReadOnlyList<CodingAgentExtensionDiagnostic> Diagnostics)
{
    public IReadOnlyList<CodingAgentExtensionEntryRenderer> EntryRenderers { get; init; } = [];
    public IReadOnlyList<CodingAgentResourceDiagnostic> ResourceDiagnostics =>
        CodingAgentResourceDiagnostics.FromExtensions(Diagnostics);
}

public sealed partial class CodingAgentExtensionCommandStore : IDisposable
{
    public const string ExtensionPathsEnvironmentVariable = "TAU_CODING_AGENT_EXTENSION_PATHS";

    private readonly string _cwd;
    private readonly string _userExtensionsDirectory;
    private readonly IReadOnlyList<string> _explicitPaths;
    private readonly Func<IReadOnlyList<string>>? _additionalPathsProvider;
    private readonly Func<IReadOnlyDictionary<string, CodingAgentSourceInfo>>? _sourceInfosProvider;
    private readonly bool _includeDefaults;
    public bool IsProjectTrusted { get; set; } = true;
    private readonly CodingAgentJavaScriptExtensionRuntime _javaScriptRuntime;
    private CodingAgentRpcExtensionUiBridge? _extensionUiBridge;
    private string _extensionMode = "tui";
    private long _uiBridgeVersion;
    private readonly object _lifecycleGate = new();
    private Task<IReadOnlyList<CodingAgentExtensionLifecycleEventError>>? _startupTask;
    private bool _sessionStarted;
    private int _disposed;

    public CodingAgentExtensionCommandStore(
        string? cwd = null,
        string? userExtensionsDirectory = null,
        IReadOnlyList<string>? explicitPaths = null,
        Func<IReadOnlyList<string>>? additionalPathsProvider = null,
        bool includeDefaults = true,
        CodingAgentJavaScriptExtensionRuntime? javaScriptRuntime = null,
        Func<IReadOnlyDictionary<string, CodingAgentSourceInfo>>? sourceInfosProvider = null)
    {
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFullPath(cwd);
        _userExtensionsDirectory = string.IsNullOrWhiteSpace(userExtensionsDirectory)
            ? GetDefaultUserExtensionsDirectory()
            : Path.GetFullPath(userExtensionsDirectory);
        _explicitPaths = explicitPaths ?? GetConfiguredExtensionPaths();
        _additionalPathsProvider = additionalPathsProvider;
        _sourceInfosProvider = sourceInfosProvider;
        _includeDefaults = includeDefaults;
        _javaScriptRuntime = javaScriptRuntime ?? new CodingAgentJavaScriptExtensionRuntime(_cwd);
    }

    public IReadOnlyList<CodingAgentExtensionCommand> Load()
    {
        return LoadStatus().Commands;
    }

    /// <summary>【CodingAgent】【扩展绑定】使本存储内的命令、工具和事件共享真实会话。</summary>
    /// <param name="runner">当前运行器。</param>
    /// <param name="tree">可选 JSONL 控制器。</param>
    /// <param name="flat">可选平面会话存储。</param>
    public void BindSession(ICodingAgentRunner runner, CodingAgentTreeSessionController? tree = null, CodingAgentSessionStore? flat = null)
    {
        _javaScriptRuntime.BindSession(runner, tree, flat);
        _javaScriptRuntime.SessionCommands = this;
        if (runner is RuntimeCodingAgentRunner runtime) runtime.ConfigureExtensionCommands(this);
    }

    /// <summary>【CodingAgent】【扩展交互】替换桥接器并解除旧桥接器的事件订阅</summary>
    /// <param name="extensionUiBridge">新的桥接器；为空时停用交互</param>
    /// <param name="mode">宿主运行模式</param>
    public void SetExtensionUiBridge(CodingAgentRpcExtensionUiBridge? extensionUiBridge, string mode = "tui")
    {
        _extensionUiBridge?.SetUiPromptEventPublisher(null);
        _extensionUiBridge = extensionUiBridge;
        _extensionMode = mode;
        var version = Interlocked.Increment(ref _uiBridgeVersion);
        extensionUiBridge?.SetUiPromptEventPublisher((eventType, kind, title, token) =>
            version == Volatile.Read(ref _uiBridgeVersion)
                ? PublishUiPromptEventAsync(eventType, kind, title, token)
                : Task.CompletedTask);
        _javaScriptRuntime.SetExtensionUiBridge(extensionUiBridge, mode);
    }

    /// <summary>【CodingAgent】【扩展重载】重建扩展进程并重新发现注册信息</summary>
    /// <returns>重新初始化后的扩展状态</returns>
    public CodingAgentExtensionStatus Reload()
    {
        PublishSessionShutdownAsync(reason: "reload").GetAwaiter().GetResult();
        ResetRuntime();
        return LoadStatus();
    }

    /// <summary>【CodingAgent】【扩展生命周期】停止当前扩展进程，保留存储对象以供下次加载</summary>
    public void ResetRuntime()
    {
        RefreshResourcePaths?.Invoke();
        // 1. 【CodingAgent】【扩展重载】使旧交互回调失效，避免取消回调意外启动新进程
        SetExtensionUiBridge(_extensionUiBridge, _extensionMode);
        _javaScriptRuntime.Reset();
        ResetDiscoveredResources();
        lock (_lifecycleGate) { _startupTask = null; _sessionStarted = false; }
    }

    /// <summary>【CodingAgent】【扩展生命周期】释放存储拥有的运行时，包括构造时传入的运行时</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        SetExtensionUiBridge(null, _extensionMode);
        try
        {
            if (_sessionStarted) PublishSessionShutdownAsync().GetAwaiter().GetResult();
        }
        finally { _javaScriptRuntime.Dispose(); }
    }

    /// <summary>【CodingAgent】【会话启动】每次扩展进程生命周期只自动发布一次启动事件。</summary>
    /// <param name="cancellationToken">取消等待信号。</param>
    /// <returns>启动处理器错误列表。</returns>
    public Task<IReadOnlyList<CodingAgentExtensionLifecycleEventError>> EnsureSessionStartedAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            _startupTask ??= _sessionStarted
                ? Task.FromResult<IReadOnlyList<CodingAgentExtensionLifecycleEventError>>([])
                : Task.Run(() => PublishSessionStartAsync("startup", cancellationToken), cancellationToken);
            return _startupTask;
        }
    }

    /// <summary>【CodingAgent】【会话关闭】在进程终止前发布关闭事件，允许扩展保存最后状态。</summary>
    /// <param name="cancellationToken">取消信号。</param>
    /// <param name="reason">quit、reload 或会话替换等生命周期原因。</param>
    /// <param name="targetSessionFile">即将切入的会话文件，退出或重载时为空。</param>
    /// <returns>处理器错误列表。</returns>
    public Task<IReadOnlyList<CodingAgentExtensionLifecycleEventError>> PublishSessionShutdownAsync(CancellationToken cancellationToken = default, string reason = "quit", string? targetSessionFile = null)
    {
        lock (_lifecycleGate)
        {
            if (!_sessionStarted) return Task.FromResult<IReadOnlyList<CodingAgentExtensionLifecycleEventError>>([]);
            _sessionStarted = false;
        }
        var payload = JsonSerializer.SerializeToElement(new { type = "session_shutdown", reason, targetSessionFile });
        return PublishSessionEventAsync("session_shutdown", payload, cancellationToken);
    }

    /// <summary>
    /// 将扩展 UI 提示生命周期事件转发给当前已加载的扩展处理器。
    /// </summary>
    /// <param name="eventType">事件类型，必须是 <c>ui_prompt_start</c> 或 <c>ui_prompt_end</c>。</param>
    /// <param name="kind">提示种类。</param>
    /// <param name="title">提示标题。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>扩展处理器错误列表。</returns>
    public async Task PublishUiPromptEventAsync(
        string eventType,
        string kind,
        string? title,
        CancellationToken cancellationToken = default)
    {
        var sink = LoadLifecycleEventSink();
        if (sink is null)
        {
            return;
        }

        _ = await sink.PublishUiPromptAsync(eventType, kind, title, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<CodingAgentExtensionTool> LoadToolDefinitions()
    {
        return LoadStatus().Tools;
    }

    public IReadOnlyList<IAgentTool> LoadTools()
    {
        return LoadToolDefinitions()
            .Select(tool => new CodingAgentExtensionToolAdapter(tool, _javaScriptRuntime))
            .ToArray();
    }

    /// <summary>
    /// 加载扩展注册的自定义消息渲染器。
    /// </summary>
    /// <returns>按扩展加载顺序去重后的消息渲染器列表。</returns>
    public IReadOnlyList<CodingAgentExtensionMessageRenderer> LoadMessageRenderers()
    {
        return LoadStatus().MessageRenderers;
    }

    /// <summary>
    /// 尝试使用扩展注册的消息渲染器渲染自定义消息。
    /// </summary>
    /// <param name="message">需要展示的自定义消息。</param>
    /// <param name="rendered">渲染成功时返回可写入 transcript 的显示消息。</param>
    /// <param name="expanded">是否按展开状态调用扩展 renderer。</param>
    /// <returns>找到 renderer 且渲染出非空文本时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public bool TryRenderCustomMessage(
        AgentCustomMessage message,
        out CodingAgentDisplayedMessage? rendered,
        bool expanded = true)
    {
        ArgumentNullException.ThrowIfNull(message);
        rendered = null;

        foreach (var renderer in LoadMessageRenderers())
        {
            if (!renderer.CustomType.Equals(message.CustomType, StringComparison.Ordinal))
            {
                continue;
            }

            var result = _javaScriptRuntime.RenderMessage(
                renderer.FilePath,
                renderer.CustomType,
                message,
                expanded);
            if (!result.Success)
            {
                return false;
            }

            var text = string.Join(Environment.NewLine, result.Lines);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            rendered = new CodingAgentDisplayedMessage(
                CodingAgentMessageDisplayFormatter.CustomKind,
                text);
            return true;
        }

        return false;
    }

    public IReadOnlyList<IToolInterceptor> LoadToolInterceptors()
    {
        var modules = LoadStatus()
            .EventHandlers
            .Where(static handler =>
                handler.EventType.Equals("tool_call", StringComparison.Ordinal) ||
                handler.EventType.Equals("tool_result", StringComparison.Ordinal))
            .GroupBy(static handler => Path.GetFullPath(handler.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var handlers = group.ToArray();
                var first = handlers[0];
                return new CodingAgentExtensionToolEventModule(
                    first.FilePath,
                    first.Scope,
                    first.Runtime,
                    handlers.Any(static handler => handler.EventType.Equals("tool_call", StringComparison.Ordinal)),
                    handlers.Any(static handler => handler.EventType.Equals("tool_result", StringComparison.Ordinal)));
            })
            .ToArray();

        return modules.Length == 0
            ? []
            : [new CodingAgentExtensionToolEventInterceptor(modules, _javaScriptRuntime)];
    }

    public CodingAgentExtensionLifecycleEventSink? LoadLifecycleEventSink()
    {
        var modules = LoadStatus()
            .EventHandlers
            .Where(static handler => CodingAgentExtensionLifecycleEventSink.IsSupportedEventType(handler.EventType))
            .GroupBy(static handler => Path.GetFullPath(handler.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var handlers = group.ToArray();
                var first = handlers[0];
                return new CodingAgentExtensionLifecycleEventModule(
                    first.FilePath,
                    first.Scope,
                    first.Runtime,
                    handlers
                        .Select(static handler => handler.EventType)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray());
            })
            .ToArray();

        return modules.Length == 0
            ? null
            : new CodingAgentExtensionLifecycleEventSink(modules, _javaScriptRuntime);
    }

    /// <summary>【CodingAgent】【会话启动】发布启动或重载事件。</summary>
    /// <param name="reason">startup、reload、new、resume 或 fork。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <param name="previousSessionFile">替换前的会话文件；首次启动及重载时为空。</param>
    /// <returns>处理器错误列表。</returns>
    public async Task<IReadOnlyList<CodingAgentExtensionLifecycleEventError>> PublishSessionStartAsync(
        string reason = "startup",
        CancellationToken cancellationToken = default, string? previousSessionFile = null)
    {
        lock (_lifecycleGate) _sessionStarted = true;
        using var document = CreateSessionStartEventDocument(reason, previousSessionFile);
        var errors = await PublishSessionEventAsync("session_start", document.RootElement, cancellationToken).ConfigureAwait(false);
        var discoveryErrors = await DiscoverResourcesAsync(reason == "reload" ? "reload" : "startup", cancellationToken).ConfigureAwait(false);
        return errors.Concat(discoveryErrors).ToArray();
    }

    /// <summary>【CodingAgent】【会话事件】向所有注册模块按顺序分发事件。</summary>
    /// <param name="eventType">事件类型。</param>
    /// <param name="extensionEvent">完整事件对象。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>模块或处理器错误列表。</returns>
    private async Task<IReadOnlyList<CodingAgentExtensionLifecycleEventError>> PublishSessionEventAsync(
        string eventType, JsonElement extensionEvent, CancellationToken cancellationToken)
    {
        var modules = LoadStatus()
            .EventHandlers
            .Where(handler => handler.EventType.Equals(eventType, StringComparison.Ordinal))
            .GroupBy(static handler => Path.GetFullPath(handler.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        if (modules.Length == 0)
        {
            return [];
        }

        var errors = new List<CodingAgentExtensionLifecycleEventError>();
        foreach (var module in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _javaScriptRuntime.EmitEventAsync(module.FilePath, extensionEvent, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                errors.Add(new CodingAgentExtensionLifecycleEventError(
                    module.FilePath,
                    module.Scope,
                    module.Runtime,
                    eventType,
                    result.Error ?? "javascript extension session handler failed"));
                continue;
            }

            foreach (var handlerError in result.HandlerErrors)
            {
                errors.Add(new CodingAgentExtensionLifecycleEventError(
                    module.FilePath,
                    module.Scope,
                    module.Runtime,
                    eventType,
                    handlerError));
            }
        }

        return errors;
    }

    public CodingAgentExtensionResources LoadResources()
    {
        return LoadStatus().Resources;
    }

    public CodingAgentExtensionStatus LoadStatus(IKeyBindingMap? keyBindings = null)
    {
        var definitions = new List<CommandDefinition>();
        var tools = new List<CodingAgentExtensionTool>();
        var flags = new List<CodingAgentExtensionFlag>();
        var shortcuts = new List<CodingAgentExtensionShortcut>();
        var skillPaths = new List<string>();
        var promptPaths = new List<string>();
        var themePaths = new List<string>();
        var files = new List<CodingAgentExtensionFileStatus>();
        var modules = new List<CodingAgentExtensionModule>();
        var eventHandlers = new List<CodingAgentExtensionEventHandler>();
        var messageRenderers = new List<CodingAgentExtensionMessageRenderer>();
        var entryRenderers = new List<CodingAgentExtensionEntryRenderer>();
        var diagnostics = new List<CodingAgentExtensionDiagnostic>();
        var autoFilter = AutoResourceFilterProvider?.Invoke();
        if (_includeDefaults)
        {
            LoadSourceDirectory(_userExtensionsDirectory, "user", definitions, tools, flags, shortcuts, skillPaths, promptPaths, themePaths, files, modules, eventHandlers, messageRenderers, entryRenderers, diagnostics, _javaScriptRuntime, resourceFilter: autoFilter);
            if (IsProjectTrusted) LoadSourceDirectory(Path.Combine(_cwd, ".tau", "extensions"), "project", definitions, tools, flags, shortcuts, skillPaths, promptPaths, themePaths, files, modules, eventHandlers, messageRenderers, entryRenderers, diagnostics, _javaScriptRuntime, resourceFilter: autoFilter);
        }

        foreach (var path in GetExplicitPaths())
        {
            var resolved = ResolvePath(path, _cwd);
            if (Directory.Exists(resolved))
            {
                LoadSourceDirectory(resolved, "path", definitions, tools, flags, shortcuts, skillPaths, promptPaths, themePaths, files, modules, eventHandlers, messageRenderers, entryRenderers, diagnostics, _javaScriptRuntime, reportMissing: true);
            }
            else if (File.Exists(resolved) && Path.GetExtension(resolved).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                LoadSourceFile(resolved, "path", definitions, skillPaths, promptPaths, themePaths, files, diagnostics);
            }
            else if (File.Exists(resolved) && IsModuleFile(resolved))
            {
                AddModule(resolved, "path", modules, eventHandlers, messageRenderers, entryRenderers, definitions, tools, flags, shortcuts, diagnostics, _javaScriptRuntime);
            }
            else if (File.Exists(resolved))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    "extension path is not a json file",
                    resolved,
                    "path"));
            }
            else
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    "extension path does not exist",
                    resolved,
                    "path"));
            }
        }

        var resources = new CodingAgentExtensionResources(
            skillPaths.Concat(_discoveredResources.SkillPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            promptPaths.Concat(_discoveredResources.PromptPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            themePaths.Concat(_discoveredResources.ThemePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()) { SourceInfos = _discoveredResources.SourceInfos };

        var resolvedShortcuts = keyBindings is null
            ? shortcuts.ToArray()
            : ResolveShortcuts(shortcuts, keyBindings, diagnostics)
                .Select(static resolved => resolved.Shortcut)
                .ToArray();

        var sourceInfos = modules.DistinctBy(module => Path.GetFullPath(module.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(module => Path.GetFullPath(module.FilePath), module => ResolveExtensionSource(module.FilePath, module.Scope), StringComparer.OrdinalIgnoreCase);
        _javaScriptRuntime.SetExtensionSources(sourceInfos);
        CurrentCommandCatalog = ResolveInvocationNames(definitions).Select(command => command with
        { SourceInfo = sourceInfos.GetValueOrDefault(Path.GetFullPath(command.FilePath)) ?? ResolveExtensionSource(command.FilePath, command.Scope) }).ToArray();
        diagnostics.AddRange(_javaScriptRuntime.ProviderRefreshDiagnostics);
        return new CodingAgentExtensionStatus(
            CurrentCommandCatalog,
            tools.Select(tool => tool with { SourceInfo = sourceInfos.GetValueOrDefault(Path.GetFullPath(tool.FilePath)) }).ToArray(),
            flags.ToArray(),
            resolvedShortcuts,
            resources,
            files,
            modules
                .DistinctBy(static module => Path.GetFullPath(module.FilePath), StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            eventHandlers,
            messageRenderers,
            diagnostics) { EntryRenderers = entryRenderers };
    }

    public IReadOnlyList<CodingAgentResolvedExtensionShortcut> LoadResolvedShortcuts(IKeyBindingMap? keyBindings = null)
    {
        var diagnostics = new List<CodingAgentExtensionDiagnostic>();
        return ResolveShortcuts(LoadStatus().Shortcuts, keyBindings, diagnostics);
    }

    private IEnumerable<string> GetExplicitPaths()
    {
        foreach (var path in _explicitPaths)
        {
            yield return path;
        }

        if (_additionalPathsProvider is null)
        {
            yield break;
        }

        foreach (var path in _additionalPathsProvider())
        {
            yield return path;
        }
    }

    /// <summary>【CodingAgent】【扩展命令】匹配并执行斜杠命令，允许宿主取消运行中的扩展。</summary>
    /// <param name="input">完整命令输入。</param>
    /// <param name="invocation">命令执行结果；未匹配时为空。</param>
    /// <param name="cancellationToken">宿主取消信号。</param>
    /// <returns>是否匹配已注册命令。</returns>
    /// <param name="preserveMessageActions">保留原始消息动作供真实运行器按统一规则投递。</param>
    public bool TryInvoke(string input, out CodingAgentExtensionCommandInvocation? invocation, CancellationToken cancellationToken = default,
        bool preserveMessageActions = false)
    {
        invocation = null;
        if (!input.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var spaceIndex = input.IndexOf(' ');
        var commandName = spaceIndex < 0 ? input[1..] : input[1..spaceIndex];
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return false;
        }

        var command = Load().FirstOrDefault(candidate =>
            candidate.InvocationName.Equals(commandName, StringComparison.Ordinal));
        if (command is null)
        {
            return false;
        }

        var argsText = spaceIndex < 0 ? string.Empty : input[(spaceIndex + 1)..];
        if (IsNodeModuleRuntime(command.Runtime))
        {
            var result = _javaScriptRuntime.Invoke(command.FilePath, command.Name, argsText, cancellationToken, preserveMessageActions);
            if (!result.Success)
            {
                invocation = CodingAgentExtensionCommandInvocation.Error(
                    command,
                    $"extension command '/{command.InvocationName}' failed: {result.Error ?? $"unknown {command.Runtime} extension error"}")
                    with { MessageActions = preserveMessageActions ? result.MessageActions : null };
                return true;
            }

            if (preserveMessageActions)
            {
                invocation = CodingAgentExtensionCommandInvocation.Status(command, result.StatusMessage ?? "")
                    with { MessageActions = result.MessageActions };
                return true;
            }
            var customMessages = result.CustomMessages.Select(static message => message.Message).ToArray();
            var customMessageDeliveries = CreateCustomMessageDeliveries(result.CustomMessages);
            var displayMessages = RenderCustomMessages(result.CustomMessages);
            if (result.RunnerMessages.Count > 0)
            {
                invocation = CodingAgentExtensionCommandInvocation.Runner(
                    command,
                    string.Join($"{Environment.NewLine}{Environment.NewLine}", result.RunnerMessages),
                    displayMessages,
                    customMessages,
                    customMessageDeliveries);
                return true;
            }

            invocation = CodingAgentExtensionCommandInvocation.Status(
                command,
                displayMessages.Count > 0 && string.IsNullOrWhiteSpace(result.StatusMessage)
                    ? string.Empty
                    : string.IsNullOrWhiteSpace(result.StatusMessage)
                    ? $"extension command '/{command.InvocationName}' completed"
                    : result.StatusMessage,
                displayMessages,
                customMessages,
                customMessageDeliveries);
            return true;
        }

        var args = CodingAgentPromptTemplateStore.ParseCommandArgs(argsText);
        var template = command.SendToRunner
            ? command.Prompt ?? command.Response
            : command.Response ?? command.Prompt;
        var message = string.IsNullOrWhiteSpace(template)
            ? string.Empty
            : CodingAgentPromptTemplateStore.SubstituteArgs(template, args);

        if (command.SendToRunner)
        {
            invocation = string.IsNullOrWhiteSpace(message)
                ? CodingAgentExtensionCommandInvocation.Error(
                    command,
                    $"extension command '/{command.InvocationName}' has no prompt or response")
                : CodingAgentExtensionCommandInvocation.Runner(command, message);
            return true;
        }

        invocation = CodingAgentExtensionCommandInvocation.Status(
            command,
            string.IsNullOrWhiteSpace(message)
                ? $"extension command '/{command.InvocationName}' completed"
                : message);
        return true;
    }

    /// <summary>【CodingAgent】【扩展快捷键】执行已解析的扩展快捷键并传递宿主取消信号。</summary>
    /// <param name="shortcut">快捷键定义。</param>
    /// <param name="invocation">调用结果。</param>
    /// <param name="cancellationToken">宿主取消信号。</param>
    /// <param name="preserveMessageActions">是否使用完整有序动作，保留图片与未指定的触发状态。</param>
    /// <returns>是否处理该快捷键。</returns>
    public bool TryInvokeShortcut(
        CodingAgentExtensionShortcut shortcut,
        out CodingAgentExtensionShortcutInvocation? invocation, CancellationToken cancellationToken = default, bool preserveMessageActions = false)
    {
        invocation = null;
        if (!shortcut.HasHandler)
        {
            invocation = CodingAgentExtensionShortcutInvocation.Error(
                shortcut,
                $"extension shortcut '{shortcut.Shortcut}' has no handler");
            return true;
        }

        if (!IsNodeModuleRuntime(shortcut.Runtime))
        {
            invocation = CodingAgentExtensionShortcutInvocation.Error(
                shortcut,
                $"extension shortcut '{shortcut.Shortcut}' uses unsupported runtime '{shortcut.Runtime}'");
            return true;
        }

        var result = _javaScriptRuntime.InvokeShortcut(shortcut.FilePath, shortcut.Shortcut, cancellationToken, preserveMessageActions);
        if (!result.Success)
        {
            invocation = CodingAgentExtensionShortcutInvocation.Error(
                shortcut,
                $"extension shortcut '{shortcut.Shortcut}' failed: {result.Error ?? $"unknown {shortcut.Runtime} extension error"}")
                with { MessageActions = preserveMessageActions ? result.MessageActions : null };
            return true;
        }

        if (preserveMessageActions)
        {
            invocation = CodingAgentExtensionShortcutInvocation.Status(shortcut, result.StatusMessage ?? "")
                with { MessageActions = result.MessageActions };
            return true;
        }
        var customMessages = result.CustomMessages.Select(static message => message.Message).ToArray();
        var customMessageDeliveries = CreateCustomMessageDeliveries(result.CustomMessages);
        var displayMessages = RenderCustomMessages(result.CustomMessages);
        if (result.RunnerMessages.Count > 0)
        {
            invocation = CodingAgentExtensionShortcutInvocation.Runner(
                shortcut,
                string.Join($"{Environment.NewLine}{Environment.NewLine}", result.RunnerMessages),
                displayMessages,
                customMessages,
                customMessageDeliveries);
            return true;
        }

        invocation = CodingAgentExtensionShortcutInvocation.Status(
            shortcut,
            displayMessages.Count > 0 && string.IsNullOrWhiteSpace(result.StatusMessage)
                ? string.Empty
                : string.IsNullOrWhiteSpace(result.StatusMessage)
                ? $"extension shortcut '{shortcut.Shortcut}' completed"
                : result.StatusMessage,
            displayMessages,
            customMessages,
            customMessageDeliveries);
        return true;
    }

    private static IReadOnlyList<CodingAgentExtensionCustomMessageDelivery> CreateCustomMessageDeliveries(
        IReadOnlyList<CodingAgentJavaScriptExtensionCustomMessage> messages)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        return messages
            .Select(static message => new CodingAgentExtensionCustomMessageDelivery(
                message.Message,
                message.TriggerTurn,
                message.DeliverAs))
            .ToArray();
    }

    private IReadOnlyList<CodingAgentDisplayedMessage> RenderCustomMessages(
        IReadOnlyList<CodingAgentJavaScriptExtensionCustomMessage> messages)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        var rendered = new List<CodingAgentDisplayedMessage>();
        foreach (var custom in messages)
        {
            if (!custom.Message.Display)
            {
                continue;
            }

            var text = string.Join(Environment.NewLine, custom.RenderedLines);
            if (!string.IsNullOrWhiteSpace(text))
            {
                rendered.Add(new CodingAgentDisplayedMessage(CodingAgentMessageDisplayFormatter.CustomKind, text));
                continue;
            }

            rendered.Add(TryRenderCustomMessage(custom.Message, out var rendererMessage) && rendererMessage is not null
                ? rendererMessage
                : CodingAgentMessageDisplayFormatter.FormatCustomMessage(custom.Message));
        }

        return rendered.ToArray();
    }

    /// <summary>
    /// Validates CLI-supplied extension flag tokens against currently registered extension flags and
    /// seeds the resolved values into the JavaScript/TypeScript runtime so <c>pi.getFlag(...)</c> returns
    /// them. Mirrors upstream <c>applyExtensionFlagValues</c>: boolean flags resolve to <c>true</c> when
    /// present, string flags require a value, unknown flags and value-less string flags produce error
    /// diagnostics. A <c>null</c> dictionary value means the flag was supplied without an <c>=value</c>.
    /// </summary>
    public IReadOnlyList<CodingAgentExtensionDiagnostic> ApplyExtensionFlagValues(
        IReadOnlyDictionary<string, string?> cliFlags)
    {
        ArgumentNullException.ThrowIfNull(cliFlags);
        if (cliFlags.Count == 0)
        {
            return [];
        }

        var registered = LoadStatus().Flags;
        var registeredByName = new Dictionary<string, CodingAgentExtensionFlag>(StringComparer.Ordinal);
        foreach (var flag in registered)
        {
            registeredByName[flag.Name] = flag;
        }

        var diagnostics = new List<CodingAgentExtensionDiagnostic>();
        var resolved = new Dictionary<string, object>(StringComparer.Ordinal);
        var unknownFlags = new List<string>();
        foreach (var (rawName, value) in cliFlags)
        {
            var name = NormalizeName(rawName);
            if (!registeredByName.TryGetValue(name, out var flag))
            {
                unknownFlags.Add(name);
                continue;
            }

            if (flag.Type.Equals("boolean", StringComparison.Ordinal))
            {
                resolved[name] = true;
                continue;
            }

            if (value is not null)
            {
                resolved[name] = value;
                continue;
            }

            diagnostics.Add(new CodingAgentExtensionDiagnostic(
                "error",
                $"Extension flag \"--{name}\" requires a value",
                flag.FilePath,
                flag.Scope));
        }

        if (unknownFlags.Count > 0)
        {
            diagnostics.Add(new CodingAgentExtensionDiagnostic(
                "error",
                $"Unknown option{(unknownFlags.Count == 1 ? string.Empty : "s")}: {string.Join(", ", unknownFlags.Select(static name => $"--{name}"))}",
                string.Empty,
                "cli"));
        }

        _javaScriptRuntime.SetFlagValues(resolved);
        return diagnostics;
    }

    private static bool IsNodeModuleRuntime(string runtime) =>
        runtime.Equals("javascript", StringComparison.Ordinal) ||
        runtime.Equals("typescript", StringComparison.Ordinal);

    private static IReadOnlyList<CodingAgentResolvedExtensionShortcut> ResolveShortcuts(
        IReadOnlyList<CodingAgentExtensionShortcut> shortcuts,
        IKeyBindingMap? keyBindings,
        ICollection<CodingAgentExtensionDiagnostic> diagnostics)
    {
        var resolved = new Dictionary<KeyBinding, CodingAgentExtensionShortcut>();
        foreach (var shortcut in shortcuts)
        {
            if (!TryParseShortcutKey(shortcut.Shortcut, out var keyBinding))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"Extension shortcut '{shortcut.Shortcut}' from {shortcut.FilePath} uses an unsupported key format. Skipping.",
                    shortcut.FilePath,
                    shortcut.Scope));
                continue;
            }

            if (keyBindings is not null &&
                keyBindings.Bindings.TryGetValue(keyBinding, out var builtInAction) &&
                builtInAction != EditorAction.None)
            {
                if (IsReservedBuiltInShortcutAction(builtInAction))
                {
                    diagnostics.Add(new CodingAgentExtensionDiagnostic(
                        "warning",
                        $"Extension shortcut '{shortcut.Shortcut}' from {shortcut.FilePath} conflicts with built-in shortcut. Skipping.",
                        shortcut.FilePath,
                        shortcut.Scope));
                    continue;
                }

                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"Extension shortcut conflict: '{shortcut.Shortcut}' is built-in shortcut for {builtInAction} and {shortcut.FilePath}. Using {shortcut.FilePath}.",
                    shortcut.FilePath,
                    shortcut.Scope));
            }

            if (resolved.TryGetValue(keyBinding, out var existing))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"Extension shortcut conflict: '{shortcut.Shortcut}' registered by both {existing.FilePath} and {shortcut.FilePath}. Using {shortcut.FilePath}.",
                    shortcut.FilePath,
                    shortcut.Scope));
            }

            resolved[keyBinding] = shortcut;
        }

        return resolved
            .Select(static pair => new CodingAgentResolvedExtensionShortcut(pair.Key, pair.Value))
            .ToArray();
    }

    private static bool IsReservedBuiltInShortcutAction(EditorAction action) =>
        action is EditorAction.Cancel
            or EditorAction.Submit
            or EditorAction.CycleModelForward
            or EditorAction.CycleModelBackward
            or EditorAction.SelectModel
            or EditorAction.OpenExternalEditor
            or EditorAction.KillToLineEnd;

    public static bool TryParseShortcutKey(string shortcut, out KeyBinding keyBinding)
    {
        keyBinding = default;
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return false;
        }

        var parts = shortcut
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part => part.ToLowerInvariant())
            .ToArray();
        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = ConsoleModifiers.None;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            modifiers |= parts[index] switch
            {
                "ctrl" or "control" => ConsoleModifiers.Control,
                "shift" => ConsoleModifiers.Shift,
                "alt" or "option" => ConsoleModifiers.Alt,
                _ => (ConsoleModifiers)(-1)
            };

            if ((int)modifiers < 0)
            {
                return false;
            }
        }

        if (!TryParseConsoleKey(parts[^1], out var key))
        {
            return false;
        }

        keyBinding = new KeyBinding(key, modifiers);
        return true;
    }

    private static bool TryParseConsoleKey(string keyName, out ConsoleKey key)
    {
        key = default;
        if (keyName.Length == 1)
        {
            var ch = keyName[0];
            if (ch is >= 'a' and <= 'z')
            {
                key = Enum.Parse<ConsoleKey>(ch.ToString().ToUpperInvariant());
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                key = Enum.Parse<ConsoleKey>($"D{ch}");
                return true;
            }
        }

        keyName = keyName switch
        {
            "enter" or "return" => nameof(ConsoleKey.Enter),
            "esc" or "escape" => nameof(ConsoleKey.Escape),
            "tab" => nameof(ConsoleKey.Tab),
            "space" or "spacebar" => nameof(ConsoleKey.Spacebar),
            "backspace" => nameof(ConsoleKey.Backspace),
            "delete" or "del" => nameof(ConsoleKey.Delete),
            "left" => nameof(ConsoleKey.LeftArrow),
            "right" => nameof(ConsoleKey.RightArrow),
            "up" => nameof(ConsoleKey.UpArrow),
            "down" => nameof(ConsoleKey.DownArrow),
            _ => keyName
        };

        return Enum.TryParse(keyName, ignoreCase: true, out key);
    }

    private static void LoadSourceDirectory(
        string directory,
        string scope,
        ICollection<CommandDefinition> definitions,
        ICollection<CodingAgentExtensionTool> tools,
        ICollection<CodingAgentExtensionFlag> flags,
        ICollection<CodingAgentExtensionShortcut> shortcuts,
        ICollection<string> skillPaths,
        ICollection<string> promptPaths,
        ICollection<string> themePaths,
        ICollection<CodingAgentExtensionFileStatus> fileStatuses,
        ICollection<CodingAgentExtensionModule> modules,
        ICollection<CodingAgentExtensionEventHandler> eventHandlers,
        ICollection<CodingAgentExtensionMessageRenderer> messageRenderers,
        ICollection<CodingAgentExtensionEntryRenderer> entryRenderers,
        ICollection<CodingAgentExtensionDiagnostic> diagnostics,
        CodingAgentJavaScriptExtensionRuntime javaScriptRuntime,
        bool reportMissing = false,
        Func<string, string, bool>? resourceFilter = null)
    {
        if (!Directory.Exists(directory))
        {
            if (reportMissing)
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    "extension directory does not exist",
                    directory,
                    scope));
            }

            return;
        }

        IEnumerable<string> jsonFiles;
        try
        {
            jsonFiles = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                .Where(static file => !IsIgnoredExtensionPath(file))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new CodingAgentExtensionDiagnostic(
                "warning",
                $"extension directory could not be read: {ex.Message}",
                directory,
                scope));
            return;
        }

        foreach (var file in jsonFiles)
        {
            if (resourceFilter?.Invoke(file, scope) == false) continue;
            LoadSourceFile(file, scope, definitions, skillPaths, promptPaths, themePaths, fileStatuses, diagnostics);
        }

        foreach (var module in DiscoverModuleFiles(directory))
        {
            if (resourceFilter?.Invoke(module, scope) == false) continue;
            AddModule(module, scope, modules, eventHandlers, messageRenderers, entryRenderers, definitions, tools, flags, shortcuts, diagnostics, javaScriptRuntime);
        }
    }

    private static void LoadSourceFile(
        string filePath,
        string scope,
        ICollection<CommandDefinition> definitions,
        ICollection<string> skillPaths,
        ICollection<string> promptPaths,
        ICollection<string> themePaths,
        ICollection<CodingAgentExtensionFileStatus> files,
        ICollection<CodingAgentExtensionDiagnostic> diagnostics)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                File.ReadAllText(filePath),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
        }
        catch (JsonException ex)
        {
            diagnostics.Add(new CodingAgentExtensionDiagnostic(
                "error",
                $"failed to load extension json: {ex.Message}",
                Path.GetFullPath(filePath),
                scope));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new CodingAgentExtensionDiagnostic(
                "warning",
                $"extension file could not be read: {ex.Message}",
                Path.GetFullPath(filePath),
                scope));
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    "extension json root must be an object",
                    Path.GetFullPath(filePath),
                    scope));
                return;
            }

            var commandCountBefore = definitions.Count;
            var fileSkillPaths = new List<string>();
            var filePromptPaths = new List<string>();
            var fileThemePaths = new List<string>();
            var commands = new List<CommandDefinition>();
            if (document.RootElement.TryGetProperty("commands", out var commandArray)
                && commandArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in commandArray.EnumerateArray())
                {
                    var command = ReadCommand(element, filePath, scope);
                    if (command is not null)
                    {
                        commands.Add(command);
                    }
                }
            }
            else
            {
                var singleCommand = ReadCommand(document.RootElement, filePath, scope);
                if (singleCommand is not null)
                {
                    commands.Add(singleCommand);
                }
            }

            var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Environment.CurrentDirectory;
            AddPathArray(document.RootElement, "skillPaths", baseDirectory, fileSkillPaths);
            AddPathArray(document.RootElement, "skill-paths", baseDirectory, fileSkillPaths);
            AddPathArray(document.RootElement, "promptPaths", baseDirectory, filePromptPaths);
            AddPathArray(document.RootElement, "prompt-paths", baseDirectory, filePromptPaths);
            AddPathArray(document.RootElement, "themePaths", baseDirectory, fileThemePaths);
            AddPathArray(document.RootElement, "theme-paths", baseDirectory, fileThemePaths);
            if (document.RootElement.TryGetProperty("resources", out var resources)
                && resources.ValueKind == JsonValueKind.Object)
            {
                AddPathArray(resources, "skillPaths", baseDirectory, fileSkillPaths);
                AddPathArray(resources, "skill-paths", baseDirectory, fileSkillPaths);
                AddPathArray(resources, "promptPaths", baseDirectory, filePromptPaths);
                AddPathArray(resources, "prompt-paths", baseDirectory, filePromptPaths);
                AddPathArray(resources, "themePaths", baseDirectory, fileThemePaths);
                AddPathArray(resources, "theme-paths", baseDirectory, fileThemePaths);
            }

            foreach (var command in commands)
            {
                definitions.Add(command);
            }

            foreach (var path in fileSkillPaths)
            {
                skillPaths.Add(path);
            }

            foreach (var path in filePromptPaths)
            {
                promptPaths.Add(path);
            }

            foreach (var path in fileThemePaths)
            {
                themePaths.Add(path);
            }

            files.Add(new CodingAgentExtensionFileStatus(
                Path.GetFullPath(filePath),
                scope,
                definitions.Count - commandCountBefore,
                fileSkillPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                filePromptPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                fileThemePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
    }

    private static void AddPathArray(
        JsonElement element,
        string propertyName,
        string baseDirectory,
        ICollection<string> paths)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            AddPath(property.GetString(), baseDirectory, paths);
            return;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                AddPath(item.GetString(), baseDirectory, paths);
            }
        }
    }

    private static void AddPath(string? path, string baseDirectory, ICollection<string> paths)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        paths.Add(ResolvePath(path, baseDirectory));
    }

    private static CommandDefinition? ReadCommand(JsonElement element, string filePath, string scope)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = NormalizeName(ReadString(element, "name"));
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new CommandDefinition(
            name,
            ReadString(element, "description")?.Trim() ?? string.Empty,
            ReadString(element, "argumentHint") ?? ReadString(element, "argument-hint"),
            ReadString(element, "response"),
            ReadString(element, "prompt"),
            ReadBool(element, "sendToRunner") ?? ReadBool(element, "send-to-runner") ?? false,
            Path.GetFullPath(filePath),
            scope,
            "json");
    }

    private static IReadOnlyList<CodingAgentExtensionCommand> ResolveInvocationNames(
        IReadOnlyList<CommandDefinition> definitions)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            counts[definition.Name] = counts.GetValueOrDefault(definition.Name) + 1;
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var takenInvocationNames = new HashSet<string>(StringComparer.Ordinal);
        var commands = new List<CodingAgentExtensionCommand>(definitions.Count);

        foreach (var definition in definitions)
        {
            var occurrence = seen.GetValueOrDefault(definition.Name) + 1;
            seen[definition.Name] = occurrence;

            var invocationName = counts[definition.Name] > 1
                ? $"{definition.Name}:{occurrence}"
                : definition.Name;
            if (takenInvocationNames.Contains(invocationName))
            {
                var suffix = occurrence;
                do
                {
                    suffix++;
                    invocationName = $"{definition.Name}:{suffix}";
                }
                while (takenInvocationNames.Contains(invocationName));
            }

            takenInvocationNames.Add(invocationName);
            commands.Add(new CodingAgentExtensionCommand(
                definition.Name,
                invocationName,
                definition.Description,
                string.IsNullOrWhiteSpace(definition.ArgumentHint) ? null : definition.ArgumentHint.Trim(),
                definition.Response,
                definition.Prompt,
                definition.SendToRunner,
                definition.FilePath,
                definition.Scope,
                definition.Runtime));
        }

        return commands.ToArray();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static JsonDocument CreateSessionStartEventDocument(string reason, string? previousSessionFile = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "session_start");
            writer.WriteString("reason", string.IsNullOrWhiteSpace(reason) ? "startup" : reason);
            if (previousSessionFile is not null) writer.WriteString("previousSessionFile", previousSessionFile);
            writer.WriteEndObject();
        }

        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }

    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static string NormalizeName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        var name = rawName.Trim();
        if (name.StartsWith("/", StringComparison.Ordinal))
        {
            name = name[1..];
        }

        return name.Any(char.IsWhiteSpace) ? string.Empty : name;
    }

    private static IReadOnlyList<string> GetConfiguredExtensionPaths()
    {
        var configured = Environment.GetEnvironmentVariable(ExtensionPathsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return [];
        }

        return configured
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static string ResolvePath(string path, string cwd)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(cwd, path));
    }

    private static string GetDefaultUserExtensionsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tau", "extensions");

    private static bool IsIgnoredExtensionPath(string file)
    {
        var parts = Path.GetFullPath(file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(static part =>
            part.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || part.Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> DiscoverModuleFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var rootEntries = ResolveModuleEntries(directory);
        if (rootEntries is not null)
        {
            return rootEntries;
        }

        var modules = new List<string>();
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrWhiteSpace(name) ||
                name.StartsWith(".", StringComparison.Ordinal) ||
                name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(entry))
            {
                if (IsModuleFile(entry))
                {
                    modules.Add(Path.GetFullPath(entry));
                }

                continue;
            }

            if (!Directory.Exists(entry))
            {
                continue;
            }

            var resolved = ResolveModuleEntries(entry);
            if (resolved is not null)
            {
                modules.AddRange(resolved);
            }
        }

        return modules
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string>? ResolveModuleEntries(string directory)
    {
        var manifestEntries = ResolveModuleManifestEntries(directory);
        if (manifestEntries.Count > 0)
        {
            return manifestEntries;
        }

        var indexTs = Path.Combine(directory, "index.ts");
        if (File.Exists(indexTs))
        {
            return [Path.GetFullPath(indexTs)];
        }

        var indexJs = Path.Combine(directory, "index.js");
        if (File.Exists(indexJs))
        {
            return [Path.GetFullPath(indexJs)];
        }

        return null;
    }

    private static IReadOnlyList<string> ResolveModuleManifestEntries(string directory)
    {
        var packageJsonPath = Path.Combine(directory, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return [];
        }

        IReadOnlyList<string>? configured;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            configured = document.RootElement.TryGetProperty("pi", out var pi) && pi.ValueKind == JsonValueKind.Object
                ? ReadPathArray(pi, "extensions")
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        if (configured is null || configured.Count == 0)
        {
            return [];
        }

        var modules = new List<string>();
        foreach (var entry in configured)
        {
            var resolved = ResolvePath(entry, directory);
            if (File.Exists(resolved))
            {
                if (IsModuleFile(resolved))
                {
                    modules.Add(Path.GetFullPath(resolved));
                }

                continue;
            }

            if (Directory.Exists(resolved) &&
                !Path.GetFullPath(resolved).Equals(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            {
                modules.AddRange(DiscoverModuleFiles(resolved));
            }
        }

        return modules
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string>? ReadPathArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString() ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .ToArray();
    }

    private static bool IsModuleFile(string file)
    {
        var extension = Path.GetExtension(file);
        return extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".js", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddModule(
        string filePath,
        string scope,
        ICollection<CodingAgentExtensionModule> modules,
        ICollection<CodingAgentExtensionEventHandler> eventHandlers,
        ICollection<CodingAgentExtensionMessageRenderer> messageRenderers,
        ICollection<CodingAgentExtensionEntryRenderer> entryRenderers,
        ICollection<CommandDefinition> definitions,
        ICollection<CodingAgentExtensionTool> tools,
        ICollection<CodingAgentExtensionFlag> flags,
        ICollection<CodingAgentExtensionShortcut> shortcuts,
        ICollection<CodingAgentExtensionDiagnostic> diagnostics,
        CodingAgentJavaScriptExtensionRuntime javaScriptRuntime)
    {
        var fullPath = Path.GetFullPath(filePath);
        // 1. 【CodingAgent】【扩展去重】用户目录、项目目录及显式路径重叠时，同一模块只注册一次
        if (modules.Any(module => Path.GetFullPath(module.FilePath).Equals(fullPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) return;
        var runtime = Path.GetExtension(filePath).Equals(".ts", StringComparison.OrdinalIgnoreCase)
            ? "typescript"
            : "javascript";

        var result = javaScriptRuntime.Load(fullPath);
        if (!result.Success)
        {
            diagnostics.Add(new CodingAgentExtensionDiagnostic(
                "error",
                $"failed to load {runtime} extension: {result.Error ?? $"unknown {runtime} extension error"}",
                fullPath,
                scope));
            modules.Add(new CodingAgentExtensionModule(
                fullPath,
                scope,
                runtime,
                "load failed"));
            return;
        }

        foreach (var command in result.Commands)
        {
            var name = NormalizeName(command.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension registered a command with an invalid name",
                    fullPath,
                    scope));
                continue;
            }

            if (!command.HasHandler)
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension command '{name}' has no handler",
                    fullPath,
                    scope));
                continue;
            }

            definitions.Add(new CommandDefinition(
                name,
                command.Description.Trim(),
                command.ArgumentHint,
                null,
                null,
                false,
                fullPath,
                scope,
                runtime));
        }

        foreach (var tool in result.Tools)
        {
            var name = NormalizeName(tool.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension registered a tool with an invalid name",
                    fullPath,
                    scope));
                continue;
            }

            if (!tool.HasHandler)
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension tool '{name}' has no execute handler",
                    fullPath,
                    scope));
                continue;
            }

            if (tools.Any(existing => existing.Name.Equals(name, StringComparison.Ordinal)))
            {
                continue;
            }

            tools.Add(new CodingAgentExtensionTool(
                name,
                string.IsNullOrWhiteSpace(tool.Label) ? name : tool.Label.Trim(),
                tool.Description.Trim(),
                tool.ParameterSchema.Clone(),
                fullPath,
                scope,
                runtime,
                tool.HasPrepareArguments,
                tool.ExecutionMode)
                {
                    HasPrepareLoadout = tool.HasPrepareLoadout,
                    PromptSnippet = tool.PromptSnippet, PromptGuidelines = tool.PromptGuidelines,
                    Exposure = tool.Exposure, DefaultActive = tool.DefaultActive, OutputSchema = tool.OutputSchema,
                    Namespace = tool.Namespace, Annotations = tool.Annotations, ConstrainedSampling = tool.ConstrainedSampling
                });
        }

        foreach (var eventType in result.EventHandlerTypes)
        {
            eventHandlers.Add(new CodingAgentExtensionEventHandler(
                fullPath,
                scope,
                runtime,
                eventType));
        }

        foreach (var renderer in result.MessageRenderers)
        {
            var customType = renderer.CustomType.Trim();
            if (string.IsNullOrWhiteSpace(customType))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension registered a message renderer with an invalid custom type",
                    fullPath,
                    scope));
                continue;
            }

            if (!renderer.HasRenderer)
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension message renderer '{customType}' has no renderer function",
                    fullPath,
                    scope));
                continue;
            }

            if (messageRenderers.Any(existing => existing.CustomType.Equals(customType, StringComparison.Ordinal)))
            {
                continue;
            }

            messageRenderers.Add(new CodingAgentExtensionMessageRenderer(
                customType,
                fullPath,
                scope,
                runtime));
        }

        // 2. 【CodingAgent】【条目渲染注册】无效注册保留诊断，不遮盖其他模块的有效渲染器
        foreach (var renderer in result.EntryRenderers)
        {
            var customType = renderer.CustomType.Trim();
            if (string.IsNullOrWhiteSpace(customType) || !renderer.HasRenderer)
            {
                diagnostics.Add(new("error", $"{runtime} extension entry renderer requires a non-empty custom type and a renderer function", fullPath, scope));
                continue;
            }
            if (entryRenderers.All(existing => existing.CustomType != customType))
                entryRenderers.Add(new(customType, fullPath, scope, runtime));
        }

        foreach (var flag in result.Flags)
        {
            var name = NormalizeName(flag.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension registered a flag with an invalid name",
                    fullPath,
                    scope));
                continue;
            }

            if (!flag.Type.Equals("boolean", StringComparison.Ordinal) &&
                !flag.Type.Equals("string", StringComparison.Ordinal))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension flag '{name}' has an unsupported type",
                    fullPath,
                    scope));
                continue;
            }

            if (flags.Any(existing => existing.Name.Equals(name, StringComparison.Ordinal)))
            {
                continue;
            }

            flags.Add(new CodingAgentExtensionFlag(
                name,
                flag.Description.Trim(),
                flag.Type,
                flag.DefaultValue?.Clone(),
                fullPath,
                scope,
                runtime));
        }

        foreach (var shortcut in result.Shortcuts)
        {
            var key = shortcut.Shortcut.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension registered a shortcut with an invalid key",
                    fullPath,
                    scope));
                continue;
            }

            if (!shortcut.HasHandler)
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension shortcut '{key}' has no handler",
                    fullPath,
                    scope));
                continue;
            }

            if (shortcuts.Any(existing => existing.Shortcut.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            shortcuts.Add(new CodingAgentExtensionShortcut(
                key,
                shortcut.Description.Trim(),
                true,
                fullPath,
                scope,
                runtime));
        }

        modules.Add(new CodingAgentExtensionModule(
            fullPath,
            scope,
            runtime,
            FormatNodeModuleStatus(result)) { HasMarkdownTransformer = result.HasMarkdownTransformer });
    }

    private static string FormatNodeModuleStatus(CodingAgentJavaScriptExtensionLoadResult result)
    {
        var status = $"loaded; commands {result.Commands.Count(static command => command.HasHandler)}; tools {result.Tools.Count(static tool => tool.HasHandler)}";
        if (result.HasMarkdownTransformer) status += "; markdown transformer";
        if (result.Flags.Count > 0)
        {
            status = $"{status}; flags {result.Flags.Count}";
        }

        var shortcutCount = result.Shortcuts.Count(static shortcut => shortcut.HasHandler);
        if (shortcutCount > 0)
        {
            status = $"{status}; shortcuts {shortcutCount}";
        }

        if (result.EventHandlerTypes.Count > 0)
        {
            status = $"{status}; events {result.EventHandlerTypes.Count}";
        }

        var messageRendererCount = result.MessageRenderers.Count(static renderer => renderer.HasRenderer);
        if (messageRendererCount > 0)
        {
            status = $"{status}; message renderers {messageRendererCount}";
        }
        var entryRendererCount = result.EntryRenderers.Count(static renderer => renderer.HasRenderer);
        if (entryRendererCount > 0) status = $"{status}; entry renderers {entryRendererCount}";

        status = $"{status}; limited runtime";
        var unsupported = new List<string>();
        if (result.Unsupported.Tools > 0)
        {
            unsupported.Add($"tools {result.Unsupported.Tools}");
        }

        if (result.Unsupported.Flags > 0)
        {
            unsupported.Add($"flags {result.Unsupported.Flags}");
        }

        if (result.Unsupported.Shortcuts > 0)
        {
            unsupported.Add($"shortcuts {result.Unsupported.Shortcuts}");
        }

        if (result.Unsupported.Handlers > 0)
        {
            unsupported.Add($"events {result.Unsupported.Handlers}");
        }

        if (result.Unsupported.MessageRenderers > 0)
        {
            unsupported.Add($"message renderers {result.Unsupported.MessageRenderers}");
        }

        if (result.Unsupported.Providers > 0)
        {
            unsupported.Add($"providers {result.Unsupported.Providers}");
        }

        return unsupported.Count == 0
            ? status
            : $"{status}; unsupported {string.Join(", ", unsupported)}";
    }

    private sealed record CommandDefinition(
        string Name,
        string Description,
        string? ArgumentHint,
        string? Response,
        string? Prompt,
        bool SendToRunner,
        string FilePath,
        string Scope,
        string Runtime);
}
