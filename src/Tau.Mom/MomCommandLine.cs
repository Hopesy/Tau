namespace Tau.Mom;

public sealed record MomCommandLineOptions(
    bool RunOnce,
    bool ValidateSandbox,
    bool ValidateSlack,
    bool DownloadRequested,
    string? DownloadChannelId,
    bool JsonOutput,
    string[] HostArgs)
{
    public bool HasDownload => DownloadRequested;
}

public static class MomCommandLine
{
    public static MomCommandLineOptions Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var runOnce = false;
        var validateSandbox = false;
        var validateSlack = false;
        var downloadRequested = false;
        string? downloadChannelId = null;
        var jsonOutput = false;
        var hostArgs = new List<string>();
        var sourceArgs = args.ToArray();

        for (var index = 0; index < sourceArgs.Length; index++)
        {
            var arg = sourceArgs[index];
            if (string.Equals(arg, "--once", StringComparison.OrdinalIgnoreCase))
            {
                runOnce = true;
                continue;
            }

            if (string.Equals(arg, "--validate-sandbox", StringComparison.OrdinalIgnoreCase))
            {
                validateSandbox = true;
                continue;
            }

            if (string.Equals(arg, "--validate-slack", StringComparison.OrdinalIgnoreCase))
            {
                validateSlack = true;
                continue;
            }

            if (arg.StartsWith("--download=", StringComparison.OrdinalIgnoreCase))
            {
                downloadRequested = true;
                downloadChannelId = arg["--download=".Length..];
                continue;
            }

            if (string.Equals(arg, "--download", StringComparison.OrdinalIgnoreCase))
            {
                downloadRequested = true;
                downloadChannelId = index + 1 < sourceArgs.Length ? sourceArgs[++index] : null;
                continue;
            }

            if (string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
            {
                jsonOutput = true;
                continue;
            }

            hostArgs.Add(arg);
        }

        return new MomCommandLineOptions(
            runOnce,
            validateSandbox,
            validateSlack,
            downloadRequested,
            string.IsNullOrWhiteSpace(downloadChannelId) ? null : downloadChannelId.Trim(),
            jsonOutput,
            [.. hostArgs]);
    }
}
