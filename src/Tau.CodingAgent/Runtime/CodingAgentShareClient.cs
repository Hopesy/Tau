using System.ComponentModel;
using System.Diagnostics;

namespace Tau.CodingAgent.Runtime;

public interface ICodingAgentShareClient
{
    Task<CodingAgentShareResult> ShareAsync(string htmlPath, CancellationToken cancellationToken = default);
}

public sealed record CodingAgentShareResult(string GistUrl, string GistId, string ShareUrl);

public sealed class GitHubCliCodingAgentShareClient : ICodingAgentShareClient
{
    public const string ShareViewerUrlEnvironmentVariable = "TAU_SHARE_VIEWER_URL";

    private const string DefaultShareViewerUrl = "https://pi.dev/session/";

    public async Task<CodingAgentShareResult> ShareAsync(
        string htmlPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(htmlPath) || !File.Exists(htmlPath))
        {
            throw new FileNotFoundException("HTML transcript not found.", htmlPath);
        }

        var auth = await RunProcessAsync("gh", ["auth", "status"], cancellationToken).ConfigureAwait(false);
        if (auth.StartFailed)
        {
            throw new InvalidOperationException("GitHub CLI (gh) is not installed. Install it from https://cli.github.com/");
        }

        if (auth.ExitCode != 0)
        {
            throw new InvalidOperationException("GitHub CLI is not logged in. Run 'gh auth login' first.");
        }

        var create = await RunProcessAsync(
                "gh",
                ["gist", "create", "--public=false", htmlPath],
                cancellationToken)
            .ConfigureAwait(false);
        if (create.StartFailed)
        {
            throw new InvalidOperationException("GitHub CLI (gh) is not installed. Install it from https://cli.github.com/");
        }

        if (create.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(create.StandardError)
                ? "Unknown error"
                : create.StandardError.Trim();
            throw new InvalidOperationException($"Failed to create gist: {error}");
        }

        var gistUrl = create.StandardOutput.Trim();
        var gistId = ExtractGistId(gistUrl);
        if (string.IsNullOrWhiteSpace(gistId))
        {
            throw new InvalidOperationException("Failed to parse gist ID from gh output.");
        }

        return new CodingAgentShareResult(gistUrl, gistId, GetShareViewerUrl(gistId));
    }

    public static string GetShareViewerUrl(string gistId)
    {
        var baseUrl = Environment.GetEnvironmentVariable(ShareViewerUrlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = DefaultShareViewerUrl;
        }

        return baseUrl.Contains('#')
            ? $"{baseUrl}{gistId}"
            : $"{baseUrl}#{gistId}";
    }

    private static string ExtractGistId(string gistUrl)
    {
        if (string.IsNullOrWhiteSpace(gistUrl))
        {
            return string.Empty;
        }

        var trimmed = gistUrl.Trim().TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    private static async Task<CodingAgentProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo =
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return new CodingAgentProcessResult(null, string.Empty, string.Empty, StartFailed: true);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new CodingAgentProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private sealed record CodingAgentProcessResult(
        int? ExitCode,
        string StandardOutput,
        string StandardError,
        bool StartFailed = false);
}
