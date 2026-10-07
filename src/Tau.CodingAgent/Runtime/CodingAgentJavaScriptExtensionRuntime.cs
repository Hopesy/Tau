using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentJavaScriptExtensionCommand(
    string Name,
    string Description,
    string? ArgumentHint,
    bool HasHandler);

public sealed record CodingAgentJavaScriptExtensionTool(
    string Name,
    string Label,
    string Description,
    JsonElement ParameterSchema,
    bool HasHandler,
    bool HasPrepareArguments,
    string? ExecutionMode)
{
    public bool HasPrepareLoadout { get; init; }
    public string? PromptSnippet { get; init; }
    public IReadOnlyList<string> PromptGuidelines { get; init; } = [];
    public string Exposure { get; init; } = "direct";
    public bool? DefaultActive { get; init; }
    public JsonElement? OutputSchema { get; init; }
    public JsonElement? Namespace { get; init; }
    public JsonElement? Annotations { get; init; }
    public ConstrainedSamplingConfig? ConstrainedSampling { get; init; }
}

public sealed record CodingAgentJavaScriptExtensionFlag(
    string Name,
    string Description,
    string Type,
    JsonElement? DefaultValue);

public sealed record CodingAgentJavaScriptExtensionShortcut(
    string Shortcut,
    string Description,
    bool HasHandler);

public sealed record CodingAgentJavaScriptExtensionMessageRenderer(
    string CustomType,
    bool HasRenderer);

public sealed record CodingAgentJavaScriptExtensionCustomMessage(
    AgentCustomMessage Message,
    IReadOnlyList<string> RenderedLines,
    bool TriggerTurn,
    string? DeliverAs);

public sealed record CodingAgentJavaScriptExtensionUnsupportedRegistrations(
    int Tools,
    int Flags,
    int Shortcuts,
    int Handlers,
    int MessageRenderers,
    int Providers);

public sealed record CodingAgentJavaScriptExtensionLoadResult(
    bool Success,
    IReadOnlyList<CodingAgentJavaScriptExtensionCommand> Commands,
    IReadOnlyList<CodingAgentJavaScriptExtensionTool> Tools,
    IReadOnlyList<CodingAgentJavaScriptExtensionFlag> Flags,
    IReadOnlyList<CodingAgentJavaScriptExtensionShortcut> Shortcuts,
    IReadOnlyList<string> EventHandlerTypes,
    IReadOnlyList<CodingAgentJavaScriptExtensionMessageRenderer> MessageRenderers,
    CodingAgentJavaScriptExtensionUnsupportedRegistrations Unsupported,
    string? Error)
{
    /// <summary>【CodingAgent】【条目渲染注册】模块登记的非消息条目渲染器。</summary>
    public IReadOnlyList<CodingAgentJavaScriptExtensionMessageRenderer> EntryRenderers { get; init; } = [];
    public bool HasMarkdownTransformer { get; init; }
}

public sealed record CodingAgentJavaScriptExtensionInvokeResult(
    bool Success,
    IReadOnlyList<string> RunnerMessages,
    IReadOnlyList<CodingAgentJavaScriptExtensionCustomMessage> CustomMessages,
    string? StatusMessage,
    string? Error)
{
    internal IReadOnlyList<CodingAgentExtensionMessageDelivery> MessageActions { get; init; } = [];
}

public sealed record CodingAgentJavaScriptExtensionShortcutInvokeResult(
    bool Success,
    IReadOnlyList<string> RunnerMessages,
    IReadOnlyList<CodingAgentJavaScriptExtensionCustomMessage> CustomMessages,
    string? StatusMessage,
    string? Error)
{
    internal IReadOnlyList<CodingAgentExtensionMessageDelivery> MessageActions { get; init; } = [];
}

public sealed record CodingAgentJavaScriptExtensionUiAction(
    string Method,
    string? Message,
    string? NotifyType,
    string? StatusKey,
    string? StatusText,
    string? WidgetKey,
    IReadOnlyList<string>? WidgetLines,
    string? WidgetPlacement,
    IReadOnlyList<string>? FooterLines,
    IReadOnlyList<string>? HeaderLines,
    string? WorkingMessage,
    IReadOnlyList<string>? WorkingIndicatorFrames,
    int? WorkingIndicatorIntervalMilliseconds,
    string? HiddenThinkingLabel,
    string? Title,
    string? Text);

public sealed record CodingAgentJavaScriptExtensionToolInvokeResult(
    bool Success,
    IReadOnlyList<ContentBlock> Content,
    bool IsError,
    JsonElement? Details,
    string? Error)
{
    public Usage? Usage { get; init; }
    public JsonElement? StructuredContent { get; init; }
    public bool Terminate { get; init; }
}

public sealed record CodingAgentJavaScriptExtensionToolPrepareResult(
    bool Success,
    JsonElement? PreparedArgs,
    string? Error);

public sealed record CodingAgentJavaScriptExtensionToolCallEventResult(
    bool Success,
    bool Blocked,
    bool Terminate,
    string? Reason,
    JsonElement? Arguments,
    string? Error);

public sealed record CodingAgentJavaScriptExtensionToolResultEventResult(
    bool Success,
    IReadOnlyList<ContentBlock> Content,
    bool IsError,
    JsonElement? Details,
    string? Error)
{
    public Usage? Usage { get; init; }
    public JsonElement? StructuredContent { get; init; }
}

public sealed record CodingAgentJavaScriptExtensionEventEmitResult(
    bool Success,
    IReadOnlyList<string> HandlerErrors,
    ChatMessage? ReplacementMessage,
    string? Error)
{
    /// <summary>按处理器顺序转换后的事件，供上下文及协议回调继续传递。</summary>
    public JsonElement? TransformedEvent { get; init; }
}

public sealed record CodingAgentJavaScriptExtensionMessageRenderResult(
    bool Success,
    IReadOnlyList<string> Lines,
    string? Error);

public sealed partial class CodingAgentJavaScriptExtensionRuntime : IDisposable
{
    public const string NodeExecutableEnvironmentVariable = "TAU_CODING_AGENT_NODE";

    private static readonly Lazy<string> NodeScript = new(ReadNodeScript);
    private readonly object _processGate = new();
    private CodingAgentNodeProcess? _process;
    private readonly HashSet<CodingAgentNodeProcess> _retiredProcesses = [];
    private long _processGeneration;
    private long _resetGeneration;

    /// <summary>【CodingAgent】【扩展代次】标识重载前启动的异步工作，防止旧工作重新启动已停止的进程。</summary>
    internal long ResetGeneration => Volatile.Read(ref _resetGeneration);
    private bool _disposed;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly string _cwd;
    private readonly string? _nodeExecutable;
    private readonly TimeSpan _timeout;
    private IReadOnlyDictionary<string, object> _flagValues = EmptyFlagValues;
    private CodingAgentRpcExtensionUiBridge? _extensionUiBridge;
    private string _extensionMode = "print";
    private CodingAgentExtensionSessionBridge? _sessionBridge;
    internal CodingAgentExtensionCommandStore? SessionCommands { get; set; }
    internal CodingAgentTreeSessionController? TreeController => _sessionBridge?.TreeController;

    /// <summary>【CodingAgent】【扩展绑定】把所有命令、工具和事件调用连接到当前会话。</summary>
    /// <param name="runner">当前运行器。</param>
    /// <param name="tree">可选 JSONL 控制器。</param>
    /// <param name="flat">可选平面会话存储。</param>
    public void BindSession(ICodingAgentRunner runner, CodingAgentTreeSessionController? tree = null, CodingAgentSessionStore? flat = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        lock (_processGate)
        {
            if (_sessionBridge?.Matches(runner, tree, flat) == true) return;
            _sessionBridge?.StopBackgroundDelivery(detach: true);
            _sessionBridge = new CodingAgentExtensionSessionBridge(runner, tree, flat, _cwd);
            _sessionBridge.ConfigureReplacement(this);
            if (runner is RuntimeCodingAgentRunner runtime)
            {
                runtime.BindExtensionProviders(this);
                runtime.EnableStateNotifications();
            }
        }
    }

    /// <summary>【CodingAgent】【扩展会话】应用工作进程发出的元数据操作，未绑定时明确报错。</summary>
    /// <param name="action">带调用和会话标识的元数据操作。</param>
    private void ApplySessionAction(JsonElement action) =>
        (_sessionBridge ?? throw new InvalidOperationException("Extension session is not initialized.")).Apply(action);

    private static readonly IReadOnlyDictionary<string, object> EmptyFlagValues =
        new Dictionary<string, object>(StringComparer.Ordinal);

    /// <summary>【CodingAgent】【扩展生命周期】创建按需启动的持久扩展运行时</summary>
    /// <param name="cwd">工作目录；为空时使用当前目录</param>
    /// <param name="nodeExecutable">Node 路径；为空时读取环境配置</param>
    /// <param name="timeout">单次执行时限；用户交互等待不计入时限</param>
    public CodingAgentJavaScriptExtensionRuntime(
        string? cwd = null,
        string? nodeExecutable = null,
        TimeSpan? timeout = null)
    {
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFullPath(cwd);
        _nodeExecutable = string.IsNullOrWhiteSpace(nodeExecutable)
            ? Environment.GetEnvironmentVariable(NodeExecutableEnvironmentVariable)
            : nodeExecutable;
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout != Timeout.InfiniteTimeSpan && (_timeout <= TimeSpan.Zero || _timeout.TotalMilliseconds > uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    /// <summary>【CodingAgent】【扩展参数】设置后续请求可读取的 CLI 标志值</summary>
    /// <param name="flagValues">布尔或字符串标志值；覆盖扩展默认值</param>
    public void SetFlagValues(IReadOnlyDictionary<string, object> flagValues)
    {
        _flagValues = flagValues ?? EmptyFlagValues;
    }

    /// <summary>【CodingAgent】【扩展交互】关联宿主编辑器交互桥接器</summary>
    /// <param name="extensionUiBridge">桥接器；为空时停用交互</param>
    /// <param name="mode">宿主模式名称</param>
    public void SetExtensionUiBridge(CodingAgentRpcExtensionUiBridge? extensionUiBridge, string mode = "tui")
    {
        _extensionUiBridge = extensionUiBridge;
        _extensionMode = mode is "json" or "rpc" ? mode
            : extensionUiBridge is null || string.IsNullOrWhiteSpace(mode) ? "print" : mode;
    }

    public CodingAgentJavaScriptExtensionLoadResult Load(string filePath)
    {
        var execution = Execute(BuildPayload("load", filePath, _cwd));
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionLoadResult(false, [], [], [], [], [], [], EmptyUnsupported, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionLoadResult(
                    false,
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    ReadUnsupported(root),
                    ReadString(root, "error") ?? "javascript extension load failed");
            }

            ApplyProviderRegistrations(ReadOptionalJsonElement(root, "providers"));
            ApplyMcpServers(ReadOptionalJsonElement(root, "mcpServers"));
            return new CodingAgentJavaScriptExtensionLoadResult(
                true,
                ReadCommands(root),
                ReadTools(root),
                ReadFlags(root),
                ReadShortcuts(root),
                ReadStringArray(root, "eventHandlers"),
                ReadMessageRenderers(root),
                ReadUnsupported(root),
                null) { EntryRenderers = ReadMessageRenderers(root, "entryRenderers"), HasMarkdownTransformer = ReadBool(root, "hasMarkdownTransformer") };
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionLoadResult(
                false,
                [],
                [],
                [],
                [],
                [],
                [],
                EmptyUnsupported,
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    /// <summary>
    /// 使用扩展注册的 <c>MessageRenderer</c> 渲染自定义消息。
    /// </summary>
    /// <param name="filePath">注册 renderer 的扩展模块路径。</param>
    /// <param name="customType">要渲染的自定义消息类型。</param>
    /// <param name="message">包含内容、展示开关、详情与时间戳的自定义消息。</param>
    /// <param name="expanded">是否按展开状态渲染消息。</param>
    /// <returns>渲染成功时返回文本行；失败时返回错误信息。</returns>
    public CodingAgentJavaScriptExtensionMessageRenderResult RenderMessage(
        string filePath,
        string customType,
        AgentCustomMessage message,
        bool expanded = true)
    {
        ArgumentNullException.ThrowIfNull(message);

        var execution = Execute(BuildPayload(
            "renderMessage",
            filePath,
            _cwd,
            customType: customType,
            messageContent: message.Content,
            messageDisplay: message.Display,
            messageDetails: message.Details,
            messageTimestamp: message.Timestamp,
            expanded: expanded));
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionMessageRenderResult(false, [], execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionMessageRenderResult(
                    false,
                    [],
                    ReadString(root, "error") ?? "javascript extension message renderer failed");
            }

            return new CodingAgentJavaScriptExtensionMessageRenderResult(
                true,
                ReadStringArray(root, "lines"),
                null);
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionMessageRenderResult(
                false,
                [],
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    /// <summary>【CodingAgent】【扩展命令】执行命令并传递宿主取消信号。</summary>
    /// <param name="filePath">扩展路径。</param>
    /// <param name="commandName">命令名称。</param>
    /// <param name="args">命令文本参数。</param>
    /// <param name="cancellationToken">请求取消信号。</param>
    /// <param name="deferMessageRendering">是否由宿主在完成消息事件中渲染自定义内容。</param>
    /// <returns>命令状态及消息操作。</returns>
    public CodingAgentJavaScriptExtensionInvokeResult Invoke(
        string filePath,
        string commandName,
        string args,
        CancellationToken cancellationToken = default, bool deferMessageRendering = false)
    {
        var execution = Execute(BuildPayload("invoke", filePath, _cwd, commandName, args, deferMessageRendering: deferMessageRendering), cancellationToken);
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionInvokeResult(false, [], [], null, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionInvokeResult(
                    false,
                    [],
                    [],
                    null,
                    ReadString(root, "error") ?? "javascript extension command failed") { MessageActions = ReadMessageActions(root, filePath) };
            }

            DispatchUiActions(root);
            return new CodingAgentJavaScriptExtensionInvokeResult(
                true,
                ReadRunnerMessages(root),
                ReadCustomMessages(root),
                ReadString(root, "returnText"),
                null) { MessageActions = ReadMessageActions(root, filePath) };
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionInvokeResult(
                false,
                [],
                [],
                null,
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    /// <summary>【CodingAgent】【扩展快捷键】调用快捷键处理器并支持协作取消。</summary>
    /// <param name="filePath">扩展路径。</param>
    /// <param name="shortcut">快捷键标识。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <param name="deferMessageRendering">是否交由完成消息事件渲染。</param>
    /// <returns>快捷键产生的状态和消息。</returns>
    public CodingAgentJavaScriptExtensionShortcutInvokeResult InvokeShortcut(
        string filePath,
        string shortcut, CancellationToken cancellationToken = default, bool deferMessageRendering = false)
    {
        var execution = Execute(BuildPayload("invokeShortcut", filePath, _cwd, shortcut: shortcut, deferMessageRendering: deferMessageRendering), cancellationToken);
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionShortcutInvokeResult(false, [], [], null, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionShortcutInvokeResult(
                    false,
                    [],
                    [],
                    null,
                    ReadString(root, "error") ?? "javascript extension shortcut failed") { MessageActions = ReadMessageActions(root, filePath) };
            }

            DispatchUiActions(root);
            return new CodingAgentJavaScriptExtensionShortcutInvokeResult(
                true,
                ReadRunnerMessages(root),
                ReadCustomMessages(root),
                ReadString(root, "returnText"),
                null) { MessageActions = ReadMessageActions(root, filePath) };
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionShortcutInvokeResult(
                false,
                [],
                [],
                null,
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    /// <summary>【CodingAgent】【扩展工具】执行工具并向第三个参数及上下文提供相同的 AbortSignal。</summary>
    /// <param name="filePath">扩展路径。</param>
    /// <param name="toolName">工具名称。</param>
    /// <param name="toolCallId">调用标识。</param>
    /// <param name="args">结构化参数。</param>
    /// <param name="cancellationToken">执行取消信号。</param>
    /// <param name="onUpdate">实时工具进度回调。</param>
    /// <returns>工具结果。</returns>
    public CodingAgentJavaScriptExtensionToolInvokeResult ExecuteTool(
        string filePath,
        string toolName,
        string toolCallId,
        JsonElement args,
        CancellationToken cancellationToken = default,
        Func<ToolUpdate, Task>? onUpdate = null) =>
        ExecuteToolAsync(filePath, toolName, toolCallId, args, cancellationToken, onUpdate).GetAwaiter().GetResult();

    /// <summary>【CodingAgent】【扩展工具】异步等待工具响应和进度，避免占用线程池等待 Node 进程。</summary>
    /// <param name="filePath">扩展路径。</param>
    /// <param name="toolName">工具名称。</param>
    /// <param name="toolCallId">调用标识。</param>
    /// <param name="args">结构化参数。</param>
    /// <param name="cancellationToken">执行取消信号。</param>
    /// <param name="onUpdate">实时工具进度回调。</param>
    /// <returns>工具结果。</returns>
    public async Task<CodingAgentJavaScriptExtensionToolInvokeResult> ExecuteToolAsync(
        string filePath, string toolName, string toolCallId, JsonElement args,
        CancellationToken cancellationToken = default, Func<ToolUpdate, Task>? onUpdate = null)
    {
        var execution = await ExecuteAsync(BuildPayload(
            "executeTool",
            filePath,
            _cwd,
            toolName: toolName,
            toolCallId: toolCallId,
            toolArgs: args), cancellationToken, onUpdate is null ? null : update => onUpdate(ReadToolUpdate(update))).ConfigureAwait(false);
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionToolInvokeResult(false, [], true, null, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionToolInvokeResult(
                    false,
                    [],
                    true,
                    null,
                    ReadString(root, "error") ?? "javascript extension tool failed");
            }

            DispatchUiActions(root);
            DispatchMessageActions(root, filePath);
            return new CodingAgentJavaScriptExtensionToolInvokeResult(
                true,
                ReadContentBlocks(root.GetProperty("content"), preserveEmpty: true),
                ReadBool(root, "isError"),
                root.TryGetProperty("details", out var details) ? details.Clone() : null,
                null) { Usage = ReadToolUsage(root), StructuredContent = ReadOptionalJsonElement(root, "structuredContent"), Terminate = ReadBool(root, "terminate") };
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionToolInvokeResult(
                false,
                [],
                true,
                null,
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    /// <summary>【CodingAgent】【参数准备】调用扩展参数预处理器，宿主取消时解除等待。</summary>
    /// <param name="filePath">扩展路径。</param>
    /// <param name="toolName">工具名称。</param>
    /// <param name="args">原始参数。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>规范化参数或错误。</returns>
    public CodingAgentJavaScriptExtensionToolPrepareResult PrepareToolArguments(
        string filePath,
        string toolName,
        JsonElement args, CancellationToken cancellationToken = default)
    {
        var execution = Execute(BuildPayload(
            "prepareToolArguments",
            filePath,
            _cwd,
            toolName: toolName,
            toolArgs: args), cancellationToken);
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionToolPrepareResult(false, null, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionToolPrepareResult(
                    false,
                    null,
                    ReadString(root, "error") ?? "javascript extension tool argument preparation failed");
            }

            if (!root.TryGetProperty("preparedArgs", out var preparedArgs))
            {
                return new CodingAgentJavaScriptExtensionToolPrepareResult(
                    false,
                    null,
                    "javascript extension tool did not return prepared arguments");
            }

            return new CodingAgentJavaScriptExtensionToolPrepareResult(
                true,
                preparedArgs.Clone(),
                null);
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionToolPrepareResult(
                false,
                null,
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    /// <summary>【CodingAgent】【工具调用事件】允许扩展拦截或修改工具参数，并支持取消等待。</summary>
    /// <param name="filePath">扩展路径。</param>
    /// <param name="toolName">工具名称。</param>
    /// <param name="toolCallId">工具调用标识。</param>
    /// <param name="args">调用参数。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>阻止决定或修改后的参数。</returns>
    public CodingAgentJavaScriptExtensionToolCallEventResult EmitToolCall(
        string filePath,
        string toolName,
        string toolCallId,
        JsonElement args, CancellationToken cancellationToken = default, string? parentToolCallId = null)
    {
        var execution = Execute(BuildPayload(
            "emitToolCall",
            filePath,
            _cwd,
            toolName: toolName,
            toolCallId: toolCallId,
            toolArgs: args, parentToolCallId: parentToolCallId), cancellationToken);
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionToolCallEventResult(false, false, false, null, null, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionToolCallEventResult(
                    false,
                    false,
                    false,
                    null,
                    null,
                    ReadString(root, "error") ?? "javascript extension tool_call handler failed");
            }

            DispatchUiActions(root);
            DispatchMessageActions(root, filePath);
            return new CodingAgentJavaScriptExtensionToolCallEventResult(
                true,
                ReadBool(root, "block"),
                ReadBool(root, "terminate"),
                ReadString(root, "reason"),
                root.TryGetProperty("input", out var inputElement) ? inputElement.Clone() : null,
                null);
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionToolCallEventResult(
                false,
                false,
                false,
                null,
                null,
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    /// <summary>【CodingAgent】【工具结果事件】允许扩展修改工具结果，并支持取消等待。</summary>
    /// <param name="filePath">扩展路径。</param>
    /// <param name="toolName">工具名称。</param>
    /// <param name="toolCallId">工具调用标识。</param>
    /// <param name="args">调用参数。</param>
    /// <param name="result">原始结果。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>扩展处理后的结果。</returns>
    public CodingAgentJavaScriptExtensionToolResultEventResult EmitToolResult(
        string filePath,
        string toolName,
        string toolCallId,
        JsonElement args,
        ToolResult result, CancellationToken cancellationToken = default, string? parentToolCallId = null)
    {
        var execution = Execute(BuildPayload(
            "emitToolResult",
            filePath,
            _cwd,
            toolName: toolName,
            toolCallId: toolCallId,
            toolArgs: args,
            toolResult: result, parentToolCallId: parentToolCallId), cancellationToken);
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionToolResultEventResult(false, [], result.IsError, null, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionToolResultEventResult(
                    false,
                    [],
                    result.IsError,
                    null,
                    ReadString(root, "error") ?? "javascript extension tool_result handler failed");
            }

            DispatchUiActions(root);
            DispatchMessageActions(root, filePath);
            return new CodingAgentJavaScriptExtensionToolResultEventResult(
                true,
                ReadContentBlocks(root.GetProperty("content"), preserveEmpty: true),
                ReadBool(root, "isError"),
                root.TryGetProperty("details", out var details) ? details.Clone() : null,
                null) { Usage = ReadToolUsage(root), StructuredContent = ReadOptionalJsonElement(root, "structuredContent") };
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionToolResultEventResult(
                false,
                [],
                result.IsError,
                null,
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    /// <summary>【CodingAgent】【扩展事件】发布生命周期或请求事件，并允许等待中的处理器协作取消。</summary>
    /// <param name="filePath">扩展路径。</param>
    /// <param name="extensionEvent">事件 JSON。</param>
    /// <param name="cancellationToken">请求取消信号。</param>
    /// <returns>替换消息、转换事件及处理器错误。</returns>
    public CodingAgentJavaScriptExtensionEventEmitResult EmitEvent(
        string filePath,
        JsonElement extensionEvent,
        CancellationToken cancellationToken = default) => EmitEventCore(filePath, extensionEvent, cancellationToken);

    /// <summary>【CodingAgent】【代次事件】只向启动异步任务时的扩展代次发送通知。</summary>
    /// <param name="filePath">扩展文件。</param>
    /// <param name="extensionEvent">事件 JSON。</param>
    /// <param name="generation">启动任务时的重载代次。</param>
    /// <param name="cancellationToken">事件取消信号。</param>
    /// <returns>执行结果；代次变化后拒绝创建或调用进程。</returns>
    internal CodingAgentJavaScriptExtensionEventEmitResult EmitEventForGeneration(string filePath, JsonElement extensionEvent,
        long generation, CancellationToken cancellationToken) => EmitEventCore(filePath, extensionEvent, cancellationToken, generation);

    /// <summary>【CodingAgent】【事件执行】解析扩展结果并投递界面与消息动作。</summary>
    /// <param name="filePath">扩展文件。</param>
    /// <param name="extensionEvent">事件 JSON。</param>
    /// <param name="cancellationToken">事件取消信号。</param>
    /// <param name="generation">可选重载代次限制。</param>
    /// <returns>处理结果与转换事件。</returns>
    private CodingAgentJavaScriptExtensionEventEmitResult EmitEventCore(string filePath, JsonElement extensionEvent,
        CancellationToken cancellationToken, long? generation = null) =>
        EmitEventAsync(filePath, extensionEvent, cancellationToken, generation).GetAwaiter().GetResult();

    /// <summary>【CodingAgent】【异步事件】等待处理器完成，允许处理器重入模型和会话操作。</summary>
    /// <param name="filePath">扩展文件。</param><param name="extensionEvent">事件 JSON。</param>
    /// <param name="cancellationToken">取消信号。</param><param name="generation">可选重载代次。</param>
    /// <returns>处理结果。</returns>
    internal async Task<CodingAgentJavaScriptExtensionEventEmitResult> EmitEventAsync(string filePath, JsonElement extensionEvent,
        CancellationToken cancellationToken, long? generation = null)
    {
        var execution = await ExecuteAsync(BuildPayload(
            "emitEvent",
            filePath,
            _cwd,
            extensionEvent: extensionEvent), cancellationToken, expectedGeneration: generation).ConfigureAwait(false);
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionEventEmitResult(false, [], null, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionEventEmitResult(
                    false,
                    [],
                    null,
                    ReadString(root, "error") ?? "javascript extension event handler failed");
            }

            DispatchUiActions(root);
            DispatchMessageActions(root, filePath);
            return new CodingAgentJavaScriptExtensionEventEmitResult(
                true,
                ReadStringArray(root, "handlerErrors"),
                ReadReplacementMessage(root),
                null)
            {
                TransformedEvent = ReadOptionalJsonElement(root, "transformedEvent")
            };
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionEventEmitResult(
                false,
                [],
                null,
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    /// <summary>【CodingAgent】【扩展调用】在共享工作进程中执行请求，失败后返回可诊断的结果</summary>
    /// <param name="payloadJson">调用参数 JSON</param>
    /// <param name="cancellationToken">当前请求的协作取消信号</param>
    /// <param name="onUpdate">工具中间结果回调</param>
    /// <param name="expectedGeneration">异步事件所属重载代次；为空时使用当前代次</param>
    /// <returns>执行结果或错误信息</returns>
    private ProcessExecutionResult Execute(string payloadJson, CancellationToken cancellationToken = default,
        Func<JsonElement, Task>? onUpdate = null, long? expectedGeneration = null) =>
        ExecuteAsync(payloadJson, cancellationToken, onUpdate, expectedGeneration).GetAwaiter().GetResult();

    /// <summary>【CodingAgent】【扩展调用】异步等待共享工作进程，保留请求取消、进度排空和代次校验。</summary>
    /// <param name="payloadJson">调用参数 JSON。</param>
    /// <param name="cancellationToken">当前请求的协作取消信号。</param>
    /// <param name="onUpdate">工具中间结果回调。</param>
    /// <param name="expectedGeneration">异步事件所属重载代次。</param>
    /// <param name="timeout">覆盖脚本时限，流式提供方由自身取消信号管理。</param>
    /// <returns>执行结果或错误信息。</returns>
    private async Task<ProcessExecutionResult> ExecuteAsync(string payloadJson, CancellationToken cancellationToken = default,
        Func<JsonElement, Task>? onUpdate = null, long? expectedGeneration = null, TimeSpan? timeout = null)
    {
        try
        {
            CodingAgentNodeProcess process;
            // 1. 【CodingAgent】【扩展生命周期】只对进程创建加锁，执行期间允许 UI 事件回调重入
            lock (_processGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (expectedGeneration is { } expected && expected != _resetGeneration)
                    return ProcessExecutionResult.Failed("Extension runtime has been reloaded.");
                if (_process is null || _process.IsStopped)
                {
                    if (_process is { } previousProcess) { previousProcess.Dispose(); _retiredProcesses.Add(previousProcess); }
                    var executable = string.IsNullOrWhiteSpace(_nodeExecutable) ? "node" : _nodeExecutable;
                    var generation = Interlocked.Increment(ref _processGeneration);
                    _process = new CodingAgentNodeProcess(executable, _cwd, NodeScript.Value, HandleRuntimeUiRequestAsync,
                        action => { EnsureProcessGeneration(generation); ApplySessionAction(action); },
                        (request, token, onProgress) =>
                        {
                            EnsureProcessGeneration(generation);
                            if (ReadString(request, "operation") == "publishProviderModels") return Task.FromResult(HandleProviderPublication(request));
                            if (ReadString(request, "operation") == "providerAuthCallback") return HandleProviderAuthCallbackAsync(request, token);
                            if (ReadString(request, "operation") == "providerAuthDeviceId") return Task.FromResult(HandleProviderDeviceId(request, token));
                            return (_sessionBridge ?? throw new InvalidOperationException("Extension session is not initialized.")).HandleRuntimeRequestAsync(request, token, onProgress);
                        },
                        action => { if (generation == Volatile.Read(ref _processGeneration)) DispatchBackgroundActions(action); });
                }
                process = _process;
            }
            return ProcessExecutionResult.Succeeded(await process.ExecuteAsync(payloadJson, timeout ?? _timeout, cancellationToken, onUpdate).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return ProcessExecutionResult.Failed($"node extension runtime unavailable: {ex.Message}");
        }
    }

    /// <summary>【CodingAgent】【进程隔离】拒绝重载前工作进程延迟发出的状态写入和宿主调用。</summary>
    /// <param name="generation">创建工作进程时捕获的代次。</param>
    private void EnsureProcessGeneration(long generation)
    {
        if (generation != Volatile.Read(ref _processGeneration)) throw new InvalidOperationException("Extension runtime has been reloaded.");
    }

    /// <summary>【CodingAgent】【扩展交互】向宿主请求选择、确认或文本输入，并构造带标识的响应</summary>
    /// <param name="root">交互请求参数</param>
    /// <param name="cancellationToken">进程停止时触发的取消信号</param>
    /// <returns>编辑器输入或取消结果的 JSON</returns>
    private async Task<string> HandleRuntimeUiRequestAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var id = ReadString(root, "id");
        if (ReadBool(root, "background") && (_sessionBridge is null || ReadString(root, "sessionId") != _sessionBridge.Snapshot().Header.Id))
            return JsonSerializer.Serialize(new { id, cancelled = true });
        var title = ReadString(root, "title");
        var bridge = _extensionUiBridge;
        TimeSpan? timeout = root.TryGetProperty("timeout", out var timeoutValue) && timeoutValue.TryGetDouble(out var milliseconds)
            && double.IsFinite(milliseconds) && milliseconds > 0 ? TimeSpan.FromMilliseconds(milliseconds) : null;
        object? value = null;
        if (bridge is not null && !string.IsNullOrWhiteSpace(title))
        {
            // 1. 【CodingAgent】【扩展交互】所有对话共用请求关联、取消和 UI 生命周期事件
            value = ReadString(root, "method") switch
            {
                "editor" => await bridge.EditorAsync(title, ReadString(root, "prefill"), timeout, cancellationToken).ConfigureAwait(false),
                "input" => await bridge.InputAsync(title, ReadString(root, "placeholder"), timeout, cancellationToken).ConfigureAwait(false),
                "select" => await bridge.SelectAsync(title, ReadStringArray(root, "options"), timeout, cancellationToken).ConfigureAwait(false),
                "confirm" => await bridge.ConfirmAsync(title, ReadString(root, "message") ?? "", timeout, cancellationToken).ConfigureAwait(false),
                _ => null
            };
        }
        return JsonSerializer.Serialize(new { id, cancelled = value is null, value });
    }

    /// <summary>【CodingAgent】【扩展加载】从程序集读取 Node 脚本，保证发布后可独立运行</summary>
    /// <returns>完整运行时脚本</returns>
    private static string ReadNodeScript()
    {
        using var stream = typeof(CodingAgentJavaScriptExtensionRuntime).Assembly.GetManifestResourceStream(
            "Tau.CodingAgent.Runtime.JavaScript.extension-runtime.cjs")
            ?? throw new InvalidOperationException("embedded node extension runtime script unavailable");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>【CodingAgent】【扩展重载】终止当前进程，下次调用重新初始化全部扩展及依赖</summary>
    public void Reset()
    {
        CodingAgentNodeProcess? process;
        lock (_processGate)
        {
            Interlocked.Increment(ref _resetGeneration);
            Interlocked.Increment(ref _processGeneration);
            process = _process;
            _process = null;
            if (process is not null) _retiredProcesses.Add(process);
        }
        _sessionBridge?.StopBackgroundDelivery();
        process?.Dispose();
        ApplyProviderRegistrations(null);
        ApplyMcpServers(null);
    }

    /// <summary>【CodingAgent】【扩展生命周期】释放进程和交互资源，后续请求返回失败</summary>
    public void Dispose()
    {
        lock (_processGate) _disposed = true;
        Reset();
        CodingAgentNodeProcess[] retired;
        lock (_processGate) { retired = _retiredProcesses.ToArray(); _retiredProcesses.Clear(); }
        foreach (var process in retired) process.DrainHostRequestsAsync().GetAwaiter().GetResult();
        _sessionBridge?.StopBackgroundDelivery(detach: true);
        _sessionBridge?.DrainStateNotifications();
        _extensionUiBridge = null;
    }

    private string BuildPayload(
        string mode,
        string filePath,
        string cwd,
        string? commandName = null,
        string? args = null,
        string? shortcut = null,
        string? toolName = null,
        string? toolCallId = null,
        JsonElement? toolArgs = null,
        ToolResult? toolResult = null,
        JsonElement? extensionEvent = null,
        string? customType = null,
        IReadOnlyList<ContentBlock>? messageContent = null,
        bool? messageDisplay = null,
        object? messageDetails = null,
        DateTimeOffset? messageTimestamp = null,
        bool? expanded = null, bool deferMessageRendering = false, string? parentToolCallId = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("mode", mode);
            if (mode is "load" or "refreshProvider") WriteFactoryProviderDefaults(writer);
            writer.WriteNumber("extensionGeneration", ResetGeneration);
            writer.WriteString("filePath", Path.GetFullPath(filePath));
            writer.WriteString("cwd", cwd);
            writer.WriteString("extensionMode", _extensionMode);
            writer.WritePropertyName("extensionSources"); writer.WriteStartObject();
            foreach (var source in _extensionSources)
            {
                writer.WritePropertyName(source.Key);
                source.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteBoolean("deferMessageRendering", deferMessageRendering);
            if (mode is not ("load" or "providerAuth" or "modelRegistryCallback" or "capabilityProvider") && _sessionBridge is { } sessionBridge)
            {
                writer.WritePropertyName("session");
                JsonSerializer.Serialize(writer, sessionBridge.Snapshot(), CodingAgentTreeSessionJsonContext.Default.CodingAgentExtensionSessionSnapshot);
                writer.WritePropertyName("runtime");
                sessionBridge.WriteRuntimeSnapshot(writer);
            }
            if (commandName is not null)
            {
                writer.WriteString("commandName", commandName);
            }

            if (args is not null)
            {
                writer.WriteString("args", args);
            }

            if (shortcut is not null)
            {
                writer.WriteString("shortcut", shortcut);
            }

            if (toolName is not null)
            {
                writer.WriteString("toolName", toolName);
            }

            if (toolCallId is not null)
            {
                writer.WriteString("toolCallId", toolCallId);
            }
            if (parentToolCallId is not null) writer.WriteString("parentToolCallId", parentToolCallId);

            if (toolArgs.HasValue)
            {
                writer.WritePropertyName("toolArgs");
                toolArgs.Value.WriteTo(writer);
            }

            if (toolResult is not null)
            {
                writer.WritePropertyName("toolResult");
                WriteToolResult(writer, toolResult);
            }

            if (extensionEvent.HasValue)
            {
                writer.WritePropertyName("event");
                extensionEvent.Value.WriteTo(writer);
            }

            if (customType is not null)
            {
                writer.WriteString("customType", customType);
            }

            if (messageContent is not null)
            {
                writer.WritePropertyName("messageContent");
                WriteContentBlocks(writer, messageContent);
            }

            if (messageDisplay.HasValue)
            {
                writer.WriteBoolean("messageDisplay", messageDisplay.Value);
            }

            if (messageDetails is not null)
            {
                writer.WritePropertyName("messageDetails");
                WriteObject(writer, messageDetails);
            }

            if (messageTimestamp.HasValue)
            {
                writer.WriteNumber("messageTimestamp", messageTimestamp.Value.ToUnixTimeMilliseconds());
            }

            if (expanded.HasValue)
            {
                writer.WriteBoolean("expanded", expanded.Value);
            }

            if (_flagValues.Count > 0)
            {
                writer.WritePropertyName("flagValues");
                writer.WriteStartObject();
                foreach (var (name, value) in _flagValues)
                {
                    switch (value)
                    {
                        case bool boolValue:
                            writer.WriteBoolean(name, boolValue);
                            break;
                        case string stringValue:
                            writer.WriteString(name, stringValue);
                            break;
                    }
                }

                writer.WriteEndObject();
            }

            if (_extensionUiBridge is not null)
            {
                writer.WriteBoolean("hasExtensionUi", true);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>【CodingAgent】【工具协议】写入内容、详情及程序调用元数据。</summary>
    /// <param name="writer">JSON 写入器。</param>
    /// <param name="result">工具结果。</param>
    internal static void WriteToolResult(Utf8JsonWriter writer, ToolResult result)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("isError", result.IsError);
        if (result.Terminate) writer.WriteBoolean("terminate", true);
        if (result.StructuredContent is { } structured) { writer.WritePropertyName("structuredContent"); structured.WriteTo(writer); }
        if (result.Usage is { } usage) { writer.WritePropertyName("usage"); WriteToolUsage(writer, usage); }
        writer.WritePropertyName("content");
        WriteContentBlocks(writer, result.Content);
        if (result.Details is not null)
        {
            writer.WritePropertyName("details");
            WriteObject(writer, result.Details);
        }

        writer.WriteEndObject();
    }

    private static void WriteContentBlocks(Utf8JsonWriter writer, IReadOnlyList<ContentBlock> content)
    {
        writer.WriteStartArray();
        foreach (var block in content)
        {
            writer.WriteStartObject();
            switch (block)
            {
                case TextContent text:
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text.Text);
                    if (text.TextSignature is not null) writer.WriteString("textSignature", text.TextSignature);
                    break;
                case ImageContent image:
                    writer.WriteString("type", "image");
                    writer.WriteString("data", image.Data);
                    writer.WriteString("mimeType", image.MimeType);
                    break;
                case ThinkingContent thinking:
                    writer.WriteString("type", "text");
                    writer.WriteString("text", thinking.Thinking);
                    break;
                default:
                    writer.WriteString("type", block.Type);
                    break;
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteObject(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case JsonDocument document:
                document.RootElement.WriteTo(writer);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case int integer:
                writer.WriteNumberValue(integer);
                break;
            case long integer:
                writer.WriteNumberValue(integer);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static IReadOnlyList<CodingAgentJavaScriptExtensionCommand> ReadCommands(JsonElement root)
    {
        if (!root.TryGetProperty("commands", out var commandsElement) ||
            commandsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var commands = new List<CodingAgentJavaScriptExtensionCommand>();
        foreach (var command in commandsElement.EnumerateArray())
        {
            if (command.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(command, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            commands.Add(new CodingAgentJavaScriptExtensionCommand(
                name,
                ReadString(command, "description") ?? string.Empty,
                ReadString(command, "argumentHint"),
                ReadBool(command, "hasHandler")));
        }

        return commands.ToArray();
    }

    /// <summary>【CodingAgent】【工具定义】读取工具参数、输出结构和会话访问元数据。</summary>
    /// <param name="root">模块注册结果。</param>
    /// <returns>模块注册的工具定义。</returns>
    private static IReadOnlyList<CodingAgentJavaScriptExtensionTool> ReadTools(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var toolsElement) ||
            toolsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var tools = new List<CodingAgentJavaScriptExtensionTool>();
        foreach (var tool in toolsElement.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(tool, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            tools.Add(new CodingAgentJavaScriptExtensionTool(
                name,
                ReadString(tool, "label") ?? name,
                ReadString(tool, "description") ?? string.Empty,
                ReadParameterSchema(tool),
                ReadBool(tool, "hasHandler"),
                ReadBool(tool, "hasPrepareArguments"),
                ReadString(tool, "executionMode"))
                {
                    HasPrepareLoadout = ReadBool(tool, "hasPrepareLoadout"),
                    PromptSnippet = ReadString(tool, "promptSnippet"), PromptGuidelines = ReadStringArray(tool, "promptGuidelines"),
                    Exposure = ReadString(tool, "exposure") ?? "direct", DefaultActive = ReadOptionalBool(tool, "defaultActive"),
                    OutputSchema = ReadOptionalJsonElement(tool, "outputSchema"), Namespace = ReadOptionalJsonElement(tool, "namespace"),
                    Annotations = ReadOptionalJsonElement(tool, "annotations"),
                    ConstrainedSampling = tool.TryGetProperty("constrainedSampling", out var sampling) && sampling.ValueKind == JsonValueKind.Object
                        ? JsonSerializer.Deserialize(sampling, CodingAgentTreeSessionJsonContext.Default.ConstrainedSamplingConfig) : null
                });
        }

        return tools.ToArray();
    }

    private static JsonElement ReadParameterSchema(JsonElement tool)
    {
        if (tool.TryGetProperty("parameters", out var parameters) &&
            parameters.ValueKind == JsonValueKind.Object)
        {
            return parameters.Clone();
        }

        using var document = JsonDocument.Parse("""{"type":"object"}""");
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<CodingAgentJavaScriptExtensionFlag> ReadFlags(JsonElement root)
    {
        if (!root.TryGetProperty("flags", out var flagsElement) ||
            flagsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var flags = new List<CodingAgentJavaScriptExtensionFlag>();
        foreach (var flag in flagsElement.EnumerateArray())
        {
            if (flag.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(flag, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            flags.Add(new CodingAgentJavaScriptExtensionFlag(
                name,
                ReadString(flag, "description") ?? string.Empty,
                ReadString(flag, "type") ?? string.Empty,
                ReadFlagDefault(flag)));
        }

        return flags.ToArray();
    }

    private static IReadOnlyList<CodingAgentJavaScriptExtensionShortcut> ReadShortcuts(JsonElement root)
    {
        if (!root.TryGetProperty("shortcuts", out var shortcutsElement) ||
            shortcutsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var shortcuts = new List<CodingAgentJavaScriptExtensionShortcut>();
        foreach (var shortcut in shortcutsElement.EnumerateArray())
        {
            if (shortcut.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var key = ReadString(shortcut, "shortcut");
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            shortcuts.Add(new CodingAgentJavaScriptExtensionShortcut(
                key,
                ReadString(shortcut, "description") ?? string.Empty,
                ReadBool(shortcut, "hasHandler")));
        }

        return shortcuts.ToArray();
    }

    /// <summary>【CodingAgent】【渲染器元数据】读取消息或条目渲染器的类型和函数有效性。</summary>
    /// <param name="root">运行时返回对象。</param><param name="property">注册表字段。</param><returns>渲染器描述列表。</returns>
    private static IReadOnlyList<CodingAgentJavaScriptExtensionMessageRenderer> ReadMessageRenderers(JsonElement root, string property = "messageRenderers")
    {
        if (!root.TryGetProperty(property, out var renderersElement) ||
            renderersElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var renderers = new List<CodingAgentJavaScriptExtensionMessageRenderer>();
        foreach (var renderer in renderersElement.EnumerateArray())
        {
            if (renderer.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            renderers.Add(new CodingAgentJavaScriptExtensionMessageRenderer(
                ReadString(renderer, "customType") ?? string.Empty,
                ReadBool(renderer, "hasRenderer")));
        }

        return renderers.ToArray();
    }

    /// <summary>
    /// 读取 JavaScript message_end handler 返回的替换消息。
    /// </summary>
    /// <param name="root">Node runtime 返回的根 JSON。</param>
    /// <returns>可识别的会话消息；没有替换消息时返回 <see langword="null"/>。</returns>
    private static ChatMessage? ReadReplacementMessage(JsonElement root)
    {
        if (!root.TryGetProperty("replacementMessage", out var message) ||
            message.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        return ReadChatMessage(message);
    }

    /// <summary>
    /// 将扩展返回的 JSON message 转换为 Tau 会话消息。
    /// </summary>
    /// <param name="message">扩展返回的消息 JSON。</param>
    /// <returns>可识别的会话消息；role 缺失或不支持时返回 <see langword="null"/>。</returns>
    private static ChatMessage? ReadChatMessage(JsonElement message)
    {
        var stored = JsonSerializer.Deserialize(message, CodingAgentSessionJsonContext.Default.CodingAgentSessionMessage);
        return stored is null ? null : CodingAgentSessionStore.ToMessage(stored);
    }

    /// <summary>
    /// 读取 assistant message 的停止原因。
    /// </summary>
    /// <param name="message">扩展返回的 assistant message JSON。</param>
    /// <returns>可识别的停止原因；缺失时返回 <see langword="null"/>。</returns>
    private static StopReason? ReadAssistantStopReason(JsonElement message)
    {
        return ReadString(message, "stopReason") switch
        {
            "length" => StopReason.MaxTokens,
            "toolUse" => StopReason.ToolUse,
            "error" => StopReason.Error,
            "aborted" => StopReason.Aborted,
            "stop" => StopReason.EndTurn,
            _ => null
        };
    }

    private static JsonElement? ReadFlagDefault(JsonElement flag)
    {
        if (!flag.TryGetProperty("default", out var value))
        {
            return null;
        }

        return value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.String
            ? value.Clone()
            : null;
    }

    private static IReadOnlyList<string> ReadRunnerMessages(JsonElement root)
    {
        if (!root.TryGetProperty("actions", out var actionsElement) ||
            actionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var messages = new List<string>();
        foreach (var action in actionsElement.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object || ReadString(action, "type") is not ("sendMessage" or "userMessage"))
            {
                continue;
            }

            var message = ReadString(action, "type") == "userMessage" && action.TryGetProperty("content", out var content)
                ? string.Join("\n", ReadContentBlocks(content).OfType<TextContent>().Select(block => block.Text))
                : ReadString(action, "message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                messages.Add(message);
            }
        }

        return messages.ToArray();
    }

    private static IReadOnlyList<CodingAgentJavaScriptExtensionCustomMessage> ReadCustomMessages(JsonElement root)
    {
        if (!root.TryGetProperty("actions", out var actionsElement) ||
            actionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var messages = new List<CodingAgentJavaScriptExtensionCustomMessage>();
        foreach (var action in actionsElement.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadString(action, "type"), "customMessage", StringComparison.Ordinal))
            {
                continue;
            }

            var customType = ReadString(action, "customType");
            if (string.IsNullOrWhiteSpace(customType) ||
                !action.TryGetProperty("content", out var contentElement))
            {
                continue;
            }

            messages.Add(new CodingAgentJavaScriptExtensionCustomMessage(
                new AgentCustomMessage(
                    customType,
                    ReadContentBlocks(contentElement),
                    ReadOptionalBool(action, "display") ?? true,
                    ReadOptionalJsonElement(action, "details"),
                    ReadUnixMilliseconds(action, "timestamp")),
                ReadOptionalStringArray(action, "renderedLines") ?? [],
                ReadOptionalBool(action, "triggerTurn") ?? false,
                ReadCustomMessageDeliverAs(action)));
        }

        return messages.ToArray();
    }

    private static string? ReadCustomMessageDeliverAs(JsonElement action)
    {
        var deliverAs = ReadString(action, "deliverAs");
        return deliverAs is "steer" or "followUp" or "nextTurn"
            ? deliverAs
            : null;
    }

    /// <summary>【CodingAgent】【内容转换】读取文字及图片，并按调用方约定保留空工具内容。</summary>
    /// <param name="contentElement">字符串或内容块数组。</param>
    /// <param name="preserveEmpty">是否保留空数组，不插入占位文字。</param>
    /// <returns>可在运行器及会话中使用的内容块。</returns>
    private static IReadOnlyList<ContentBlock> ReadContentBlocks(JsonElement contentElement, bool preserveEmpty = false)
    {
        if (contentElement.ValueKind == JsonValueKind.String)
        {
            return [new TextContent(contentElement.GetString() ?? string.Empty)];
        }

        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return [new TextContent(contentElement.ToString())];
        }

        var blocks = new List<ContentBlock>();
        foreach (var item in contentElement.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    blocks.Add(new TextContent(item.GetString() ?? string.Empty));
                    break;
                case JsonValueKind.Object:
                    var type = ReadString(item, "type");
                    if (string.Equals(type, "image", StringComparison.Ordinal))
                    {
                        var data = ReadString(item, "data");
                        var mimeType = ReadString(item, "mimeType");
                        if (!string.IsNullOrWhiteSpace(data) && !string.IsNullOrWhiteSpace(mimeType))
                        {
                            blocks.Add(new ImageContent(data, mimeType));
                        }
                    }
                    else
                    {
                        blocks.Add(new TextContent(
                            ReadString(item, "text") ??
                            ReadString(item, "content") ??
                            (string.IsNullOrWhiteSpace(type) ? item.ToString() : $"[{type}]"))
                            { TextSignature = ReadString(item, "textSignature") });
                    }
                    break;
                default:
                    blocks.Add(new TextContent(item.ToString()));
                    break;
            }
        }

        return blocks.Count == 0 && !preserveEmpty ? [new TextContent(string.Empty)] : blocks.ToArray();
    }

    /// <summary>【CodingAgent】【工具进度】从独立进度帧恢复内容块、错误状态和任意结构化详情。</summary>
    /// <param name="update">进度 JSON 对象。</param>
    /// <returns>交给工具执行器的中间结果。</returns>
    private static ToolUpdate ReadToolUpdate(JsonElement update)
    {
        var content = ReadContentBlocks(update.GetProperty("content"), preserveEmpty: true);
        return new ToolUpdate(string.Join("\n", content.OfType<TextContent>().Select(static block => block.Text)),
            content, ReadBool(update, "isError"), update.TryGetProperty("details", out var details) ? details.Clone() : null, ReadBool(update, "terminate"))
            { Usage = ReadToolUsage(update), StructuredContent = ReadOptionalJsonElement(update, "structuredContent") };
    }

    private static JsonElement? ReadOptionalJsonElement(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.Clone();
    }

    private static DateTimeOffset? ReadUnixMilliseconds(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out var milliseconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private void DispatchUiActions(JsonElement root)
    {
        var bridge = _extensionUiBridge;
        if (bridge is null)
        {
            return;
        }

        foreach (var action in ReadUiActions(root))
        {
            switch (action.Method)
            {
                case "notify":
                    if (!string.IsNullOrWhiteSpace(action.Message))
                    {
                        bridge.NotifyAsync(action.Message, action.NotifyType, CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                    }
                    break;
                case "setStatus":
                    if (!string.IsNullOrWhiteSpace(action.StatusKey))
                    {
                        bridge.SetStatusAsync(action.StatusKey, action.StatusText, CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                    }
                    break;
                case "setWidget":
                    if (!string.IsNullOrWhiteSpace(action.WidgetKey))
                    {
                        bridge.SetWidgetAsync(action.WidgetKey, action.WidgetLines, action.WidgetPlacement, CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                    }
                    break;
                case "setFooter":
                    bridge.SetFooterAsync(action.FooterLines, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    break;
                case "setHeader":
                    bridge.SetHeaderAsync(action.HeaderLines, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    break;
                case "setWorkingMessage":
                    bridge.SetWorkingMessageAsync(action.WorkingMessage, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    break;
                case "setWorkingIndicator":
                    bridge.SetWorkingIndicatorAsync(
                            action.WorkingIndicatorFrames,
                            action.WorkingIndicatorIntervalMilliseconds,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    break;
                case "setHiddenThinkingLabel":
                    bridge.SetHiddenThinkingLabelAsync(action.HiddenThinkingLabel, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    break;
                case "setTitle":
                    if (!string.IsNullOrWhiteSpace(action.Title))
                    {
                        bridge.SetTitleAsync(action.Title, CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                    }
                    break;
                case "set_editor_text":
                    bridge.SetEditorTextAsync(action.Text ?? string.Empty, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    break;
            }
        }
    }

    private static IReadOnlyList<CodingAgentJavaScriptExtensionUiAction> ReadUiActions(JsonElement root)
    {
        if (!root.TryGetProperty("actions", out var actionsElement) ||
            actionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var actions = new List<CodingAgentJavaScriptExtensionUiAction>();
        foreach (var action in actionsElement.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadString(action, "type"), "ui", StringComparison.Ordinal))
            {
                continue;
            }

            var method = ReadString(action, "method");
            if (string.IsNullOrWhiteSpace(method))
            {
                continue;
            }

            actions.Add(new CodingAgentJavaScriptExtensionUiAction(
                method,
                ReadString(action, "message"),
                ReadString(action, "notifyType"),
                ReadString(action, "statusKey"),
                ReadString(action, "statusText"),
                ReadString(action, "widgetKey"),
                ReadOptionalStringArray(action, "widgetLines"),
                ReadString(action, "widgetPlacement"),
                ReadOptionalStringArray(action, "footerLines"),
                ReadOptionalStringArray(action, "headerLines"),
                ReadString(action, "workingMessage"),
                ReadOptionalStringArray(action, "workingIndicatorFrames"),
                ReadOptionalInt32(action, "workingIndicatorIntervalMs"),
                ReadString(action, "hiddenThinkingLabel"),
                ReadString(action, "title"),
                ReadString(action, "text")));
        }

        return actions.ToArray();
    }

    private static IReadOnlyList<string>? ReadOptionalStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arrayElement) ||
            arrayElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (arrayElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<string>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                values.Add(item.GetString() ?? string.Empty);
            }
        }

        return values.ToArray();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arrayElement) ||
            arrayElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                values.Add(item.GetString() ?? string.Empty);
            }
        }

        return values.ToArray();
    }

    private static int? ReadOptionalInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static CodingAgentJavaScriptExtensionUnsupportedRegistrations ReadUnsupported(JsonElement root)
    {
        if (!root.TryGetProperty("unsupported", out var unsupported) ||
            unsupported.ValueKind != JsonValueKind.Object)
        {
            return EmptyUnsupported;
        }

        return new CodingAgentJavaScriptExtensionUnsupportedRegistrations(
            ReadInt(unsupported, "tools"),
            ReadInt(unsupported, "flags"),
            ReadInt(unsupported, "shortcuts"),
            ReadInt(unsupported, "handlers"),
            ReadInt(unsupported, "messageRenderers"),
            ReadInt(unsupported, "providers"));
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => false
        };
    }

    private static bool? ReadOptionalBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static CodingAgentJavaScriptExtensionUnsupportedRegistrations EmptyUnsupported { get; } = new(0, 0, 0, 0, 0, 0);

    private sealed record ProcessExecutionResult(
        bool Success,
        string ResultJson,
        string? Error)
    {
        public static ProcessExecutionResult Succeeded(string resultJson) => new(true, resultJson, null);

        public static ProcessExecutionResult Failed(string error) => new(false, string.Empty, error);
    }


}
