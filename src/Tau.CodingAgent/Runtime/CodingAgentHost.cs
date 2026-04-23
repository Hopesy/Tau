using Tau.Agent;
using Tau.Ai;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentHost
{
    private readonly InteractiveConsoleSession _ui;
    private readonly ICodingAgentRunner _runner;

    public CodingAgentHost(InteractiveConsoleSession ui, ICodingAgentRunner runner)
    {
        _ui = ui;
        _runner = runner;
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
        }

        _ui.WriteShutdown("Goodbye!");
        return 0;
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
