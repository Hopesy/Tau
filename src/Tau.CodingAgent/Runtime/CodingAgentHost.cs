using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Observability;
using Tau.CodingAgent.Tools;
using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentHost
{
    private const string ContextOverflowCompactionInstructions =
        "Recover from a context overflow. Keep the current goal, decisions, blockers, changed files, and enough recent details to retry the pending user request.";

    private readonly InteractiveConsoleSession _ui;
    private readonly ICodingAgentRunner _runner;
    private readonly TuiCompositionSession? _compositionSession;
    private readonly CodingAgentSessionStore? _sessionStore;
    private readonly CodingAgentSettingsStore? _settingsStore;
    private readonly CodingAgentTreeSessionController? _initialTreeSessionController;
    private CodingAgentTreeSessionController? _treeSessionController => _extensionCommandStore?.CurrentTreeSessionController ?? (_runner as RuntimeCodingAgentRunner)?.CurrentTreeSessionController ?? _initialTreeSessionController;
    private readonly CodingAgentPromptTemplateStore? _promptTemplateStore;
    private readonly CodingAgentSkillStore? _skillStore;
    private readonly CodingAgentExtensionCommandStore? _extensionCommandStore;
    private readonly CodingAgentFooterDataProvider? _footerDataProvider;
    private readonly TuiToolExecutionTheme _toolExecutionTheme;
    private readonly TuiBashExecutionTheme _bashExecutionTheme;
    private readonly bool _ownsFooterDataProvider;
    private readonly CodingAgentRpcExtensionUiBridge? _extensionUiBridge;
    private readonly CodingAgentAutoCompactionOptions _autoCompactionBase;
    private CodingAgentAutoCompactionOptions _autoCompaction;
    private readonly CodingAgentCommandRouter _commandRouter;
    private readonly ICodingAgentClipboard _clipboard;
    private readonly ICodingAgentExternalEditor _externalEditor;
    private readonly ICodingAgentTurnInputSource? _turnInputSource;
    private readonly CodingAgentInitialPrompt? _initialPrompt;
    private readonly IReadOnlyList<string> _initialMessages;
    private readonly CodingAgentStartupNoticeService? _startupNoticeService;
    private readonly Func<CancellationToken, Task<CodingAgentLatestRelease?>>? _versionUpdateChecker;
    private readonly IDisposable? _compositionBinding;
    private IReadOnlyDictionary<KeyBinding, CodingAgentExtensionShortcut> _extensionShortcuts =
        new Dictionary<KeyBinding, CodingAgentExtensionShortcut>();
    private readonly Dictionary<string, TuiToolExecution> _activeToolExecutions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TuiBashExecution> _activeBashExecutions = new(StringComparer.Ordinal);
    private readonly List<AgentCustomMessage> _pendingNextTurnCustomMessages = [];
    private CodingAgentRetryOptions _retryOptions;
    private bool _hideThinkingBlock;
    private bool _toolOutputExpanded;
    private bool _hiddenThinkingLabelRendered;
    private bool _shutdownRendered;
    private readonly object _eventRenderGate = new();

    public CodingAgentHost(
        InteractiveConsoleSession ui,
        ICodingAgentRunner runner,
        CodingAgentSessionStore? sessionStore = null,
        CodingAgentSettingsStore? settingsStore = null,
        ICodingAgentClipboard? clipboard = null,
        ICodingAgentExternalEditor? externalEditor = null,
        CodingAgentTreeSessionController? treeSessionController = null,
        CodingAgentPromptTemplateStore? promptTemplateStore = null,
        CodingAgentSkillStore? skillStore = null,
        CodingAgentContextFileStore? contextFileStore = null,
        CodingAgentThemeStore? themeStore = null,
        CodingAgentExtensionCommandStore? extensionCommandStore = null,
        CodingAgentPackageManager? packageManager = null,
        CodingAgentPackageResourceState? packageResourceState = null,
        CodingAgentChangelogStore? changelogStore = null,
        CodingAgentAutoCompactionOptions? autoCompaction = null,
        bool? autoCompactionEnabled = null,
        CodingAgentRetryOptions? retryOptions = null,
        ICodingAgentTurnInputSource? turnInputSource = null,
        Func<int, IReadOnlyList<string>>? historySnapshotProvider = null,
        Func<IReadOnlyList<CodingAgentTreeViewItem>, string?, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>>? treeNavigator = null,
        Func<CodingAgentThemeStatus, string?, CancellationToken, Task<string?>>? themeSelector = null,
        Func<CodingAgentSettingsSelectorState, CancellationToken, Task<string?>>? settingsSelector = null,
        Func<CodingAgentScopedModelsSelectorState, CancellationToken, Task<CodingAgentScopedModelsSelection>>? scopedModelsSelector = null,
        Func<CodingAgentAuthSelectorState, CancellationToken, Task<string?>>? authSelector = null,
        Func<CodingAgentThinkingSelectorState, CancellationToken, Task<string?>>? thinkingSelector = null,
        Func<CodingAgentModelSelectorState, CancellationToken, Task<string?>>? modelSelector = null,
        Func<CodingAgentResumeSelectorState, CancellationToken, Task<CodingAgentResumeSelectionResult>>? resumeSelector = null,
        Func<CodingAgentTreeMetadataSnapshot, CancellationToken, Task>? metadataViewer = null,
        Func<IOAuthLoginCallbacks>? oauthLoginCallbacksFactory = null,
        IKeyBindingMap? keyBindings = null,
        CodingAgentExtensionResourceState? extensionResourceState = null,
        Func<IKeyBindingMap?>? reloadKeyBindings = null,
        TuiCompositionSession? compositionSession = null,
        CodingAgentInitialPrompt? initialPrompt = null,
        IReadOnlyList<string>? initialMessages = null,
        CodingAgentStartupNoticeService? startupNoticeService = null,
        IReadOnlyList<string>? scopedModelsOverride = null,
        CodingAgentFooterDataProvider? footerDataProvider = null,
        Func<CancellationToken, Task<CodingAgentLatestRelease?>>? versionUpdateChecker = null,
        bool hideThinkingBlock = false,
        Func<Func<CodingAgentMcpMenu>, CancellationToken, Task<string?>>? mcpMenuSelector = null)
    {
        _ui = ui;
        _runner = runner;
        _compositionSession = compositionSession;
        _toolExecutionTheme = compositionSession is null
            ? new TuiToolExecutionTheme()
            : TuiToolExecutionTheme.Agent;
        _bashExecutionTheme = compositionSession is null
            ? new TuiBashExecutionTheme()
            : TuiBashExecutionTheme.Agent;
        _sessionStore = sessionStore;
        _settingsStore = settingsStore;
        _initialTreeSessionController = treeSessionController;
        _promptTemplateStore = promptTemplateStore;
        _skillStore = skillStore;
        if (runner is RuntimeCodingAgentRunner runtimeRunner) runtimeRunner.ConfigureInputResources(skillStore, promptTemplateStore);
        _extensionCommandStore = extensionCommandStore;
        _extensionCommandStore?.BindSession(runner, treeSessionController, sessionStore);
        _autoCompactionBase = autoCompaction ?? CodingAgentAutoCompactionOptions.Disabled;
        _autoCompaction = _autoCompactionBase.WithEnabledOverride(autoCompactionEnabled);
        if (_runner is RuntimeCodingAgentRunner sessionRunner)
        {
            sessionRunner.EnsureSessionContext(treeSessionController, sessionStore);
            sessionRunner.ConfigureScopedModelPatterns(scopedModelsOverride);
            if (settingsStore is not null) sessionRunner.ConfigureSessionSettings(settingsStore);
            sessionRunner.SetAutoCompactionEnabled(autoCompactionEnabled);
        }
        _retryOptions = retryOptions ?? (_runner as RuntimeCodingAgentRunner)?.RetryOptions ?? CodingAgentRetryOptions.Disabled;
        if (_runner is RuntimeCodingAgentRunner summaryRunner) summaryRunner.SummaryRetryOptions = _retryOptions;
        _hideThinkingBlock = hideThinkingBlock;
        _turnInputSource = turnInputSource;
        _initialPrompt = initialPrompt;
        _initialMessages = initialMessages ?? [];
        _startupNoticeService = startupNoticeService;
        _versionUpdateChecker = versionUpdateChecker;
        _compositionBinding = compositionSession?.BindTranscript(ui);
        _footerDataProvider = footerDataProvider ?? new CodingAgentFooterDataProvider(Environment.CurrentDirectory);
        _ownsFooterDataProvider = footerDataProvider is null;
        _footerDataProvider.SetAvailableProviderCount(CountAvailableProviders(runner, scopedModelsOverride));
        if (extensionCommandStore is not null)
        {
            _extensionUiBridge = new CodingAgentRpcExtensionUiBridge();
            _extensionUiBridge.SetFooterDataProvider(_footerDataProvider);
            _extensionUiBridge.Attach(static (_, _) => Task.CompletedTask);
            extensionCommandStore.SetExtensionUiBridge(_extensionUiBridge);
        }

        RefreshCompositionStatus();
        _clipboard = clipboard ?? new SystemCodingAgentClipboard();
        _externalEditor = externalEditor ?? new SystemCodingAgentExternalEditor();
        _commandRouter = new CodingAgentCommandRouter(
            runner,
            settingsStore: settingsStore,
            sessionFile: sessionStore?.Path,
            clipboard: _clipboard,
            treeSessionController: treeSessionController,
            promptTemplateStore: promptTemplateStore,
            skillStore: skillStore,
            contextFileStore: contextFileStore,
            themeStore: themeStore,
            extensionCommandStore: extensionCommandStore,
            packageManager: packageManager,
            packageResourceState: packageResourceState,
            changelogStore: changelogStore,
            autoCompaction: _autoCompactionBase,
            retryOptions: _retryOptions,
            retryOptionsChanged: options =>
            {
                _retryOptions = options;
                if (_runner is RuntimeCodingAgentRunner runtime) runtime.SummaryRetryOptions = options;
            },
            autoCompactionChanged: enabled =>
            {
                _autoCompaction = _autoCompactionBase.WithEnabledOverride(enabled);
                if (_runner is RuntimeCodingAgentRunner runtime) runtime.SetAutoCompactionEnabled(enabled);
            },
            hideThinkingBlockChanged: enabled => _hideThinkingBlock = enabled,
            historySnapshotProvider: historySnapshotProvider,
            clearScreenAction: () => _ui.ClearScreen(),
            inputDraftSetter: draft => _ui.SetDraft(draft),
            inputDraftGetter: () => _ui.GetDraft(),
            treeNavigationPrompt: PromptForTreeNavigationAsync,
            sessionSwitchPrompt: PromptForSessionSwitchAsync,
            treeLabelPrompt: PromptForTreeLabelAsync,
            treeNavigator: treeNavigator,
            themeSelector: themeSelector,
            settingsSelector: settingsSelector,
            scopedModelsSelector: scopedModelsSelector,
            authSelector: authSelector,
            thinkingSelector: thinkingSelector,
            modelSelector: modelSelector,
            resumeSelector: resumeSelector,
            metadataViewer: metadataViewer,
            oauthLoginCallbacksFactory: oauthLoginCallbacksFactory,
            keyBindings: keyBindings,
            extensionResourceState: extensionResourceState,
            reloadKeyBindings: reloadKeyBindings,
            scopedModelsOverride: scopedModelsOverride,
            mcpMenuSelector: mcpMenuSelector);
        RefreshExtensionShortcuts();
        _ui.SetInputShortcutHandler(TryHandleExtensionShortcutAsync);
    }

    /// <summary>【CodingAgent】【交互宿主】处理会话输入，在取消或退出时释放扩展进程</summary>
    /// <param name="cancellationToken">宿主取消信号</param>
    /// <returns>正常结束时返回零</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var background = _runner as RuntimeCodingAgentRunner;
        if (background is not null) background.BackgroundEvent += HandleBackgroundEventAsync;
        using var mcpNotificationsCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var mcpNotifications = Task.CompletedTask;
        using var extensionCancellation = cancellationToken.Register(() =>
        {
            _extensionCommandStore?.SetExtensionUiBridge(null);
            _extensionCommandStore?.ResetRuntime();
        });
        try
        {
            _compositionSession?.Start();
            var startupExtensionErrors = await PublishExtensionSessionStartAsync(cancellationToken).ConfigureAwait(false);
            _ui.ShowWelcome(
                "Tau — Coding Agent",
                "Type your message, or 'exit' to quit.",
                _footerDataProvider?.GetCustomHeaderLines());
            foreach (var error in startupExtensionErrors)
            {
                WriteRuntimeError($"extension {error.EventType} failed: {error.Error}");
            }

            ShowStartupNoticeIfNeeded();
            RefreshSessionHistory(initial: true);
            if (background?.McpService is { } mcp)
                mcpNotifications = mcp.PresentNotificationsAsync((notice, _) =>
                {
                    if (notice.Level == "error") WriteRuntimeError(notice.Message);
                    else WriteStatus(notice.Message);
                    return Task.CompletedTask;
                }, mcpNotificationsCancellation.Token);
            background?.StartBackgroundDelivery();
            StartVersionUpdateCheck(cancellationToken);
            await RunInitialInputsAsync(cancellationToken).ConfigureAwait(false);

            while (!_shutdownRendered && !cancellationToken.IsCancellationRequested && background?.IsShutdownRequested != true)
            {
                if (background is not null) await background.WaitForStateNotificationsAsync().ConfigureAwait(false);
                RefreshSessionHistory();
                var inputResult = await ReadInputOrShutdownAsync(background, cancellationToken).ConfigureAwait(false);
                if (inputResult.Kind == InputResultKind.Action)
                {
                    if (await TryHandleEditorActionAsync(inputResult.Action, cancellationToken).ConfigureAwait(false))
                    {
                        PersistSession();
                    }
                    if (_shutdownRendered) break;
                    continue;
                }

                var input = inputResult.Text;
                if (input is null || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(input))
                {
                    continue;
                }

                if (TryStartDirectBash(input, cancellationToken)) continue;

                var slashPreparation = await TryPrepareSlashInvocationAsync(input, cancellationToken)
                    .ConfigureAwait(false);
                if (slashPreparation.AlreadyHandled)
                {
                    PersistSession();
                    continue;
                }

                if (slashPreparation.HandledResult is not null)
                {
                    RenderCommandResult(slashPreparation.HandledResult);
                    PersistSession();
                    continue;
                }

                if (slashPreparation.Preparation == SlashInvocationPreparation.Expanded)
                {
                    input = slashPreparation.PreparedInput;
                }

                try
                {
                    if (slashPreparation.Preparation != SlashInvocationPreparation.Expanded
                        && await TryHandleCommandAsync(input, cancellationToken).ConfigureAwait(false))
                    {
                        PersistSession();
                        if (_shutdownRendered)
                        {
                            break;
                        }

                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    WriteCancelled();
                    continue;
                }

                await TryAutoCompactAsync(input, cancellationToken).ConfigureAwait(false);
                await RunTurnWithRetryAsync(input, cancellationToken).ConfigureAwait(false);
                PersistSession();
            }

            if (!_shutdownRendered)
            {
                WriteShutdown("Goodbye!");
            }

            return 0;
        }
        finally
        {
            await StopDirectBashAsync().ConfigureAwait(false);
            await mcpNotificationsCancellation.CancelAsync().ConfigureAwait(false);
            await mcpNotifications.ConfigureAwait(false);
            if (background is not null)
            {
                await background.StopBackgroundDeliveryAsync().ConfigureAwait(false);
                background.BackgroundEvent -= HandleBackgroundEventAsync;
            }
            _extensionCommandStore?.SetExtensionUiBridge(null);
            if (_extensionCommandStore is not null)
                await _extensionCommandStore.PublishSessionShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            PersistSession(refreshStatus: false);
            _extensionCommandStore?.ResetRuntime();
            _extensionUiBridge?.SetFooterDataProvider(null);
            _compositionSession?.Stop();
            if (_ownsFooterDataProvider)
            {
                _footerDataProvider?.Dispose();
            }
        }
    }

    /// <summary>【CodingAgent】【退出唤醒】空闲终端同时等待输入和扩展退出，只取消输入读取，不取消已完成的会话操作。</summary>
    /// <param name="runner">真实会话运行器。</param>
    /// <param name="token">宿主取消信号。</param>
    /// <returns>输入结果；主动退出时返回空输入。</returns>
    private async Task<InputResult> ReadInputOrShutdownAsync(RuntimeCodingAgentRunner? runner, CancellationToken token)
    {
        if (runner is null) return await _ui.ReadInputResultAsync(token).ConfigureAwait(false);
        using var readingCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var reading = _ui.ReadInputResultAsync(readingCancellation.Token);
        if (await Task.WhenAny(reading, runner.ShutdownRequested).ConfigureAwait(false) == reading)
            return await reading.ConfigureAwait(false);
        await readingCancellation.CancelAsync().ConfigureAwait(false);
        try { await reading.ConfigureAwait(false); }
        catch (OperationCanceledException) when (runner.IsShutdownRequested) { }
        return InputResult.Cancelled;
    }

    /// <summary>【CodingAgent】【后台显示】把扩展自动回合交给现有终端渲染和会话保存路径。</summary>
    /// <param name="evt">后台运行事件。</param>
    /// <param name="token">取消信号。</param>
    /// <returns>显示及保存完成的任务。</returns>
    private Task HandleBackgroundEventAsync(AgentEvent evt, CancellationToken token)
    {
        HandleEvent(evt);
        if (evt is MessageEndEvent or AgentEndEvent) PersistSession();
        return Task.CompletedTask;
    }

    /// <summary>【CodingAgent】【启动提示】按真实对话判断是否恢复会话，纯系统基线属于新会话。</summary>
    private void ShowStartupNoticeIfNeeded()
    {
        var notice = _startupNoticeService?.Prepare(_runner.Messages.Any(message => message is not SystemMessage));
        if (notice is null)
        {
            return;
        }

        _ui.WriteStatus(notice.Text);
    }

    private void StartVersionUpdateCheck(CancellationToken cancellationToken)
    {
        var checker = _versionUpdateChecker;
        if (checker is null)
        {
            return;
        }

        _ = ShowVersionUpdateIfAvailableAsync(checker, cancellationToken);
    }

    private async Task ShowVersionUpdateIfAvailableAsync(
        Func<CancellationToken, Task<CodingAgentLatestRelease?>> checker,
        CancellationToken cancellationToken)
    {
        try
        {
            var release = await checker(cancellationToken).ConfigureAwait(false);
            if (release is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            WriteStatus(CodingAgentVersionNotificationFormatter.Format(
                release,
                CodingAgentCliHelp.ResolveCommandName()));
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Version checks are startup polish only; provider/session work must not fail on them.
        }
    }

    private async Task RunInitialInputsAsync(CancellationToken cancellationToken)
    {
        if ((_runner as RuntimeCodingAgentRunner)?.IsShutdownRequested == true) return;
        if (_initialPrompt is not null)
        {
            await TryAutoCompactAsync(_initialPrompt.Text, cancellationToken).ConfigureAwait(false);
            await RunTurnWithRetryAsync(_initialPrompt, cancellationToken).ConfigureAwait(false);
            PersistSession();
        }

        foreach (var message in _initialMessages)
        {
            if (_shutdownRendered || (_runner as RuntimeCodingAgentRunner)?.IsShutdownRequested == true) return;
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            if (TryStartDirectBash(message, cancellationToken)) continue;

            var slashPreparation = await TryPrepareSlashInvocationAsync(message, cancellationToken)
                .ConfigureAwait(false);
            if (slashPreparation.AlreadyHandled)
            {
                PersistSession();
                if (_shutdownRendered)
                {
                    return;
                }

                continue;
            }

            if (slashPreparation.HandledResult is not null)
            {
                RenderCommandResult(slashPreparation.HandledResult);
                PersistSession();
                if (_shutdownRendered)
                {
                    return;
                }

                continue;
            }

            var input = slashPreparation.Preparation == SlashInvocationPreparation.Expanded
                ? slashPreparation.PreparedInput
                : message;
            await TryAutoCompactAsync(input, cancellationToken).ConfigureAwait(false);
            await RunTurnWithRetryAsync(input, cancellationToken).ConfigureAwait(false);
            PersistSession();
        }
    }

    /// <summary>【CodingAgent】【编辑器动作】处理输入区命名动作，并复用命令层的会话、思考及剪贴板行为。</summary>
    /// <param name="action">编辑器请求的动作。</param><param name="cancellationToken">取消信号。</param><returns>宿主是否处理动作。</returns>
    private async Task<bool> TryHandleEditorActionAsync(
        EditorAction action,
        CancellationToken cancellationToken)
    {
        if (action == EditorAction.ClearEditor) { HandleEditorClear(); return true; }
        if (action == EditorAction.ExitIfEmpty) { RenderCommandResult(CodingAgentCommandResult.Exit("Goodbye!")); return true; }
        if (action == EditorAction.Interrupt) { await HandleEditorInterruptAsync(cancellationToken).ConfigureAwait(false); return true; }
        var previousSessionId = (_runner as RuntimeCodingAgentRunner)?.SessionId;
        var result = action switch
        {
            EditorAction.CycleThinkingLevel => _runner.Model.Reasoning
                ? await _commandRouter.TryHandleAsync("/thinking cycle", cancellationToken).ConfigureAwait(false)
                : CodingAgentCommandResult.Status("Current model does not support thinking"),
            EditorAction.CopyLastMessage => await _commandRouter.TryHandleAsync("/copy", cancellationToken).ConfigureAwait(false),
            EditorAction.NewSession => await _commandRouter.TryHandleAsync("/new", cancellationToken).ConfigureAwait(false),
            EditorAction.OpenSessionTree => await _commandRouter.TryHandleAsync("/tree --interactive", cancellationToken).ConfigureAwait(false),
            EditorAction.ForkSession => await _commandRouter.TryHandleAsync("/fork", cancellationToken).ConfigureAwait(false),
            EditorAction.ResumeSession => await _commandRouter.TryHandleAsync("/resume", cancellationToken).ConfigureAwait(false),
            EditorAction.CycleModelForward => _commandRouter.CycleModel("forward"),
            EditorAction.CycleModelBackward => _commandRouter.CycleModel("backward"),
            EditorAction.SelectModel => await _commandRouter.SelectModelAsync(cancellationToken: cancellationToken).ConfigureAwait(false),
            EditorAction.PasteImage => await PasteClipboardAsync(cancellationToken).ConfigureAwait(false),
            EditorAction.ToggleThinkingBlock => ToggleThinkingBlockVisibility(),
            EditorAction.ToggleToolOutputExpansion => ToggleToolOutputExpansion(),
            EditorAction.OpenExternalEditor => await OpenExternalEditorAsync(cancellationToken).ConfigureAwait(false),
            EditorAction.QueueFollowUpMessage => await QueueFollowUpMessageAsync(cancellationToken).ConfigureAwait(false),
            EditorAction.RestoreQueuedMessages => RestoreQueuedMessagesToDraft(),
            _ => CodingAgentCommandResult.NotCommand
        };

        if (!result.Handled)
        {
            return false;
        }

        // 1. 【CodingAgent】【会话草稿】成功新建或恢复后释放旧草稿，取消或失败仍保留原内容
        if (action is EditorAction.NewSession or EditorAction.ResumeSession && previousSessionId is not null &&
            _runner is RuntimeCodingAgentRunner changedSession && changedSession.SessionId != previousSessionId)
            _ui.SetDraft(string.Empty);

        RenderCommandResult(result);
        return true;
    }

    private string? ToolOutputExpandKeyHint() =>
        CodingAgentKeyHintFormatter.KeyTextForAction(
            _ui.InputKeyBindings,
            EditorAction.ToggleToolOutputExpansion);

    /// <summary>【CodingAgent】【生成中显示】串行化工具显示切换与运行事件，避免在工具集合更新时枚举。</summary>
    /// <returns>显示切换结果。</returns>
    private CodingAgentCommandResult ToggleToolOutputExpansion()
    {
        lock (_eventRenderGate) return ToggleToolOutputExpansionCore();
    }

    /// <summary>【CodingAgent】【工具显示】在渲染锁内更新当前工具及自定义消息的展开状态。</summary>
    /// <returns>显示状态。</returns>
    private CodingAgentCommandResult ToggleToolOutputExpansionCore()
    {
        _toolOutputExpanded = !_toolOutputExpanded;
        foreach (var pair in _activeBashExecutions)
        {
            pair.Value.SetExpanded(_toolOutputExpanded);
            _ui.WriteToolComponent(pair.Value, key: pair.Key);
        }

        foreach (var pair in _activeToolExecutions)
        {
            pair.Value.SetExpanded(_toolOutputExpanded);
            _ui.WriteToolComponent(pair.Value, key: pair.Key);
        }
        foreach (var entry in _displayedCustomEntries.Values.ToArray()) RenderCustomEntry(entry);

        return CodingAgentCommandResult.Status($"tool output: {(_toolOutputExpanded ? "expanded" : "collapsed")}");
    }

    private CodingAgentCommandResult ToggleThinkingBlockVisibility()
    {
        _hideThinkingBlock = !_hideThinkingBlock;
        var current = _settingsStore?.Load();
        if (current is not null)
        {
            _settingsStore?.Save(current with { HideThinkingBlock = _hideThinkingBlock });
        }

        return CodingAgentCommandResult.Status($"thinking blocks: {(_hideThinkingBlock ? "hidden" : "visible")}");
    }

    private async Task<CodingAgentCommandResult> OpenExternalEditorAsync(CancellationToken cancellationToken)
    {
        var wasCompositionStarted = _compositionSession?.IsStarted == true;
        if (wasCompositionStarted)
        {
            _compositionSession?.Stop();
        }

        CodingAgentExternalEditorResult result;
        try
        {
            result = await _externalEditor.EditAsync(_ui.GetDraft(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (wasCompositionStarted)
            {
                _compositionSession?.Start();
                _compositionSession?.Render(force: true);
            }
        }

        if (!result.EditorConfigured)
        {
            return CodingAgentCommandResult.Error("No editor configured. Set $VISUAL or $EDITOR environment variable.");
        }

        if (!result.Edited)
        {
            return string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? CodingAgentCommandResult.Status("external editor cancelled")
                : CodingAgentCommandResult.Error(result.ErrorMessage);
        }

        _ui.SetDraft(result.Text ?? string.Empty);
        return CodingAgentCommandResult.Status("external editor updated draft");
    }

    private async Task<CodingAgentCommandResult> QueueFollowUpMessageAsync(CancellationToken cancellationToken)
    {
        var draft = _ui.GetDraft();
        if (string.IsNullOrWhiteSpace(draft))
        {
            return CodingAgentCommandResult.Status("follow-up: empty draft");
        }

        _ui.SetDraft(string.Empty);
        await TryAutoCompactAsync(draft, cancellationToken).ConfigureAwait(false);
        await RunTurnWithRetryAsync(draft, cancellationToken).ConfigureAwait(false);
        return CodingAgentCommandResult.Status("follow-up sent");
    }

    private CodingAgentCommandResult RestoreQueuedMessagesToDraft()
    {
        var queued = _runner.DrainQueuedMessages();
        var queuedText = queued.Steering.Concat(queued.FollowUp)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        if (queuedText.Length == 0)
        {
            return CodingAgentCommandResult.Status("No queued messages to restore");
        }

        var currentDraft = _ui.GetDraft();
        var restored = string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            queuedText.Append(currentDraft).Where(static text => !string.IsNullOrWhiteSpace(text)));
        _ui.SetDraft(restored);
        return CodingAgentCommandResult.Status(
            $"Restored {queuedText.Length} queued message{(queuedText.Length == 1 ? string.Empty : "s")} to editor");
    }

    private async Task<bool> TryHandleCommandAsync(string input, CancellationToken cancellationToken)
    {
        var result = await _commandRouter.TryHandleAsync(input, cancellationToken).ConfigureAwait(false);
        if (!result.Handled)
        {
            return false;
        }

        RenderCommandResult(result);
        if (!result.IsError && IsReloadCommand(input))
        {
            RefreshExtensionShortcuts();
        }

        return true;
    }

    private async Task<bool> TryHandleExtensionShortcutAsync(
        ConsoleKeyInfo key,
        CancellationToken cancellationToken)
    {
        if (_extensionCommandStore is null ||
            !_extensionShortcuts.TryGetValue(KeyBinding.From(key), out var shortcut))
        {
            return false;
        }

        if (!_extensionCommandStore.TryInvokeShortcut(shortcut, out var invocation, cancellationToken,
            preserveMessageActions: _runner is RuntimeCodingAgentRunner) || invocation is null)
        {
            return false;
        }

        if (invocation.IsError && invocation.MessageActions is null)
        {
            WriteRuntimeError(invocation.Message);
            return true;
        }

        if (_runner is RuntimeCodingAgentRunner runtime && invocation.MessageActions is { } actions)
        {
            await RunExtensionDeliveriesAsync(runtime, actions, cancellationToken).ConfigureAwait(false);
            if (invocation.IsError) WriteRuntimeError(invocation.Message);
            else if (!string.IsNullOrWhiteSpace(invocation.Message)) WriteStatus(invocation.Message);
            PersistSession();
            return true;
        }

        var handledByCustomMessage = await HandleExtensionCustomMessagesAsync(
                invocation.CustomMessageDeliveries,
                cancellationToken)
            .ConfigureAwait(false);
        if (!handledByCustomMessage)
        {
            RenderDisplayedMessages(invocation.DisplayMessages);
        }

        if (invocation.SendToRunner)
        {
            await TryAutoCompactAsync(invocation.Message, cancellationToken).ConfigureAwait(false);
            await RunTurnWithRetryAsync(invocation.Message, cancellationToken).ConfigureAwait(false);
            PersistSession();
            return true;
        }

        if (!string.IsNullOrWhiteSpace(invocation.Message))
        {
            WriteStatus(invocation.Message);
        }

        return true;
    }

    private void RefreshExtensionShortcuts()
    {
        RefreshMarkdownTransformers();
        if (_extensionCommandStore is null || _ui.InputKeyBindings is null)
        {
            _extensionShortcuts = new Dictionary<KeyBinding, CodingAgentExtensionShortcut>();
            return;
        }

        _extensionShortcuts = _extensionCommandStore
            .LoadResolvedShortcuts(_ui.InputKeyBindings)
            .ToDictionary(
                static resolved => resolved.KeyBinding,
                static resolved => resolved.Shortcut);
    }

    private Task<IReadOnlyList<CodingAgentExtensionLifecycleEventError>> PublishExtensionSessionStartAsync(
        CancellationToken cancellationToken)
    {
        return _extensionCommandStore is null
            ? Task.FromResult<IReadOnlyList<CodingAgentExtensionLifecycleEventError>>([])
            : _extensionCommandStore.EnsureSessionStartedAsync(cancellationToken);
    }

    private static int CountAvailableProviders(ICodingAgentRunner runner, IReadOnlyList<string>? scopedModelsOverride)
    {
        try
        {
            if (scopedModelsOverride is { Count: > 0 })
            {
                return scopedModelsOverride
                    .Select(static value => value.Split('/', 2)[0])
                    .Where(static provider => !string.IsNullOrWhiteSpace(provider))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
            }

            return runner.GetProviders().Count;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsReloadCommand(string input)
    {
        var trimmed = input.Trim();
        return trimmed.Equals("/reload", StringComparison.Ordinal) ||
               trimmed.StartsWith("/reload ", StringComparison.Ordinal);
    }

    /// <summary>【CodingAgent】【自动压缩】真实对话足够且超过阈值时生成摘要。</summary>
    /// <param name="pendingInput">即将执行的用户输入。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>检查和压缩任务。</returns>
    private async Task TryAutoCompactAsync(string pendingInput, CancellationToken cancellationToken)
    {
        if (_runner is RuntimeCodingAgentRunner) return;
        if (!_autoCompaction.IsEnabled || _runner.Messages.Count(message => message is not SystemMessage) < 2)
        {
            return;
        }

        var estimatedTokens = CodingAgentTokenEstimator.Estimate(_runner.Messages, pendingInput);
        if (estimatedTokens < _autoCompaction.ThresholdTokens)
        {
            return;
        }

        try
        {
            _treeSessionController?.SyncFromRunner(_runner);
            var result = await (_runner is RuntimeCodingAgentRunner runtime
                ? runtime.CompactForReasonAsync(_autoCompaction.Instructions, "threshold", false, cancellationToken)
                : _runner.CompactAsync(_autoCompaction.Instructions, cancellationToken)).ConfigureAwait(false);
            _treeSessionController?.RecordCompaction(_runner, result with { FromHook = true });
            var messagesAfter = _treeSessionController is null ? result.MessagesAfter : _runner.Messages.Count;
            WriteStatus(
                $"auto-compacted session: {result.MessagesBefore} -> {messagesAfter} messages, estimated {estimatedTokens} tokens");
            PersistSession();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            WriteRuntimeError($"auto-compaction failed: {ex.Message}");
        }
    }

    private async Task RunTurnWithRetryAsync(string input, CancellationToken cancellationToken)
    {
        var turnMessages = CreateTurnMessagesWithPendingCustomMessages(input);
        if (turnMessages is not null)
        {
            await RunTurnWithRetryAsync(
                    input,
                    (logContext, token) => RunSingleTurnAttemptAsync(turnMessages, logContext, token),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await RunTurnWithRetryAsync(
                input,
                (logContext, token) => RunSingleTurnAttemptAsync(input, logContext, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunTurnWithRetryAsync(
        CodingAgentInitialPrompt prompt,
        CancellationToken cancellationToken)
    {
        var displayText = prompt.Text;
        var turnMessages = CreateTurnMessagesWithPendingCustomMessages(prompt);
        if (turnMessages is not null)
        {
            await RunTurnWithRetryAsync(
                    displayText,
                    (logContext, token) => RunSingleTurnAttemptAsync(turnMessages, logContext, token),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await RunTurnWithRetryAsync(
                displayText,
                (logContext, token) => RunSingleTurnAttemptAsync(prompt, logContext, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunTurnWithRetryAsync(AgentCustomMessage input, CancellationToken cancellationToken)
    {
        await RunTurnWithRetryAsync(
                input.Display
                    ? [CodingAgentMessageDisplayFormatter.FormatCustomMessage(input)]
                    : [],
                (logContext, token) => RunSingleTurnAttemptAsync(input, logContext, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 将延迟到下一轮的 custom message 注入到普通文本 prompt 后面。
    /// </summary>
    /// <param name="input">用户输入文本。</param>
    /// <returns>存在待注入 custom message 时返回批量 prompt；否则返回 <see langword="null"/>。</returns>
    private IReadOnlyList<ChatMessage>? CreateTurnMessagesWithPendingCustomMessages(string input)
    {
        if (_pendingNextTurnCustomMessages.Count == 0)
        {
            return null;
        }

        var messages = new List<ChatMessage> { new UserMessage(input) };
        messages.AddRange(_pendingNextTurnCustomMessages);
        _pendingNextTurnCustomMessages.Clear();
        return messages.ToArray();
    }

    /// <summary>
    /// 将延迟到下一轮的 custom message 注入到图片/文本 prompt 后面。
    /// </summary>
    /// <param name="prompt">初始 prompt。</param>
    /// <returns>存在待注入 custom message 时返回批量 prompt；否则返回 <see langword="null"/>。</returns>
    private IReadOnlyList<ChatMessage>? CreateTurnMessagesWithPendingCustomMessages(CodingAgentInitialPrompt prompt)
    {
        if (_pendingNextTurnCustomMessages.Count == 0)
        {
            return null;
        }

        var messages = new List<ChatMessage>
        {
            prompt.HasImages
                ? new UserMessage(prompt.ToContentBlocks())
                : new UserMessage(prompt.Text)
        };
        messages.AddRange(_pendingNextTurnCustomMessages);
        _pendingNextTurnCustomMessages.Clear();
        return messages.ToArray();
    }

    private async Task RunTurnWithRetryAsync(
        string displayedInput,
        Func<TauRuntimeLogContext, CancellationToken, Task<CodingAgentTurnAttemptResult>> runAttempt,
        CancellationToken cancellationToken)
    {
        await RunTurnWithRetryAsync(
                CodingAgentMessageDisplayFormatter.FormatUserMessage(displayedInput),
                runAttempt,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunTurnWithRetryAsync(
        CodingAgentDisplayedMessage displayedInput,
        Func<TauRuntimeLogContext, CancellationToken, Task<CodingAgentTurnAttemptResult>> runAttempt,
        CancellationToken cancellationToken)
    {
        await RunTurnWithRetryAsync(
                [displayedInput],
                runAttempt,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunTurnWithRetryAsync(
        IReadOnlyList<CodingAgentDisplayedMessage> displayedInput,
        Func<TauRuntimeLogContext, CancellationToken, Task<CodingAgentTurnAttemptResult>> runAttempt,
        CancellationToken cancellationToken)
    {
        var rollbackSnapshot = CreateRollbackSnapshot();
        var logContext = CreateTurnLogContext();
        var retryAttempt = 0;
        var overflowRecoveryAttempted = false;
        RenderDisplayedMessages(displayedInput);

        // 1. 【CodingAgent】【统一恢复】真实运行器负责重试和记录省略，宿主完整消费所有后续事件
        if (_runner is RuntimeCodingAgentRunner)
        {
            var completed = await runAttempt(logContext, cancellationToken).ConfigureAwait(false);
            if (!completed.IsSuccess && !completed.IsCancelled && !completed.ErrorAlreadyRendered && completed.ErrorMessage is { } error)
                WriteRuntimeError(error);
            return;
        }

        while (true)
        {
            var result = await runAttempt(logContext, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                if (retryAttempt > 0)
                {
                    SyncTreeSessionAfterRecoveredRetry();
                    RecordAutoRetryEnd(success: true, retryAttempt);
                    var attemptLabel = retryAttempt == 1 ? "attempt" : "attempts";
                    WriteStatus($"auto-retry recovered after {retryAttempt} {attemptLabel}");
                }

                return;
            }

            if (result.IsCancelled)
            {
                RollbackTurn(rollbackSnapshot, "rolled back cancelled turn");
                return;
            }

            if (!overflowRecoveryAttempted && CodingAgentRetryClassifier.IsContextOverflow(result.ErrorMessage))
            {
                var recovery = await TryRecoverContextOverflowAsync(rollbackSnapshot, cancellationToken)
                    .ConfigureAwait(false);
                if (recovery.IsRecovered)
                {
                    rollbackSnapshot = recovery.RollbackSnapshot ?? CreateRollbackSnapshot();
                    overflowRecoveryAttempted = true;
                    continue;
                }

                if (recovery.IsCancelled)
                {
                    RollbackTurn(rollbackSnapshot, "rolled back cancelled turn");
                    return;
                }
            }

            var isRetryable = _retryOptions.IsEnabled
                && CodingAgentRetryClassifier.IsRetryable(result.ErrorMessage, _runner.Model.ContextWindow ?? 0);
            if (isRetryable && retryAttempt < _retryOptions.MaxAttempts)
            {
                retryAttempt++;
                if (!RestoreTurnSnapshot(rollbackSnapshot))
                {
                    return;
                }

                var delay = _retryOptions.GetDelay(retryAttempt);
                RecordAutoRetryStart(
                    retryAttempt,
                    _retryOptions.MaxAttempts,
                    (int)delay.TotalMilliseconds,
                    result.ErrorMessage);
                WriteStatus(
                    $"auto-retry {retryAttempt}/{_retryOptions.MaxAttempts} after {(int)delay.TotalMilliseconds}ms: {result.ErrorMessage}");

                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await DelayBeforeRetryAsync(
                                delay,
                                retryAttempt,
                                _retryOptions.MaxAttempts,
                                result.ErrorMessage ?? string.Empty,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        WriteCancelled();
                        WriteStatus("auto-retry cancelled during delay");
                        RecordAutoRetryEnd(success: false, retryAttempt, "Retry cancelled");
                        RollbackTurn(rollbackSnapshot, "rolled back cancelled turn");
                        return;
                    }
                }

                continue;
            }

            if (!result.ErrorAlreadyRendered && !string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                WriteRuntimeError(result.ErrorMessage);
            }

            if (_retryOptions.IsEnabled &&
                retryAttempt > 0 &&
                CodingAgentRetryClassifier.IsRetryable(result.ErrorMessage, _runner.Model.ContextWindow ?? 0))
            {
                RecordAutoRetryEnd(success: false, retryAttempt, result.ErrorMessage);
            }

            RollbackTurn(rollbackSnapshot, "rolled back failed turn");
            return;
        }
    }

    private async Task<CodingAgentTurnAttemptResult> RunSingleTurnAttemptAsync(
        AgentCustomMessage input,
        TauRuntimeLogContext logContext,
        CancellationToken cancellationToken)
    {
        var errorAlreadyRendered = false;
        string? finalError = null;
        using var turnInputCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? turnInputTask = null;

        try
        {
            RefreshCompositionStatus(GetRunningStatusText());
            var events = _runner.RunAsync(input, logContext, turnInputCts.Token);
            turnInputTask = StartTurnInputListener(turnInputCts, cancellationToken);

            await foreach (var evt in events.ConfigureAwait(false))
            {
                if (evt is AgentEndEvent end)
                {
                    finalError = end.ErrorMessage;
                    if (finalError is not null && _runner is not RuntimeCodingAgentRunner)
                    {
                        HandleEvent(evt);
                        return CodingAgentTurnAttemptResult.Failed(finalError, errorAlreadyRendered: true);
                    }
                    errorAlreadyRendered = finalError is not null;
                }
                HandleEvent(evt);
            }

            return finalError is null ? CodingAgentTurnAttemptResult.Success() : CodingAgentTurnAttemptResult.Failed(finalError, errorAlreadyRendered);
        }
        catch (OperationCanceledException)
        {
            WriteCancelled();
            return CodingAgentTurnAttemptResult.Cancelled();
        }
        catch (Exception ex)
        {
            return CodingAgentTurnAttemptResult.Failed(ex.Message, errorAlreadyRendered: false);
        }
        finally
        {
            turnInputCts.Cancel();
            if (turnInputTask is not null)
            {
                try
                {
                    await turnInputTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the turn finishes before the input listener does.
                }
            }
        }
    }

    private async Task<CodingAgentTurnAttemptResult> RunSingleTurnAttemptAsync(
        IReadOnlyList<ChatMessage> input,
        TauRuntimeLogContext logContext,
        CancellationToken cancellationToken)
    {
        var errorAlreadyRendered = false;
        string? finalError = null;
        using var turnInputCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? turnInputTask = null;

        try
        {
            RefreshCompositionStatus(GetRunningStatusText());
            var events = _runner.RunAsync(input, logContext, turnInputCts.Token);
            turnInputTask = StartTurnInputListener(turnInputCts, cancellationToken);

            await foreach (var evt in events.ConfigureAwait(false))
            {
                if (evt is AgentEndEvent end)
                {
                    finalError = end.ErrorMessage;
                    if (finalError is not null && _runner is not RuntimeCodingAgentRunner)
                    {
                        HandleEvent(evt);
                        return CodingAgentTurnAttemptResult.Failed(finalError, errorAlreadyRendered: true);
                    }
                    errorAlreadyRendered = finalError is not null;
                }
                HandleEvent(evt);
            }

            return finalError is null ? CodingAgentTurnAttemptResult.Success() : CodingAgentTurnAttemptResult.Failed(finalError, errorAlreadyRendered);
        }
        catch (OperationCanceledException)
        {
            WriteCancelled();
            return CodingAgentTurnAttemptResult.Cancelled();
        }
        catch (Exception ex)
        {
            return CodingAgentTurnAttemptResult.Failed(ex.Message, errorAlreadyRendered: false);
        }
        finally
        {
            turnInputCts.Cancel();
            if (turnInputTask is not null)
            {
                try
                {
                    await turnInputTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the turn finishes before the input listener does.
                }
            }
        }
    }

    /// <summary>
    /// 按扩展传入的投递选项处理 custom message。
    /// </summary>
    /// <param name="deliveries">扩展发送的 custom message 与投递选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>至少一条 custom message 已经触发 runner turn 时返回 <see langword="true"/>。</returns>
    private async Task<bool> HandleExtensionCustomMessagesAsync(
        IReadOnlyList<CodingAgentExtensionCustomMessageDelivery>? deliveries,
        CancellationToken cancellationToken)
    {
        if (deliveries is not { Count: > 0 })
        {
            return false;
        }

        var triggeredTurn = false;
        foreach (var delivery in deliveries)
        {
            if (await HandleExtensionCustomMessageAsync(delivery, cancellationToken).ConfigureAwait(false))
            {
                triggeredTurn = true;
            }
        }

        return triggeredTurn;
    }

    /// <summary>
    /// 在不能启动异步 turn 的预处理场景中处理不触发 turn 的 custom message。
    /// </summary>
    /// <param name="deliveries">扩展发送的 custom message 与投递选项。</param>
    private void HandleExtensionCustomMessagesWithoutTurn(
        IReadOnlyList<CodingAgentExtensionCustomMessageDelivery>? deliveries)
    {
        if (deliveries is not { Count: > 0 })
        {
            return;
        }

        foreach (var delivery in deliveries)
        {
            HandleExtensionCustomMessageWithoutTurn(delivery);
        }
    }

    /// <summary>
    /// 按单条 custom message 的投递选项执行 append、queue 或 trigger turn。
    /// </summary>
    /// <param name="delivery">custom message 与对应投递选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前消息已经触发 runner turn 时返回 <see langword="true"/>。</returns>
    private async Task<bool> HandleExtensionCustomMessageAsync(
        CodingAgentExtensionCustomMessageDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (delivery.DeliverAs == "nextTurn")
        {
            _pendingNextTurnCustomMessages.Add(delivery.Message);
            return false;
        }

        if (_runner.IsStreaming)
        {
            QueueStreamingCustomMessage(delivery);
            return false;
        }

        if (delivery.TriggerTurn)
        {
            await RunTurnWithRetryAsync(delivery.Message, cancellationToken).ConfigureAwait(false);
            PersistSession();
            return true;
        }

        if (HandleExtensionCustomMessageWithoutTurn(delivery))
        {
            return false;
        }

        _runner.AppendMessage(delivery.Message);
        PersistSession();
        await PublishCustomMessageLifecycleAsync(delivery.Message, cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// 为 append-only custom message 发布上游兼容的 message_start/message_end lifecycle events。
    /// </summary>
    /// <param name="message">已经追加到会话状态的 custom message。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task PublishCustomMessageLifecycleAsync(
        AgentCustomMessage message,
        CancellationToken cancellationToken)
    {
        await _runner.PublishLifecycleEventAsync(new MessageStartEvent(message), cancellationToken)
            .ConfigureAwait(false);
        await _runner.PublishLifecycleEventAsync(new MessageEndEvent(message), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 处理无需立即运行模型的 custom message 投递模式。
    /// </summary>
    /// <param name="delivery">custom message 与对应投递选项。</param>
    /// <returns>当前消息已经被处理且无需继续 append-only 逻辑时返回 <see langword="true"/>。</returns>
    private bool HandleExtensionCustomMessageWithoutTurn(CodingAgentExtensionCustomMessageDelivery delivery)
    {
        switch (delivery.DeliverAs)
        {
            case "nextTurn":
                _pendingNextTurnCustomMessages.Add(delivery.Message);
                return true;
            case "followUp":
                _runner.FollowUp(delivery.Message);
                return true;
            case "steer":
                _runner.Steer(delivery.Message);
                return true;
        }

        if (_runner.IsStreaming)
        {
            _runner.Steer(delivery.Message);
            return true;
        }

        return false;
    }

    private void QueueStreamingCustomMessage(CodingAgentExtensionCustomMessageDelivery delivery)
    {
        if (delivery.DeliverAs == "followUp")
        {
            _runner.FollowUp(delivery.Message);
            return;
        }

        _runner.Steer(delivery.Message);
    }

    private void RenderDisplayedMessages(IReadOnlyList<CodingAgentDisplayedMessage>? messages)
    {
        if (messages is not { Count: > 0 })
        {
            return;
        }

        foreach (var message in messages)
        {
            RenderDisplayedMessage(message);
        }
    }

    private void RenderDisplayedMessage(CodingAgentDisplayedMessage message)
    {
        switch (message.Kind)
        {
            case CodingAgentMessageDisplayFormatter.BranchSummaryKind:
                _ui.WriteBranchSummary(message.Text);
                break;
            case CodingAgentMessageDisplayFormatter.CompactionSummaryKind:
                _ui.WriteCompactionSummary(message.Text);
                break;
            case CodingAgentMessageDisplayFormatter.SkillKind:
                _ui.WriteSkillInvocation(message.Text);
                break;
            case CodingAgentMessageDisplayFormatter.CustomKind:
                _ui.WriteCustomMessage(message.Text);
                break;
            default:
                _ui.WriteUserMessage(message.Text);
                break;
        }
    }

    private async Task<CodingAgentTurnAttemptResult> RunSingleTurnAttemptAsync(
        CodingAgentInitialPrompt prompt,
        TauRuntimeLogContext logContext,
        CancellationToken cancellationToken)
    {
        var errorAlreadyRendered = false;
        string? finalError = null;
        using var turnInputCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? turnInputTask = null;

        try
        {
            RefreshCompositionStatus(GetRunningStatusText());
            var events = prompt.HasImages
                ? _runner.RunAsync(prompt.ToContentBlocks(), logContext, turnInputCts.Token)
                : _runner.RunAsync(prompt.Text, logContext, turnInputCts.Token);
            turnInputTask = StartTurnInputListener(turnInputCts, cancellationToken);

            await foreach (var evt in events.ConfigureAwait(false))
            {
                if (evt is AgentEndEvent end)
                {
                    finalError = end.ErrorMessage;
                    if (finalError is not null && _runner is not RuntimeCodingAgentRunner)
                    {
                        HandleEvent(evt);
                        return CodingAgentTurnAttemptResult.Failed(finalError, errorAlreadyRendered: true);
                    }
                    errorAlreadyRendered = finalError is not null;
                }
                HandleEvent(evt);
            }

            return finalError is null ? CodingAgentTurnAttemptResult.Success() : CodingAgentTurnAttemptResult.Failed(finalError, errorAlreadyRendered);
        }
        catch (OperationCanceledException)
        {
            WriteCancelled();
            return CodingAgentTurnAttemptResult.Cancelled();
        }
        catch (Exception ex)
        {
            return CodingAgentTurnAttemptResult.Failed(ex.Message, errorAlreadyRendered: false);
        }
        finally
        {
            turnInputCts.Cancel();
            if (turnInputTask is not null)
            {
                try
                {
                    await turnInputTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the turn finishes before the input listener does.
                }
            }
        }
    }

    private async Task<CodingAgentOverflowRecoveryResult> TryRecoverContextOverflowAsync(
        CodingAgentSessionSnapshot rollbackSnapshot,
        CancellationToken cancellationToken)
    {
        if (!RestoreTurnSnapshot(rollbackSnapshot))
        {
            return CodingAgentOverflowRecoveryResult.Failed();
        }

        try
        {
            _treeSessionController?.SyncFromRunner(_runner);
            var result = await (_runner is RuntimeCodingAgentRunner runtime
                ? runtime.CompactForReasonAsync(ContextOverflowCompactionInstructions, "overflow", true, cancellationToken)
                : _runner.CompactAsync(ContextOverflowCompactionInstructions, cancellationToken)).ConfigureAwait(false);
            _treeSessionController?.RecordCompaction(_runner, result with { FromHook = true });
            var messagesAfter = _treeSessionController is null ? result.MessagesAfter : _runner.Messages.Count;
            WriteStatus(
                $"context overflow compacted session: {result.MessagesBefore} -> {messagesAfter} messages; retrying turn");
            PersistSession();
            return CodingAgentOverflowRecoveryResult.Recovered(CreateRollbackSnapshot());
        }
        catch (OperationCanceledException)
        {
            WriteCancelled();
            RefreshCompositionStatus("[Cancelled]");
            return CodingAgentOverflowRecoveryResult.Cancelled();
        }
        catch (Exception ex)
        {
            WriteRuntimeError($"context overflow recovery failed: {ex.Message}");
            return CodingAgentOverflowRecoveryResult.Failed();
        }
    }

    private async Task DelayBeforeRetryAsync(
        TimeSpan delay,
        int retryAttempt,
        int maxAttempts,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        using var countdown = new CodingAgentCountdownTimer(
            delay,
            seconds => WriteStatus($"auto-retry {retryAttempt}/{maxAttempts} in {Math.Max(0, seconds)}s: {errorMessage}"),
            () => { });
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodingAgentTurnAttemptResult> RunSingleTurnAttemptAsync(
        string input,
        TauRuntimeLogContext logContext,
        CancellationToken cancellationToken)
    {
        var errorAlreadyRendered = false;
        string? finalError = null;
        using var turnInputCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? turnInputTask = null;

        try
        {
            RefreshCompositionStatus(GetRunningStatusText());
            var events = _runner.RunAsync(input, logContext, turnInputCts.Token);
            turnInputTask = StartTurnInputListener(turnInputCts, cancellationToken);

            await foreach (var evt in events.ConfigureAwait(false))
            {
                if (evt is AgentEndEvent end)
                {
                    finalError = end.ErrorMessage;
                    if (finalError is not null && _runner is not RuntimeCodingAgentRunner)
                    {
                        HandleEvent(evt);
                        return CodingAgentTurnAttemptResult.Failed(finalError, errorAlreadyRendered: true);
                    }
                    errorAlreadyRendered = finalError is not null;
                }
                HandleEvent(evt);
            }

            return finalError is null ? CodingAgentTurnAttemptResult.Success() : CodingAgentTurnAttemptResult.Failed(finalError, errorAlreadyRendered);
        }
        catch (OperationCanceledException)
        {
            WriteCancelled();
            return CodingAgentTurnAttemptResult.Cancelled();
        }
        catch (Exception ex)
        {
            return CodingAgentTurnAttemptResult.Failed(ex.Message, errorAlreadyRendered: false);
        }
        finally
        {
            turnInputCts.Cancel();
            if (turnInputTask is not null)
            {
                try
                {
                    await turnInputTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the turn finishes before the input listener does.
                }
            }
        }
    }

    private void RenderCommandResult(CodingAgentCommandResult result)
    {
        var rebuilt = RefreshSessionHistory();
        RenderDisplayedMessages(rebuilt ? result.DisplayMessages?.Where(message => message.Kind is not
            (CodingAgentMessageDisplayFormatter.CompactionSummaryKind or CodingAgentMessageDisplayFormatter.BranchSummaryKind)).ToArray() : result.DisplayMessages);

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            if (result.IsError)
            {
                WriteRuntimeError(result.Message);
            }
            else if (result.ShouldExit)
            {
                WriteShutdown(result.Message);
                _shutdownRendered = true;
            }
            else
            {
                WriteStatus(result.Message);
            }
        }

        if (result.ShouldExit && string.IsNullOrWhiteSpace(result.Message))
        {
            _shutdownRendered = true;
            WriteShutdown("Goodbye!");
        }
    }

    private async Task<CodingAgentSlashInvocationResult> TryPrepareSlashInvocationAsync(
        string input,
        CancellationToken cancellationToken)
    {
        if (!input.StartsWith("/", StringComparison.Ordinal))
        {
            return CodingAgentSlashInvocationResult.None(input);
        }

        var command = GetSlashCommandName(input);
        if (CodingAgentCommandCatalog.IsSupported(command))
        {
            return CodingAgentSlashInvocationResult.None(input);
        }

        if (_extensionCommandStore?.TryInvoke(input, out var invocation, cancellationToken,
            preserveMessageActions: _runner is RuntimeCodingAgentRunner) == true && invocation is not null)
        {
            if (_runner is RuntimeCodingAgentRunner runtime && invocation.MessageActions is { } actions)
            {
                await RunExtensionDeliveriesAsync(runtime, actions, cancellationToken).ConfigureAwait(false);
                if (invocation.IsError) WriteRuntimeError(invocation.Message);
                else if (!string.IsNullOrWhiteSpace(invocation.Message)) WriteStatus(invocation.Message);
                return CodingAgentSlashInvocationResult.Consumed(input);
            }
            if (invocation.SendToRunner)
            {
                var sendToRunnerCustomHandled = await HandleExtensionCustomMessagesAsync(
                        invocation.CustomMessageDeliveries,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!sendToRunnerCustomHandled)
                {
                    RenderDisplayedMessages(invocation.DisplayMessages);
                }

                return CodingAgentSlashInvocationResult.Expanded(invocation.Message);
            }

            var handledByCustomMessage = await HandleExtensionCustomMessagesAsync(
                    invocation.CustomMessageDeliveries,
                    cancellationToken)
                .ConfigureAwait(false);
            if (handledByCustomMessage)
            {
                return CodingAgentSlashInvocationResult.Consumed(input);
            }

            var handledResult = invocation.IsError
                ? CodingAgentCommandResult.Error(invocation.Message)
                : CodingAgentCommandResult.Status(invocation.Message, invocation.DisplayMessages ?? []);
            return CodingAgentSlashInvocationResult.Handled(input, handledResult);
        }

        // 1. 【CodingAgent】【输入顺序】真实运行器统一执行 input 钩子和技能模板展开，避免在钩子前提前展开
        if (_runner is RuntimeCodingAgentRunner) return CodingAgentSlashInvocationResult.Expanded(input);
        if (_skillStore?.TryExpand(input, out var preparedInput, out _) == true)
        {
            return CodingAgentSlashInvocationResult.Expanded(preparedInput);
        }

        if (_promptTemplateStore?.TryExpand(input, out preparedInput, out _) != true)
        {
            return CodingAgentSlashInvocationResult.None(input);
        }

        return CodingAgentSlashInvocationResult.Expanded(preparedInput);
    }

    /// <summary>【CodingAgent】【命令回合】投递完整消息期间保持终端输入监听，结束或取消时释放监听器。</summary>
    /// <param name="runtime">真实会话运行器。</param>
    /// <param name="actions">命令或快捷键生成的消息动作。</param>
    /// <param name="token">宿主取消信号。</param>
    /// <returns>消息投递及终端输入清理完成的任务。</returns>
    private async Task RunExtensionDeliveriesAsync(RuntimeCodingAgentRunner runtime,
        IReadOnlyList<CodingAgentExtensionMessageDelivery> actions, CancellationToken token)
    {
        using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var shouldListen = !runtime.IsStreaming && actions.Any(action => action.Message is UserMessage || action.TriggerTurn == true);
        var listener = shouldListen ? StartTurnInputListener(inputCancellation, token) : null;
        try
        {
            await foreach (var evt in runtime.DeliverExtensionMessagesAsync(actions, inputCancellation.Token).ConfigureAwait(false)) HandleEvent(evt);
        }
        catch (OperationCanceledException) { WriteCancelled(); }
        catch (Exception ex) { WriteRuntimeError(ex.Message); }
        finally
        {
            await inputCancellation.CancelAsync().ConfigureAwait(false);
            if (listener is not null)
                try { await listener.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            PersistSession();
        }
    }

    private CodingAgentSessionSnapshot CreateRollbackSnapshot() =>
        new([.. _runner.Messages], _runner.Model.Provider, _runner.Model.Id, _runner.SessionName);

    private TauRuntimeLogContext CreateTurnLogContext()
    {
        var turnId = Guid.NewGuid().ToString("N");
        return new TauRuntimeLogContext(
            CorrelationId: turnId,
            SessionId: TryGetTreeSessionId(),
            MessageId: turnId);
    }

    private string? TryGetTreeSessionId()
    {
        if (_treeSessionController is null)
        {
            return null;
        }

        try
        {
            return _treeSessionController.GetSummary().SessionId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private bool RestoreTurnSnapshot(CodingAgentSessionSnapshot snapshot)
    {
        try
        {
            _runner.RestoreSession(snapshot);
            return true;
        }
        catch (Exception ex)
        {
            WriteRuntimeError($"turn rollback failed: {ex.Message}");
            return false;
        }
    }

    private void SyncTreeSessionAfterRecoveredRetry()
    {
        if (_treeSessionController is null)
        {
            return;
        }

        try
        {
            _treeSessionController.SyncFromRunner(_runner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            WriteRuntimeError($"tree session save failed: {ex.Message}");
        }
    }

    private void RollbackTurn(CodingAgentSessionSnapshot snapshot, string message)
    {
        if (RestoreTurnSnapshot(snapshot))
        {
            WriteStatus(message);
        }
    }

    private void RecordAutoRetryStart(int attempt, int maxAttempts, int delayMs, string? errorMessage)
    {
        if (_treeSessionController is null)
        {
            return;
        }

        try
        {
            _treeSessionController.AppendAutoRetryStart(
                attempt,
                maxAttempts,
                delayMs,
                string.IsNullOrWhiteSpace(errorMessage) ? "Unknown error" : errorMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            WriteRuntimeError($"tree retry event save failed: {ex.Message}");
        }
    }

    private void RecordAutoRetryEnd(bool success, int attempt, string? finalError = null)
    {
        if (_treeSessionController is null)
        {
            return;
        }

        try
        {
            _treeSessionController.AppendAutoRetryEnd(success, attempt, finalError);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            WriteRuntimeError($"tree retry event save failed: {ex.Message}");
        }
    }

    private static string GetSlashCommandName(string input)
    {
        var spaceIndex = input.IndexOf(' ');
        return spaceIndex < 0 ? input : input[..spaceIndex];
    }

    private async Task<CodingAgentTreeNavigationDecision> PromptForTreeNavigationAsync(
        CodingAgentTreeNavigationPromptState state,
        CancellationToken cancellationToken)
    {
        return await PromptForNavigationDecisionAsync(
                FormatTreeNavigationPromptLabel(state),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CodingAgentTreeNavigationDecision> PromptForSessionSwitchAsync(
        CodingAgentSessionSwitchPromptState state,
        CancellationToken cancellationToken)
    {
        return await PromptForNavigationDecisionAsync(
                FormatSessionSwitchPromptLabel(state),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CodingAgentTreeNavigationDecision> PromptForNavigationDecisionAsync(
        string switchLabel,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            _ui.WriteStatus(
                $"{switchLabel}. [n] no summary, [s] summarize, [c] summarize with custom prompt, [q] cancel");

            var choice = await _ui.ReadInputAsync("switch-summary> ", ConsoleColor.Yellow, cancellationToken)
                .ConfigureAwait(false);
            if (choice is null)
            {
                return CodingAgentTreeNavigationDecision.CancelledDecision;
            }

            var normalized = choice.Trim().ToLowerInvariant();
            if (normalized.Length == 0 || normalized is "n" or "no" or "none")
            {
                return CodingAgentTreeNavigationDecision.NoSummary;
            }

            if (normalized is "q" or "quit" or "cancel")
            {
                return CodingAgentTreeNavigationDecision.CancelledDecision;
            }

            if (normalized is "s" or "sum" or "summary" or "summarize")
            {
                return CodingAgentTreeNavigationDecision.SummarizeWith();
            }

            if (normalized is "c" or "custom")
            {
                var customInstructions = await _ui.ReadInputAsync("summary-prompt> ", ConsoleColor.Yellow, cancellationToken)
                    .ConfigureAwait(false);
                if (customInstructions is null)
                {
                    continue;
                }

                return CodingAgentTreeNavigationDecision.SummarizeWith(customInstructions);
            }

            return CodingAgentTreeNavigationDecision.NoSummary;
        }
    }

    private static string FormatTreeNavigationPromptLabel(CodingAgentTreeNavigationPromptState state)
    {
        return state.Reason switch
        {
            CodingAgentTreeNavigationReason.NewSession => FormatSessionSwitchPromptLabel(
                new CodingAgentSessionSwitchPromptState(state.EntryCount, state.TokensBefore, state.Reason, state.TargetSessionPath)),
            CodingAgentTreeNavigationReason.ResumeSession => FormatSessionSwitchPromptLabel(
                new CodingAgentSessionSwitchPromptState(state.EntryCount, state.TokensBefore, state.Reason, state.TargetSessionPath)),
            CodingAgentTreeNavigationReason.ImportSession => FormatSessionSwitchPromptLabel(
                new CodingAgentSessionSwitchPromptState(state.EntryCount, state.TokensBefore, state.Reason, state.TargetSessionPath)),
            _ => $"tree switch leaves {state.EntryCount} entries / ~{state.TokensBefore} tokens behind"
        };
    }

    private static string FormatSessionSwitchPromptLabel(CodingAgentSessionSwitchPromptState state)
    {
        var label = state.Reason switch
        {
            CodingAgentTreeNavigationReason.NewSession => "new session",
            CodingAgentTreeNavigationReason.ResumeSession => FormatSessionSwitchLabel("resume", state.TargetSessionPath),
            CodingAgentTreeNavigationReason.ImportSession => FormatSessionSwitchLabel("import", state.TargetSessionPath),
            _ => "session switch"
        };
        return $"{label} leaves {state.EntryCount} entries / ~{state.TokensBefore} tokens behind";
    }

    private static string FormatSessionSwitchLabel(string verb, string? targetSessionPath)
    {
        var fileName = string.IsNullOrWhiteSpace(targetSessionPath)
            ? null
            : Path.GetFileName(targetSessionPath);
        return string.IsNullOrWhiteSpace(fileName)
            ? $"{verb} switch"
            : $"{verb} {fileName}";
    }

    private async Task<CodingAgentTreeLabelPromptResult> PromptForTreeLabelAsync(
        CodingAgentTreeLabelPromptState state,
        CancellationToken cancellationToken)
    {
        var currentLabel = string.IsNullOrWhiteSpace(state.CurrentLabel) ? "(none)" : state.CurrentLabel;
        _ui.WriteStatus(
            $"tree label {state.EntryId}: current {currentLabel}. Enter new label, empty/clear removes, cancel keeps current");

        var input = await _ui.ReadInputAsync("label> ", ConsoleColor.Yellow, cancellationToken)
            .ConfigureAwait(false);
        if (input is null)
        {
            return CodingAgentTreeLabelPromptResult.CancelledResult;
        }

        var normalized = input.Trim();
        if (normalized.Length == 0 || normalized.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            return CodingAgentTreeLabelPromptResult.Saved(null);
        }

        if (normalized.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("q", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("quit", StringComparison.OrdinalIgnoreCase))
        {
            return CodingAgentTreeLabelPromptResult.CancelledResult;
        }

        return CodingAgentTreeLabelPromptResult.Saved(normalized);
    }

    /// <summary>【CodingAgent】【会话保存】保存消息和树游标，退出收尾时保留终端已有的告别状态。</summary>
    /// <param name="refreshStatus">是否同时刷新终端状态栏。</param>
    private void PersistSession(bool refreshStatus = true)
    {
        if (_sessionStore is not null)
        {
            try
            {
                _sessionStore.Save(_runner.Messages, _runner.Model, _runner.SessionName, CodingAgentThinkingLevels.Format(_runner.ThinkingLevel));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                WriteRuntimeError($"session save failed: {ex.Message}");
            }
        }

        try
        {
            _treeSessionController?.SyncFromRunner(_runner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            WriteRuntimeError($"tree session save failed: {ex.Message}");
        }

        if (refreshStatus) RefreshCompositionStatus();
    }

    /// <summary>【CodingAgent】【事件渲染】将运行事件与流式输入显示动作串行化。</summary>
    /// <param name="evt">本次 Agent 事件。</param>
    private void HandleEvent(AgentEvent evt)
    {
        lock (_eventRenderGate) HandleEventCore(evt);
    }

    /// <summary>【CodingAgent】【事件应用】在渲染锁内更新终端、工具组件和会话显示状态。</summary>
    /// <param name="evt">Agent 事件。</param>
    private void HandleEventCore(AgentEvent evt)
    {
        switch (evt)
        {
            case CodingAgentQueueUpdateEvent:
                RefreshCompositionStatus();
                break;
            case CodingAgentEntryAppendedEvent appended when appended.Entry.TryGetProperty("kind", out var kind) && kind.GetString() == "cache_warm":
                if (_settingsStore?.GetShowCacheMissNotices() == true) WriteStatus(CodingAgentCacheWarmingFormatter.FormatUsage(appended.Entry));
                RefreshCompositionStatus();
                break;
            case CodingAgentEntryAppendedEvent appended when appended.Entry.TryGetProperty("type", out var entryType) && entryType.GetString() == "custom":
                if (_displayedHistoryRevision >= 0) RenderCustomEntry(appended.Entry);
                break;
            case CodingAgentAutoRetryStartEvent retry:
                WriteStatus($"auto-retry {retry.Attempt}/{retry.MaxAttempts} after {retry.DelayMs}ms: {retry.ErrorMessage}");
                break;

            case CodingAgentAutoRetryEndEvent retry:
                WriteStatus(retry.Success ? $"auto-retry recovered after {retry.Attempt} attempts" : retry.FinalError ?? "auto-retry failed");
                RefreshCompositionStatus();
                break;
            case CodingAgentSummaryRetryScheduledEvent retry:
                WriteStatus($"summary retry {retry.Attempt}/{retry.MaxAttempts} after {retry.DelayMs}ms: {retry.ErrorMessage}");
                break;

            case CodingAgentSummaryRetryAttemptEvent retry:
                RefreshCompositionStatus(retry.Source == "compaction" ? "Compacting..." : "Summarizing branch...");
                break;

            case CodingAgentSummaryRetryFinishedEvent:
                RefreshCompositionStatus();
                break;

            case CodingAgentCompactionStartEvent:
                RefreshCompositionStatus("Compacting...");
                break;

            case CodingAgentCompactionEndEvent:
                RefreshSessionHistory();
                RefreshCompositionStatus();
                break;

            case CodingAgentExtensionErrorEvent extensionError:
                WriteRuntimeError(extensionError.Error);
                break;

            case MessageEndEvent { Message: AgentCustomMessage { Display: true } custom }:
                RenderDisplayedMessage(_extensionCommandStore?.TryRenderCustomMessage(custom, out var rendered) == true && rendered is not null
                    ? rendered : CodingAgentMessageDisplayFormatter.FormatCustomMessage(custom));
                break;

            case MessageUpdateEvent { StreamEvent: TextDeltaEvent delta }:
                _hiddenThinkingLabelRendered = false;
                _ui.WriteAssistantText(delta.Delta);
                break;

            case MessageUpdateEvent { StreamEvent: ThinkingDeltaEvent thinking }:
                WriteAssistantThinking(thinking.Delta);
                break;

            case ToolExecutionStartEvent toolStart:
                HandleToolStart(toolStart);
                break;

            case ToolExecutionUpdateEvent toolUpdate:
                HandleToolUpdate(toolUpdate);
                break;

            case ToolExecutionEndEvent toolEnd:
                HandleToolEnd(toolEnd);
                break;

            case AgentEndEvent { WillRetry: true }:
                _ui.CompleteAssistantTurn();
                break;

            case MessageEndEvent { Message: AssistantMessage { StopReason: not (StopReason.Aborted or StopReason.Error) } }:
                if (_settingsStore?.GetShowCacheMissNotices() == true && _runner is RuntimeCodingAgentRunner cacheRunner &&
                    cacheRunner.GetLastCacheMiss() is { } miss && CodingAgentCacheStats.FormatNotice(miss) is { } notice)
                    WriteStatus(notice);
                break;

            case AgentEndEvent end when end.ErrorMessage is not null:
                WriteRuntimeError(end.ErrorMessage);
                break;

            case AgentEndEvent:
                _ui.CompleteAssistantTurn();
                RefreshCompositionStatus();
                break;
        }
    }

    /// <summary>【CodingAgent】【工具开始显示】创建顶层工具组件并选择宿主专用正文呈现。</summary><param name="toolStart">工具开始事件。</param>
    private void HandleToolStart(ToolExecutionStartEvent toolStart)
    {
        // 1. 【CodingAgent】【嵌套工具显示】子调用由父工具详情呈现，不再创建独立工具行
        if (toolStart.ParentToolCallId is not null) return;
        if (IsBashTool(toolStart.ToolName))
        {
            var bash = new TuiBashExecution(
                TryGetCommandFromArgs(toolStart.Args) ?? toolStart.ToolName,
                theme: _bashExecutionTheme);
            bash.SetExpanded(_toolOutputExpanded);
            bash.SetExpandKeyHint(ToolOutputExpandKeyHint());
            _activeBashExecutions[toolStart.ToolCallId] = bash;
            _ui.WriteToolComponent(bash, key: toolStart.ToolCallId);
            return;
        }

        var tool = new TuiToolExecution(
            toolStart.ToolName,
            toolStart.ToolCallId,
            toolStart.Args,
            _toolExecutionTheme, bodyRenderer: ToolBodyRenderer(toolStart.ToolName));
        tool.SetExpanded(_toolOutputExpanded);
        tool.SetExpandKeyHint(ToolOutputExpandKeyHint());
        tool.MarkExecutionStarted();
        tool.SetArgsComplete();
        _activeToolExecutions[toolStart.ToolCallId] = tool;
        _ui.WriteToolComponent(tool, key: toolStart.ToolCallId);
    }

    /// <summary>【CodingAgent】【工具进度显示】用最新结果快照更新顶层组件，嵌套事件由父组件负责显示。</summary><param name="toolUpdate">工具进度事件。</param>
    private void HandleToolUpdate(ToolExecutionUpdateEvent toolUpdate)
    {
        // 1. 【CodingAgent】【嵌套工具显示】保留事件供扩展和持久化使用，终端只更新父工具组件
        if (toolUpdate.ParentToolCallId is not null) return;
        if (_activeBashExecutions.TryGetValue(toolUpdate.ToolCallId, out var bash))
        {
            var output = !string.IsNullOrEmpty(toolUpdate.Update.Text)
                ? toolUpdate.Update.Text
                : ExtractText(toolUpdate.PartialResult);
            if (toolUpdate.Update.Details is ShellToolDetails || toolUpdate.PartialResult?.Details is ShellToolDetails) bash.SetOutput(output);
            else bash.AppendOutput(output);
            _ui.WriteToolComponent(bash, key: toolUpdate.ToolCallId);
            return;
        }

        if (!_activeToolExecutions.TryGetValue(toolUpdate.ToolCallId, out var tool))
        {
            tool = new TuiToolExecution(
                string.IsNullOrWhiteSpace(toolUpdate.ToolName) ? "tool" : toolUpdate.ToolName,
                toolUpdate.ToolCallId,
                toolUpdate.Args,
                _toolExecutionTheme, bodyRenderer: ToolBodyRenderer(toolUpdate.ToolName));
            tool.SetExpanded(_toolOutputExpanded);
            tool.MarkExecutionStarted();
            _activeToolExecutions[toolUpdate.ToolCallId] = tool;
        }

        if (!string.IsNullOrWhiteSpace(toolUpdate.Args))
        {
            tool.UpdateArgs(toolUpdate.Args);
        }

        if (toolUpdate.PartialResult is { } partialResult)
        {
            tool.UpdateResult(ToTuiToolExecutionResult(partialResult), isPartial: true);
        }
        else if (!string.IsNullOrEmpty(toolUpdate.Update.Text) || toolUpdate.Update.Content is not null)
        {
            tool.UpdateResult(
                ToTuiToolExecutionResult(
                    new ToolResult(
                        toolUpdate.Update.Content ?? [new TextContent(toolUpdate.Update.Text)],
                        toolUpdate.Update.IsError.GetValueOrDefault(),
                        toolUpdate.Update.Details,
                        toolUpdate.Update.Terminate.GetValueOrDefault())),
                isPartial: true);
        }

        _ui.WriteToolComponent(tool, key: toolUpdate.ToolCallId);
    }

    /// <summary>【CodingAgent】【工具结束显示】发布最终输出并移除活动组件，保留终端历史条目。</summary><param name="toolEnd">工具结束事件。</param>
    private void HandleToolEnd(ToolExecutionEndEvent toolEnd)
    {
        // 1. 【CodingAgent】【嵌套工具显示】结束事件也不创建兜底子工具行
        if (toolEnd.ParentToolCallId is not null) return;
        if (_activeBashExecutions.TryGetValue(toolEnd.ToolCallId, out var bash))
        {
            if (toolEnd.Result.StructuredContent is not null || toolEnd.Result.Details is ShellToolDetails || toolEnd.Result.IsError)
            {
                bash.SetOutput(ExtractText(toolEnd.Result));
            }
            else if (bash.OutputLines.Count == 0)
            {
                bash.AppendOutput(ExtractText(toolEnd.Result));
            }

            bash.SetComplete(
                TryGetExitCode(toolEnd.Result) ?? (toolEnd.Result.IsError ? 1 : 0),
                cancelled: toolEnd.Result.IsError && ExtractText(toolEnd.Result).EndsWith("Command aborted", StringComparison.Ordinal),
                truncated: (toolEnd.Result.Details as ShellToolDetails)?.Truncation?.Truncated == true,
                fullOutputPath: (toolEnd.Result.Details as ShellToolDetails)?.FullOutputPath);
            _ui.WriteToolComponent(bash, key: toolEnd.ToolCallId);
            _activeBashExecutions.Remove(toolEnd.ToolCallId);
            return;
        }

        if (!_activeToolExecutions.TryGetValue(toolEnd.ToolCallId, out var tool))
        {
            tool = new TuiToolExecution(
                string.IsNullOrWhiteSpace(toolEnd.ToolName) ? "tool" : toolEnd.ToolName,
                toolEnd.ToolCallId,
                theme: _toolExecutionTheme, bodyRenderer: ToolBodyRenderer(toolEnd.ToolName));
            tool.SetExpanded(_toolOutputExpanded);
        }

        tool.UpdateResult(ToTuiToolExecutionResult(toolEnd.Result));
        _ui.WriteToolComponent(tool, key: toolEnd.ToolCallId);
        _activeToolExecutions.Remove(toolEnd.ToolCallId);
    }

    /// <summary>【CodingAgent】【工具专用呈现】只为实际注册的内置脚本工具安装渲染器，不改变同名扩展工具。</summary>
    /// <param name="name">事件工具名。</param><returns>专用正文渲染器或空值。</returns>
    private Func<TuiToolExecutionRenderContext, IReadOnlyList<string>>? ToolBodyRenderer(string? name) =>
        name == "codemode" && _runner is RuntimeCodingAgentRunner runtime && runtime.GetRegisteredTools().Any(tool => tool is CodingAgentCodeModeTool)
            ? context => CodingAgentCodeModeRenderer.Render(context, _compositionSession is not null) : null;

    private static bool IsBashTool(string? toolName) =>
        string.Equals(toolName, "bash", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "powershell", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "shell", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetCommandFromArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(args);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("command", out var command) &&
                command.ValueKind == JsonValueKind.String)
            {
                return command.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return args;
    }

    private static TuiToolExecutionResult ToTuiToolExecutionResult(ToolResult result) =>
        new(
            result.Content.Select(ToTuiToolResultBlock).ToArray(),
            result.IsError,
            result.Details);

    private static TuiToolResultBlock ToTuiToolResultBlock(ContentBlock block) =>
        block switch
        {
            TextContent text => new TuiToolTextBlock(text.Text),
            ImageContent image => ToTuiToolImageBlock(image),
            _ => new TuiToolTextBlock($"[{block.Type}]")
        };

    private static TuiToolImageBlock ToTuiToolImageBlock(ImageContent image)
    {
        var capabilities = TuiTerminalImage.GetCapabilities();
        if (capabilities.Images == TuiImageProtocol.Kitty &&
            !image.MimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase) &&
            CodingAgentImageConverter.ConvertToPng(image.Data, image.MimeType) is { } converted)
        {
            return new TuiToolImageBlock(converted.Data, converted.MimeType);
        }

        return new TuiToolImageBlock(image.Data, image.MimeType);
    }

    private static string ExtractText(ToolResult? result)
    {
        if (result is null)
        {
            return string.Empty;
        }

        return string.Join('\n', result.Content.OfType<TextContent>().Select(static text => text.Text));
    }

    private static int? TryGetExitCode(ToolResult result)
    {
        if (result.StructuredContent is { ValueKind: JsonValueKind.Object } structured
            && structured.TryGetProperty("exit_code", out var code) && code.TryGetInt32(out var valueCode)) return valueCode;
        foreach (var line in ExtractText(result).Split('\n'))
        {
            var trimmed = line.Trim();
            const string prefix = "[exit code: ";
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal) || !trimmed.EndsWith(']'))
            {
                continue;
            }

            var value = trimmed[prefix.Length..^1];
            if (int.TryParse(value, out var exitCode))
            {
                return exitCode;
            }
        }

        return null;
    }

    private void RefreshCompositionStatus(string? statusLeftOverride = null)
    {
        if (_compositionSession is null)
        {
            return;
        }

        var hasStatusOverride = !string.IsNullOrWhiteSpace(statusLeftOverride);
        var stats = GetFooterSessionStats();
        if (!hasStatusOverride)
        {
            _compositionSession.SetStatusLines(CodingAgentFooterFormatter.FormatDefaultLines(
                _footerDataProvider?.GetCwd() ?? Environment.CurrentDirectory,
                GetHomeDirectory(),
                _runner.SessionName,
                _runner.Model,
                _runner.ThinkingLevel,
                _footerDataProvider,
                stats,
                (_runner as RuntimeCodingAgentRunner)?.AutoCompactionEnabled ?? _autoCompaction.IsEnabled));
            return;
        }

        var left = CodingAgentFooterFormatter.FormatLeft(statusLeftOverride!, _footerDataProvider);
        var right = CodingAgentFooterFormatter.FormatRight(
            _runner.Model,
            _runner.ThinkingLevel,
            _footerDataProvider,
            stats,
            (_runner as RuntimeCodingAgentRunner)?.AutoCompactionEnabled ?? _autoCompaction.IsEnabled);
        _compositionSession.SetStatus(left, right);
    }

    private string GetRunningStatusText() =>
        CodingAgentFooterFormatter.FormatWorkingStatus("running", _footerDataProvider);

    private string? GetHiddenThinkingLabel() =>
        CodingAgentFooterFormatter.SanitizeStatusText(_footerDataProvider?.GetHiddenThinkingLabel());

    /// <summary>【CodingAgent】【思考显示】根据可见性输出正文或固定标签，并保留实时/历史标记。</summary>
    /// <param name="delta">思考正文。</param><param name="isStreaming">是否为正在接收的响应。</param>
    private void WriteAssistantThinking(string delta, bool isStreaming = true)
    {
        if (!_hideThinkingBlock)
        {
            _ui.WriteAssistantThinking(delta, GetHiddenThinkingLabel(), isStreaming: isStreaming);
            return;
        }

        if (_hiddenThinkingLabelRendered)
        {
            return;
        }

        _hiddenThinkingLabelRendered = true;
        _ui.WriteAssistantThinking(
            CodingAgentMessageDisplayFormatter.DefaultHiddenThinkingLabel,
            GetHiddenThinkingLabel(), applyMarkdownTransform: false);
    }

    private CodingAgentSessionStats? GetFooterSessionStats()
    {
        try
        {
            var stats = _runner.GetSessionStats(_sessionStore?.Path);
            return _treeSessionController is null
                ? stats
                : stats.WithUsage(_treeSessionController.GetSessionUsageSummary());
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                InvalidOperationException)
        {
            return null;
        }
    }

    private static string? GetHomeDirectory()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return userProfile;
        }

        var specialFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(specialFolder) ? null : specialFolder;
    }

    private void WriteStatus(string message)
    {
        _ui.WriteStatus(message);
        RefreshCompositionStatus(message);
    }

    private void WriteRuntimeError(string message)
    {
        _ui.WriteRuntimeError(message);
        // 【终端错误】【消息区展示】错误已经由 transcript 渲染到输入区上方，底部状态栏只保留稳定的会话信息
        RefreshCompositionStatus();
    }

    private void WriteShutdown(string message)
    {
        _ui.WriteShutdown(message);
        RefreshCompositionStatus(message);
    }

    private void WriteCancelled()
    {
        _ui.WriteCancelled();
        RefreshCompositionStatus("[Cancelled]");
    }

    private enum SlashInvocationPreparation
    {
        None,
        Expanded,
        Handled
    }

    private sealed record CodingAgentSlashInvocationResult(
        SlashInvocationPreparation Preparation,
        string PreparedInput,
        CodingAgentCommandResult? HandledResult,
        bool AlreadyHandled)
    {
        public static CodingAgentSlashInvocationResult None(string input) =>
            new(SlashInvocationPreparation.None, input, null, false);

        public static CodingAgentSlashInvocationResult Expanded(string input) =>
            new(SlashInvocationPreparation.Expanded, input, null, false);

        public static CodingAgentSlashInvocationResult Handled(
            string input,
            CodingAgentCommandResult result) =>
            new(SlashInvocationPreparation.Handled, input, result, false);

        public static CodingAgentSlashInvocationResult Consumed(string input) =>
            new(SlashInvocationPreparation.Handled, input, null, true);
    }

    private sealed record CodingAgentTurnAttemptResult(
        bool IsSuccess,
        bool IsCancelled,
        string? ErrorMessage,
        bool ErrorAlreadyRendered)
    {
        public static CodingAgentTurnAttemptResult Success() => new(true, false, null, false);
        public static CodingAgentTurnAttemptResult Cancelled() => new(false, true, null, false);
        public static CodingAgentTurnAttemptResult Failed(string? errorMessage, bool errorAlreadyRendered) =>
            new(false, false, errorMessage, errorAlreadyRendered);
    }

    private sealed record CodingAgentOverflowRecoveryResult(
        bool IsRecovered,
        bool IsCancelled,
        CodingAgentSessionSnapshot? RollbackSnapshot)
    {
        public static CodingAgentOverflowRecoveryResult Recovered(CodingAgentSessionSnapshot rollbackSnapshot) =>
            new(true, false, rollbackSnapshot);

        public static CodingAgentOverflowRecoveryResult Cancelled() => new(false, true, null);
        public static CodingAgentOverflowRecoveryResult Failed() => new(false, false, null);
    }
}
