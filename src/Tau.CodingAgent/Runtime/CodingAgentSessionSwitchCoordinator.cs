using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public enum CodingAgentTreeNavigationReason
{
    TreeNavigation,
    NewSession,
    ResumeSession,
    ImportSession
}

public enum CodingAgentSessionForkPosition
{
    Before,
    At
}

public sealed record CodingAgentSessionSwitchPromptState(
    int EntryCount,
    int TokensBefore,
    CodingAgentTreeNavigationReason Reason,
    string? TargetSessionPath);

public sealed record CodingAgentSessionSwitchHookState(
    CodingAgentTreeNavigationReason Reason,
    string? CurrentSessionPath,
    string? CurrentSessionName,
    string CurrentProvider,
    string CurrentModel,
    string? TargetSessionPath,
    CodingAgentResumeSessionInfo? TargetSession,
    int EntryCount,
    int TokensBefore);

public sealed record CodingAgentSessionSwitchHookResult(
    bool Cancelled,
    CodingAgentTreeNavigationDecision? Decision = null)
{
    public static CodingAgentSessionSwitchHookResult Continue(CodingAgentTreeNavigationDecision? decision = null) =>
        new(false, decision);

    public static CodingAgentSessionSwitchHookResult Cancel() =>
        new(true, null);
}

public sealed record CodingAgentSessionForkHookState(
    string? CurrentSessionPath,
    string? CurrentSessionName,
    string CurrentProvider,
    string CurrentModel,
    string EntryId,
    CodingAgentSessionForkPosition Position,
    string? SelectedText,
    int EntryCount);

public sealed record CodingAgentSessionForkHookResult(bool Cancelled)
{
    public static CodingAgentSessionForkHookResult Continue() => new(false);

    public static CodingAgentSessionForkHookResult Cancel() => new(true);
}

public readonly record struct CodingAgentSessionSwitchSummaryResult(
    bool Cancelled,
    bool Completed,
    CodingAgentTreeNavigationReason Reason,
    string? PreviousSessionPath,
    string? TargetSessionPath,
    bool UsedHook,
    bool UsedPrompt,
    CodingAgentTreeNavigationDecision? Decision,
    CodingAgentBranchSummaryResult? Summary)
{
    public bool SummarizedCurrentBranch => Summary is not null;
    public int? SummaryEntryCount => Summary?.EntryCount;
    public int? TokensBeforeSummary => Summary?.TokensBefore;

    public static CodingAgentSessionSwitchSummaryResult None(
        CodingAgentTreeNavigationReason reason,
        string? previousSessionPath,
        string? targetSessionPath,
        bool usedHook = false,
        bool usedPrompt = false,
        CodingAgentTreeNavigationDecision? decision = null) =>
        new(
            Cancelled: false,
            Completed: true,
            reason,
            NormalizePath(previousSessionPath),
            NormalizePath(targetSessionPath),
            usedHook,
            usedPrompt,
            decision,
            Summary: null);

    public static CodingAgentSessionSwitchSummaryResult CancelledResult(
        CodingAgentTreeNavigationReason reason,
        string? previousSessionPath,
        string? targetSessionPath,
        bool usedHook = false,
        bool usedPrompt = false,
        CodingAgentTreeNavigationDecision? decision = null) =>
        new(
            Cancelled: true,
            Completed: false,
            reason,
            NormalizePath(previousSessionPath),
            NormalizePath(targetSessionPath),
            usedHook,
            usedPrompt,
            decision,
            Summary: null);

    public static CodingAgentSessionSwitchSummaryResult CompletedResult(
        CodingAgentTreeNavigationReason reason,
        string? previousSessionPath,
        string? targetSessionPath,
        bool usedHook,
        bool usedPrompt,
        CodingAgentTreeNavigationDecision decision,
        CodingAgentBranchSummaryResult summary) =>
        new(
            Cancelled: false,
            Completed: true,
            reason,
            NormalizePath(previousSessionPath),
            NormalizePath(targetSessionPath),
            usedHook,
            usedPrompt,
            decision,
            summary);

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFullPath(path);
}

public sealed class CodingAgentSessionSwitchCoordinator
{
    private readonly ICodingAgentRunner _runner;
    private readonly CodingAgentTreeSessionController? _treeSessionController;
    private readonly Func<CodingAgentSessionSwitchPromptState, CancellationToken, Task<CodingAgentTreeNavigationDecision>>? _sessionSwitchPrompt;
    private readonly Func<CodingAgentSessionSwitchHookState, CancellationToken, Task<CodingAgentSessionSwitchHookResult?>>? _sessionSwitchHook;
    private readonly Func<CodingAgentSessionForkHookState, CancellationToken, Task<CodingAgentSessionForkHookResult?>>? _sessionForkHook;

    public CodingAgentSessionSwitchCoordinator(
        ICodingAgentRunner runner,
        CodingAgentTreeSessionController? treeSessionController,
        Func<CodingAgentSessionSwitchPromptState, CancellationToken, Task<CodingAgentTreeNavigationDecision>>? sessionSwitchPrompt = null,
        Func<CodingAgentSessionSwitchHookState, CancellationToken, Task<CodingAgentSessionSwitchHookResult?>>? sessionSwitchHook = null,
        Func<CodingAgentSessionForkHookState, CancellationToken, Task<CodingAgentSessionForkHookResult?>>? sessionForkHook = null)
    {
        _runner = runner;
        _treeSessionController = treeSessionController;
        _sessionSwitchPrompt = sessionSwitchPrompt;
        _sessionSwitchHook = sessionSwitchHook;
        _sessionForkHook = sessionForkHook;
    }

    public async Task<CodingAgentSessionForkHookResult> TryRunForkHookAsync(
        string entryId,
        CodingAgentSessionForkPosition position,
        string? selectedText,
        CancellationToken cancellationToken)
    {
        if (_sessionForkHook is null)
        {
            return CodingAgentSessionForkHookResult.Continue();
        }

        var entryCount = _treeSessionController?.GetSummary().EntryCount ?? 0;
        var result = await _sessionForkHook(
                new CodingAgentSessionForkHookState(
                    _treeSessionController?.Path,
                    _runner.SessionName,
                    _runner.Model.Provider,
                    _runner.Model.Id,
                    entryId,
                    position,
                    string.IsNullOrWhiteSpace(selectedText) ? null : selectedText,
                    entryCount),
                cancellationToken)
            .ConfigureAwait(false);

        return result ?? CodingAgentSessionForkHookResult.Continue();
    }

    public async Task<CodingAgentSessionSwitchSummaryResult> TrySummarizeBeforeSwitchAsync(
        CodingAgentTreeNavigationReason reason,
        string? targetSessionPath,
        CancellationToken cancellationToken)
    {
        var previousSessionPath = _treeSessionController?.Path;
        var summaryMessages = _treeSessionController?.CollectBranchSummaryMessages(null) ?? [];
        var estimate = summaryMessages.Count == 0 ? 0 : CodingAgentTokenEstimator.Estimate(summaryMessages);

        CodingAgentTreeNavigationDecision? decision = null;
        var usedHook = false;
        var usedPrompt = false;
        if (_sessionSwitchHook is not null)
        {
            usedHook = true;
            var targetSession = string.IsNullOrWhiteSpace(targetSessionPath)
                ? null
                : CodingAgentTreeSessionStore.TryGetResumeSessionInfo(targetSessionPath, _treeSessionController?.Path);
            var hookResult = await _sessionSwitchHook(
                    new CodingAgentSessionSwitchHookState(
                        reason,
                        _treeSessionController?.Path,
                        _runner.SessionName,
                        _runner.Model.Provider,
                        _runner.Model.Id,
                        targetSessionPath,
                        targetSession,
                        summaryMessages.Count,
                        estimate),
                    cancellationToken)
                .ConfigureAwait(false);
            if (hookResult?.Cancelled == true)
            {
                return CodingAgentSessionSwitchSummaryResult.CancelledResult(
                    reason,
                    previousSessionPath,
                    targetSessionPath,
                    usedHook,
                    usedPrompt);
            }

            decision = hookResult?.Decision;
        }

        if (decision is null)
        {
            if (summaryMessages.Count == 0 || _sessionSwitchPrompt is null)
            {
                return CodingAgentSessionSwitchSummaryResult.None(
                    reason,
                    previousSessionPath,
                    targetSessionPath,
                    usedHook,
                    usedPrompt);
            }

            usedPrompt = true;
            decision = await _sessionSwitchPrompt(
                    new CodingAgentSessionSwitchPromptState(
                        summaryMessages.Count,
                        estimate,
                        reason,
                        targetSessionPath),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (decision.Cancelled)
        {
            return CodingAgentSessionSwitchSummaryResult.CancelledResult(
                reason,
                previousSessionPath,
                targetSessionPath,
                usedHook,
                usedPrompt,
                decision);
        }

        if (!decision.Summarize || summaryMessages.Count == 0 || _treeSessionController is null)
        {
            return CodingAgentSessionSwitchSummaryResult.None(
                reason,
                previousSessionPath,
                targetSessionPath,
                usedHook,
                usedPrompt,
                decision);
        }

        var summary = await _runner
            .SummarizeBranchAsync(
                summaryMessages,
                decision.CustomInstructions,
                decision.ReplaceInstructions,
                cancellationToken)
            .ConfigureAwait(false);
        var snapshot = _treeSessionController.SummarizeCurrentBranchToRoot(
            _runner,
            summary,
            NormalizeTreeLabel(decision.Label));
        _runner.RestoreSession(snapshot.ToFlatSnapshot());
        return CodingAgentSessionSwitchSummaryResult.CompletedResult(
            reason,
            previousSessionPath,
            targetSessionPath,
            usedHook,
            usedPrompt,
            decision,
            summary);
    }

    public async Task<CodingAgentSessionSwitchSummaryResult> TrySummarizeBeforeRpcSwitchAsync(
        JsonElement command,
        CodingAgentTreeNavigationReason reason,
        string? targetSessionPath,
        CancellationToken cancellationToken)
    {
        var previousSessionPath = _treeSessionController?.Path;
        var summaryMessages = _treeSessionController?.CollectBranchSummaryMessages(null) ?? [];
        var estimate = summaryMessages.Count == 0 ? 0 : CodingAgentTokenEstimator.Estimate(summaryMessages);

        var usedHook = false;
        if (_sessionSwitchHook is not null)
        {
            usedHook = true;
            var targetSession = string.IsNullOrWhiteSpace(targetSessionPath)
                ? null
                : CodingAgentTreeSessionStore.TryGetResumeSessionInfo(targetSessionPath, _treeSessionController?.Path);
            var hookResult = await _sessionSwitchHook(
                    new CodingAgentSessionSwitchHookState(
                        reason,
                        _treeSessionController?.Path,
                        _runner.SessionName,
                        _runner.Model.Provider,
                        _runner.Model.Id,
                        targetSessionPath,
                        targetSession,
                        summaryMessages.Count,
                        estimate),
                    cancellationToken)
                .ConfigureAwait(false);
            if (hookResult?.Cancelled == true)
            {
                return CodingAgentSessionSwitchSummaryResult.CancelledResult(
                    reason,
                    previousSessionPath,
                    targetSessionPath,
                    usedHook);
            }

            if (hookResult?.Decision is { } decision)
            {
                if (decision.Cancelled)
                {
                    return CodingAgentSessionSwitchSummaryResult.CancelledResult(
                        reason,
                        previousSessionPath,
                        targetSessionPath,
                        usedHook,
                        decision: decision);
                }

                if (!decision.Summarize || summaryMessages.Count == 0 || _treeSessionController is null)
                {
                    return CodingAgentSessionSwitchSummaryResult.None(
                        reason,
                        previousSessionPath,
                        targetSessionPath,
                        usedHook,
                        decision: decision);
                }

                var hookSummary = await _runner
                    .SummarizeBranchAsync(
                        summaryMessages,
                        decision.CustomInstructions,
                        decision.ReplaceInstructions,
                        cancellationToken)
                    .ConfigureAwait(false);
                var hookSnapshot = _treeSessionController.SummarizeCurrentBranchToRoot(
                    _runner,
                    hookSummary,
                    NormalizeTreeLabel(decision.Label));
                _runner.RestoreSession(hookSnapshot.ToFlatSnapshot());
                return CodingAgentSessionSwitchSummaryResult.CompletedResult(
                    reason,
                    previousSessionPath,
                    targetSessionPath,
                    usedHook,
                    usedPrompt: false,
                    decision,
                    hookSummary);
            }
        }

        if (_treeSessionController is null || GetOptionalBoolean(command, "summarizeCurrentBranch") != true)
        {
            return CodingAgentSessionSwitchSummaryResult.None(
                reason,
                previousSessionPath,
                targetSessionPath,
                usedHook);
        }

        if (summaryMessages.Count == 0)
        {
            return CodingAgentSessionSwitchSummaryResult.None(
                reason,
                previousSessionPath,
                targetSessionPath,
                usedHook);
        }

        var rpcDecision = CodingAgentTreeNavigationDecision.SummarizeWith(
            GetOptionalString(command, "customInstructions"),
            GetOptionalBoolean(command, "replaceInstructions") ?? false);
        var summary = await _runner
            .SummarizeBranchAsync(
                summaryMessages,
                rpcDecision.CustomInstructions,
                rpcDecision.ReplaceInstructions,
                cancellationToken)
            .ConfigureAwait(false);
        var snapshot = _treeSessionController.SummarizeCurrentBranchToRoot(_runner, summary);
        _runner.RestoreSession(snapshot.ToFlatSnapshot());
        return CodingAgentSessionSwitchSummaryResult.CompletedResult(
            reason,
            previousSessionPath,
            targetSessionPath,
            usedHook,
            usedPrompt: false,
            rpcDecision,
            summary);
    }

    private static string? GetOptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null
        };
    }

    private static bool? GetOptionalBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"RPC command property '{name}' must be boolean when provided.");
        }

        return value.GetBoolean();
    }

    private static string? NormalizeTreeLabel(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim();
}
