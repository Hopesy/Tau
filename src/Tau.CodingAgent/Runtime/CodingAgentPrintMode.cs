using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentPrintMode
{
    private readonly ICodingAgentRunner _runner;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly bool _jsonMode;

    public CodingAgentPrintMode(ICodingAgentRunner runner, TextWriter output, TextWriter error)
        : this(runner, output, error, jsonMode: false)
    {
    }

    public CodingAgentPrintMode(ICodingAgentRunner runner, TextWriter output, TextWriter error, bool jsonMode)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        _runner = runner;
        _output = output;
        _error = error;
        _jsonMode = jsonMode;
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
                if (_jsonMode)
                {
                    // Mirrors upstream print-mode.ts `--mode json`: emit every agent event as a
                    // JSON line using the same schema the RPC mode produces.
                    _output.WriteLine(CodingAgentRpcHost.SerializeEventLine(evt));
                    if (evt is AgentEndEvent { ErrorMessage: not null } jsonEnd)
                    {
                        errorMessage = jsonEnd.ErrorMessage;
                    }

                    continue;
                }

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

            if (!_jsonMode && hasText)
            {
                _output.WriteLine();
            }

            _output.Flush();

            if (errorMessage is not null)
            {
                // Upstream json mode keeps the error visible on the event stream but still exits
                // non-zero; the human-readable message stays on stderr only for text mode.
                if (!_jsonMode)
                {
                    _error.WriteLine(errorMessage);
                }

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
