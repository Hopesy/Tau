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
        return await RunCoreAsync(
                () => _runner.RunAsync(prompt, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> RunAsync(
        CodingAgentInitialPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (string.IsNullOrWhiteSpace(prompt.Text) && !prompt.HasImages)
        {
            throw new ArgumentException("Prompt content must not be empty.", nameof(prompt));
        }

        return await RunCoreAsync(
                () => prompt.HasImages
                    ? _runner.RunAsync(prompt.ToContentBlocks(), cancellationToken)
                    : _runner.RunAsync(prompt.Text, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> RunCoreAsync(
        Func<IAsyncEnumerable<AgentEvent>> run,
        CancellationToken cancellationToken)
    {
        try
        {
            string? errorMessage = null;
            var hasText = false;

            await foreach (var evt in run().ConfigureAwait(false))
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
