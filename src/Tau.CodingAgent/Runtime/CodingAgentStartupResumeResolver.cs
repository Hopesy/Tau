namespace Tau.CodingAgent.Runtime;

internal sealed record CodingAgentStartupResumeResult(
    string? SelectedPath,
    int? ExitCode,
    string? Message,
    bool IsError)
{
    public static CodingAgentStartupResumeResult Continue(string? selectedPath) =>
        new(selectedPath, null, null, false);

    public static CodingAgentStartupResumeResult Exit(int exitCode, string message, bool isError = false) =>
        new(null, exitCode, message, isError);
}

internal static class CodingAgentStartupResumeResolver
{
    public static async Task<CodingAgentStartupResumeResult> ResolveAsync(
        bool resume,
        string? explicitSessionPath,
        bool printMode,
        bool rpcMode,
        string? sessionDirectory,
        string? currentSessionPath,
        Func<CodingAgentResumeSelectorState, CancellationToken, Task<CodingAgentResumeSelectionResult>>? selector,
        CancellationToken cancellationToken = default)
    {
        if (!resume || !string.IsNullOrWhiteSpace(explicitSessionPath))
        {
            return CodingAgentStartupResumeResult.Continue(null);
        }

        if (printMode || rpcMode || selector is null)
        {
            return CodingAgentStartupResumeResult.Exit(
                1,
                "error: --resume requires an interactive terminal; use --session <path> or --continue.",
                isError: true);
        }

        var normalizedCurrentPath = string.IsNullOrWhiteSpace(currentSessionPath)
            ? CodingAgentTreeSessionStore.GetDefaultPath()
            : currentSessionPath;
        var sessions = CodingAgentSessionTarget.ListAvailableSessions(sessionDirectory, normalizedCurrentPath);
        if (sessions.Count == 0)
        {
            return CodingAgentStartupResumeResult.Exit(0, "No session selected");
        }

        var selection = await selector(
                new CodingAgentResumeSelectorState(
                    normalizedCurrentPath,
                    Environment.CurrentDirectory,
                    sessions),
                cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(selection.SelectedPath)
            ? CodingAgentStartupResumeResult.Exit(0, "No session selected")
            : CodingAgentStartupResumeResult.Continue(selection.SelectedPath);
    }
}
