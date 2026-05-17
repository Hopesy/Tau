using System.Diagnostics;
using System.Text;

namespace Tau.Ai.Providers.Bedrock;

internal interface IBedrockProcessRunner
{
    Task<BedrockProcessResult> RunAsync(BedrockProcessRequest request, CancellationToken cancellationToken);
}

internal sealed record BedrockProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout);

internal sealed record BedrockProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

internal sealed class DefaultBedrockProcessRunner : IBedrockProcessRunner
{
    public static DefaultBedrockProcessRunner Instance { get; } = new();

    public async Task<BedrockProcessResult> RunAsync(BedrockProcessRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stdoutBuilder.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stderrBuilder.AppendLine(args.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(request.Timeout);

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // best-effort kill; ignore failures
            }

            return new BedrockProcessResult(
                ExitCode: -1,
                StandardOutput: stdoutBuilder.ToString(),
                StandardError: stderrBuilder.ToString(),
                TimedOut: true);
        }

        return new BedrockProcessResult(
            ExitCode: process.ExitCode,
            StandardOutput: stdoutBuilder.ToString(),
            StandardError: stderrBuilder.ToString(),
            TimedOut: false);
    }
}
