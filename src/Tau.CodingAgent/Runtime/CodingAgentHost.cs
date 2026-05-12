using System.Text.Json;
using Tau.Agent;
using Tau.Ai;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentHost
{
    private readonly InteractiveConsoleSession _ui;
    private readonly ICodingAgentRunner _runner;
    private readonly CodingAgentSessionStore? _sessionStore;
    private readonly CodingAgentTreeSessionController? _treeSessionController;
    private readonly CodingAgentPromptTemplateStore? _promptTemplateStore;
    private readonly CodingAgentSkillStore? _skillStore;
    private readonly CodingAgentExtensionCommandStore? _extensionCommandStore;
    private readonly CodingAgentAutoCompactionOptions _autoCompaction;
    private readonly CodingAgentCommandRouter _commandRouter;
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
        CodingAgentExtensionCommandStore? extensionCommandStore = null,
        CodingAgentAutoCompactionOptions? autoCompaction = null)
    {
        _ui = ui;
        _runner = runner;
        _sessionStore = sessionStore;
        _treeSessionController = treeSessionController;
        _promptTemplateStore = promptTemplateStore;
        _skillStore = skillStore;
        _extensionCommandStore = extensionCommandStore;
        _autoCompaction = autoCompaction ?? CodingAgentAutoCompactionOptions.Disabled;
        _commandRouter = new CodingAgentCommandRouter(
            runner,
            settingsStore,
            sessionStore?.Path,
            clipboard,
            treeSessionController,
            promptTemplateStore,
            skillStore,
            extensionCommandStore,
            _autoCompaction);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        _ui.ShowWelcome("Tau — Coding Agent", "Type your message, or 'exit' to quit.");

        while (!cancellationToken.IsCancellationRequested)
        {
            var input = await _ui.ReadInputAsync(cancellationToken).ConfigureAwait(false);
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
                _ui.WriteCancelled();
                continue;
            }

            await TryAutoCompactAsync(input, cancellationToken).ConfigureAwait(false);
            _ui.WriteUserMessage(input);

            try
            {
                await foreach (var evt in _runner.RunAsync(input, cancellationToken).ConfigureAwait(false))
                {
                    HandleEvent(evt);
                }
            }
            catch (OperationCanceledException)
            {
                _ui.WriteCancelled();
            }
            catch (Exception ex)
            {
                _ui.WriteRuntimeError(ex.Message);
            }
            finally
            {
                PersistSession();
            }
        }

        if (!_shutdownRendered)
        {
            _ui.WriteShutdown("Goodbye!");
        }

        return 0;
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
            _ui.WriteStatus(
                $"auto-compacted session: {result.MessagesBefore} -> {result.MessagesAfter} messages, estimated {estimatedTokens} tokens");
            PersistSession();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _ui.WriteRuntimeError($"auto-compaction failed: {ex.Message}");
        }
    }

    private void RenderCommandResult(CodingAgentCommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            if (result.IsError)
            {
                _ui.WriteRuntimeError(result.Message);
            }
            else if (result.ShouldExit)
            {
                _ui.WriteShutdown(result.Message);
                _shutdownRendered = true;
            }
            else
            {
                _ui.WriteStatus(result.Message);
            }
        }

        if (result.ShouldExit && string.IsNullOrWhiteSpace(result.Message))
        {
            _shutdownRendered = true;
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

    private static string GetSlashCommandName(string input)
    {
        var spaceIndex = input.IndexOf(' ');
        return spaceIndex < 0 ? input : input[..spaceIndex];
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
                _ui.WriteRuntimeError($"session save failed: {ex.Message}");
            }
        }

        try
        {
            _treeSessionController?.SyncFromRunner(_runner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _ui.WriteRuntimeError($"tree session save failed: {ex.Message}");
        }
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
                _ui.WriteRuntimeError(end.ErrorMessage);
                break;

            case AgentEndEvent:
                _ui.CompleteAssistantTurn();
                break;
        }
    }

    private enum SlashInvocationPreparation
    {
        None,
        Expanded,
        Handled
    }
}
