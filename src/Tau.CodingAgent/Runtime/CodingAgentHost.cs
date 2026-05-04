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
    private readonly CodingAgentCommandRouter _commandRouter;
    private bool _shutdownRendered;

    public CodingAgentHost(
        InteractiveConsoleSession ui,
        ICodingAgentRunner runner,
        CodingAgentSessionStore? sessionStore = null,
        CodingAgentSettingsStore? settingsStore = null,
        ICodingAgentClipboard? clipboard = null)
    {
        _ui = ui;
        _runner = runner;
        _sessionStore = sessionStore;
        _commandRouter = new CodingAgentCommandRouter(runner, settingsStore, sessionStore?.Path, clipboard);
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

            try
            {
                if (await TryHandleCommandAsync(input, cancellationToken).ConfigureAwait(false))
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

        return true;
    }

    private void PersistSession()
    {
        if (_sessionStore is null)
        {
            return;
        }

        try
        {
            _sessionStore.Save(_runner.Messages, _runner.Model, _runner.SessionName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _ui.WriteRuntimeError($"session save failed: {ex.Message}");
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
}
