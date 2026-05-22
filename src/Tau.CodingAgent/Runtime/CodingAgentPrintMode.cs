using Tau.Agent;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentPrintMode
{
    private readonly ICodingAgentRunner _runner;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public CodingAgentPrintMode(ICodingAgentRunner runner, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        _runner = runner;
        _output = output;
        _error = error;
    }

    public async Task<int> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        try
        {
            string? errorMessage = null;
            var hasText = false;

            await foreach (var evt in _runner.RunAsync(prompt, cancellationToken).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case MessageUpdateEvent { StreamEvent: TextDeltaEvent delta }:
                        _output.Write(delta.Delta);
                        hasText = true;
                        break;
                    case AgentEndEvent { ErrorMessage: not null } end:
                        errorMessage = end.ErrorMessage;
                        break;
                }
            }

            if (hasText)
            {
                _output.WriteLine();
            }

            _output.Flush();

            if (errorMessage is not null)
            {
                _error.WriteLine(errorMessage);
                return 1;
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            _error.WriteLine("Cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            _error.WriteLine(ex.Message);
            return 1;
        }
    }
}
