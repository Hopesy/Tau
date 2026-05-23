namespace Tau.Mom;

public sealed record MomCommandLineOptions(
    bool RunOnce,
    bool ValidateSandbox,
    string[] HostArgs);

public static class MomCommandLine
{
    public static MomCommandLineOptions Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var runOnce = false;
        var validateSandbox = false;
        var hostArgs = new List<string>();

        foreach (var arg in args)
        {
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

            hostArgs.Add(arg);
        }

        return new MomCommandLineOptions(runOnce, validateSandbox, [.. hostArgs]);
    }
}
