using System.Text.Json;
using Tau.Agent;
using Tau.Ai;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Observability;
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentHost
{
    private const string ContextOverflowCompactionInstructions =
        "Recover from a context overflow. Keep the current goal, decisions, blockers, changed files, and enough recent details to retry the pending user request.";

    private readonly InteractiveConsoleSession _ui;
    private readonly ICodingAgentRunner _runner;
    private readonly TuiCompositionSession? _compositionSession;
    private readonly CodingAgentSessionStore? _sessionStore;
    private readonly CodingAgentTreeSessionController? _treeSessionController;
    private readonly CodingAgentPromptTemplateStore? _promptTemplateStore;
    private readonly CodingAgentSkillStore? _skillStore;
    private readonly CodingAgentExtensionCommandStore? _extensionCommandStore;
    private readonly CodingAgentAutoCompactionOptions _autoCompactionBase;
    private CodingAgentAutoCompactionOptions _autoCompaction;
    private readonly CodingAgentCommandRouter _commandRouter;
    private readonly ICodingAgentTurnInputSource? _turnInputSource;
    private readonly IDisposable? _compositionBinding;
    private CodingAgentRetryOptions _retryOptions;
    private bool _shutdownRendered;

    public CodingAgentHost(
        InteractiveConsoleSession ui,
        ICodingAgentRunner runner,
        CodingAgentSessionStore? sessionStore = null,
        CodingAgentSettingsStore? settingsStore = null,
        ICodingAgentClipboard? clipboard = null,
        CodingAgentTreeSessionController? treeSessionController = null,
        CodingAgentPromptTemplateStore? promptTemplateStore = null,
        CodingAgentSkillStore? skillStore = null,
        CodingAgentContextFileStore? contextFileStore = null,
        CodingAgentThemeStore? themeStore = null,
        CodingAgentExtensionCommandStore? extensionCommandStore = null,
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
        TuiCompositionSession? compositionSession = null)
    {
        _ui = ui;
        _runner = runner;
        _compositionSession = compositionSession;
        _sessionStore = sessionStore;
        _treeSessionController = treeSessionController;
        _promptTemplateStore = promptTemplateStore;
        _skillStore = skillStore;
        _extensionCommandStore = extensionCommandStore;
        _autoCompactionBase = autoCompaction ?? CodingAgentAutoCompactionOptions.Disabled;
        _autoCompaction = _autoCompactionBase.WithEnabledOverride(autoCompactionEnabled);
        _retryOptions = retryOptions ?? CodingAgentRetryOptions.Disabled;
        _turnInputSource = turnInputSource;
        _compositionBinding = compositionSession?.BindTranscript(ui);
        RefreshCompositionStatus();
        _commandRouter = new CodingAgentCommandRouter(
            runner,
            settingsStore: settingsStore,
            sessionFile: sessionStore?.Path,
            clipboard: clipboard,
            treeSessionController: treeSessionController,
            promptTemplateStore: promptTemplateStore,
            skillStore: skillStore,
            contextFileStore: contextFileStore,
            themeStore: themeStore,
            extensionCommandStore: extensionCommandStore,
            changelogStore: changelogStore,
            autoCompaction: _autoCompactionBase,
            retryOptions: _retryOptions,
            retryOptionsChanged: options => _retryOptions = options,
            autoCompactionChanged: enabled => _autoCompaction = _autoCompactionBase.WithEnabledOverride(enabled),
            historySnapshotProvider: historySnapshotProvider,
            clearScreenAction: () => _ui.ClearScreen(),
            inputDraftSetter: draft => _ui.SetDraft(draft),
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
            reloadKeyBindings: reloadKeyBindings);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        _compositionSession?.Start();
        _ui.ShowWelcome("Tau — Coding Agent", "Type your message, or 'exit' to quit.");

        while (!cancellationToken.IsCancellationRequested)
        {
            var inputResult = await _ui.ReadInputResultAsync(cancellationToken).ConfigureAwait(false);
            if (inputResult.Kind == InputResultKind.Action)
            {
                if (await TryHandleEditorActionAsync(inputResult.Action, cancellationToken).ConfigureAwait(false))
                {
                    PersistSession();
                }

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

            var slashPreparation = TryPrepareSlashInvocation(input, out var preparedInput, out var handledSlashResult);
            if (handledSlashResult is not null)
            {
                RenderCommandResult(handledSlashResult);
                PersistSession();
                continue;
            }

            if (slashPreparation == SlashInvocationPreparation.Expanded)
            {
                input = preparedInput;
            }

            try
            {
                if (slashPreparation != SlashInvocationPreparation.Expanded
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

        _compositionSession?.Stop();
        return 0;
    }

    private async Task<bool> TryHandleEditorActionAsync(
        EditorAction action,
        CancellationToken cancellationToken)
    {
        var result = action switch
        {
            EditorAction.CycleModelForward => _commandRouter.CycleModel("forward"),
            EditorAction.CycleModelBackward => _commandRouter.CycleModel("backward"),
            EditorAction.SelectModel => await _commandRouter.SelectModelAsync(cancellationToken: cancellationToken).ConfigureAwait(false),
            _ => CodingAgentCommandResult.NotCommand
        };

        if (!result.Handled)
        {
            return false;
        }

        RenderCommandResult(result);
        return true;
    }

    private async Task<bool> TryHandleCommandAsync(string input, CancellationToken cancellationToken)
    {
        var result = await _commandRouter.TryHandleAsync(input, cancellationToken).ConfigureAwait(false);
        if (!result.Handled)
        {
            return false;
        }

        RenderCommandResult(result);
        return true;
    }

    private async Task TryAutoCompactAsync(string pendingInput, CancellationToken cancellationToken)
    {
        if (!_autoCompaction.IsEnabled || _runner.Messages.Count < 2)
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
            var result = await _runner
                .CompactAsync(_autoCompaction.Instructions, cancellationToken)
                .ConfigureAwait(false);
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
        var rollbackSnapshot = CreateRollbackSnapshot();
        var logContext = CreateTurnLogContext();
        var retryAttempt = 0;
        var overflowRecoveryAttempted = false;
        _ui.WriteUserMessage(input);

        while (true)
        {
            var result = await RunSingleTurnAttemptAsync(input, logContext, cancellationToken).ConfigureAwait(false);
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
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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
            var result = await _runner
                .CompactAsync(ContextOverflowCompactionInstructions, cancellationToken)
                .ConfigureAwait(false);
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

    private async Task<CodingAgentTurnAttemptResult> RunSingleTurnAttemptAsync(
        string input,
        TauRuntimeLogContext logContext,
        CancellationToken cancellationToken)
    {
        var errorAlreadyRendered = false;
        using var turnInputCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? turnInputTask = null;

        try
        {
            RefreshCompositionStatus("running");
            var events = _runner.RunAsync(input, logContext, cancellationToken);
            turnInputTask = StartTurnInputListener(turnInputCts.Token);

            await foreach (var evt in events.ConfigureAwait(false))
            {
                if (evt is AgentEndEvent { ErrorMessage: not null } end)
                {
                    HandleEvent(evt);
                    errorAlreadyRendered = true;
                    return CodingAgentTurnAttemptResult.Failed(end.ErrorMessage, errorAlreadyRendered);
                }

                HandleEvent(evt);
            }

            return CodingAgentTurnAttemptResult.Success();
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

    private Task? StartTurnInputListener(CancellationToken cancellationToken)
    {
        if (_turnInputSource is null)
        {
            return null;
        }

        return Task.Run(() => ConsumeTurnInputsAsync(cancellationToken), CancellationToken.None);
    }

    private async Task ConsumeTurnInputsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var turnInput in _turnInputSource!.ReadInputsAsync(cancellationToken).ConfigureAwait(false))
            {
                var text = turnInput.Text.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                if (turnInput.Kind == CodingAgentTurnInputKind.FollowUp)
                {
                    _runner.FollowUp(text);
                }
                else
                {
                    _runner.Steer(text);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal turn completion path.
        }
        catch (Exception ex)
        {
            WriteRuntimeError($"turn input listener failed: {ex.Message}");
        }
    }

    private void RenderCommandResult(CodingAgentCommandResult result)
    {
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

    private SlashInvocationPreparation TryPrepareSlashInvocation(
        string input,
        out string preparedInput,
        out CodingAgentCommandResult? handledResult)
    {
        preparedInput = input;
        handledResult = null;
        if (!input.StartsWith("/", StringComparison.Ordinal))
        {
            return SlashInvocationPreparation.None;
        }

        var command = GetSlashCommandName(input);
        if (CodingAgentCommandCatalog.IsSupported(command))
        {
            return SlashInvocationPreparation.None;
        }

        if (_extensionCommandStore?.TryInvoke(input, out var invocation) == true && invocation is not null)
        {
            if (invocation.SendToRunner)
            {
                preparedInput = invocation.Message;
                return SlashInvocationPreparation.Expanded;
            }

            handledResult = invocation.IsError
                ? CodingAgentCommandResult.Error(invocation.Message)
                : CodingAgentCommandResult.Status(invocation.Message);
            return SlashInvocationPreparation.Handled;
        }

        if (_skillStore?.TryExpand(input, out preparedInput, out _) == true)
        {
            return SlashInvocationPreparation.Expanded;
        }

        if (_promptTemplateStore?.TryExpand(input, out preparedInput, out _) != true)
        {
            return SlashInvocationPreparation.None;
        }

        return SlashInvocationPreparation.Expanded;
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

    private void PersistSession()
    {
        if (_sessionStore is not null)
        {
            try
            {
                _sessionStore.Save(_runner.Messages, _runner.Model, _runner.SessionName);
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

        RefreshCompositionStatus();
    }

    private void HandleEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case MessageUpdateEvent { StreamEvent: TextDeltaEvent delta }:
                _ui.WriteAssistantText(delta.Delta);
                break;

            case MessageUpdateEvent { StreamEvent: ThinkingDeltaEvent thinking }:
                _ui.WriteAssistantThinking(thinking.Delta);
                break;

            case ToolExecutionStartEvent toolStart:
                _ui.WriteToolStart(toolStart.ToolName);
                break;

            case ToolExecutionEndEvent toolEnd:
                _ui.WriteToolEnd(toolEnd.Result.IsError);
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

    private void RefreshCompositionStatus(string? statusLeftOverride = null)
    {
        if (_compositionSession is null)
        {
            return;
        }

        var left = string.IsNullOrWhiteSpace(statusLeftOverride)
            ? FormatCompositionStatusLeft()
            : statusLeftOverride;
        var right = $"{_runner.Model.Provider}/{_runner.Model.Id}";
        _compositionSession.SetStatus(left, right);
    }

    private string FormatCompositionStatusLeft() =>
        string.IsNullOrWhiteSpace(_runner.SessionName)
            ? "ready"
            : $"session: {_runner.SessionName}";

    private void WriteStatus(string message)
    {
        _ui.WriteStatus(message);
        RefreshCompositionStatus(message);
    }

    private void WriteRuntimeError(string message)
    {
        _ui.WriteRuntimeError(message);
        RefreshCompositionStatus($"error: {message}");
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
