using System.Text.RegularExpressions;

namespace Tau.Ai;

public static class ContextOverflowDetector
{
    private static readonly string[] OverflowPatterns =
    [
        "prompt is too long",
        "request_too_large",
        "input is too long for requested model",
        "exceeds the context window",
        "input token count.*exceeds the maximum",
        "maximum prompt length is \\d+",
        "reduce the length of the messages",
        "maximum context length is \\d+ tokens",
        "exceeds the limit of \\d+",
        "exceeds the available context size",
        "greater than the context length",
        "context window exceeds limit",
        "exceeded model token limit",
        "too large for model with \\d+ maximum context length",
        "model_context_window_exceeded",
        "prompt too long; exceeded (?:max )?context length",
        "context window exceeded",
        "context[_ ]length[_ ]exceeded",
        "too many tokens",
        "token limit exceeded",
        "^4(?:00|13)\\s*(?:status code)?\\s*\\(no body\\)"
    ];

    private static readonly string[] NonOverflowPatterns =
    [
        "^(Throttling error|Service unavailable):",
        "rate limit",
        "too many requests"
    ];

    public static bool IsContextOverflow(AssistantMessage message, int? contextWindow = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.StopReason == StopReason.Error && IsContextOverflowError(message.ErrorMessage))
        {
            return true;
        }

        if (contextWindow is > 0 &&
            message.StopReason == StopReason.EndTurn &&
            message.Usage is { } usage)
        {
            var inputTokens = usage.InputTokens + (usage.CacheReadTokens ?? 0);
            return inputTokens > contextWindow;
        }

        return false;
    }

    public static bool IsContextOverflowError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        if (MatchesAny(errorMessage, NonOverflowPatterns))
        {
            return false;
        }

        return MatchesAny(errorMessage, OverflowPatterns);
    }

    public static IReadOnlyList<string> GetOverflowPatternSources() => [.. OverflowPatterns];

    private static bool MatchesAny(string value, IReadOnlyList<string> patterns) =>
        patterns.Any(pattern => Regex.IsMatch(
            value,
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
}
