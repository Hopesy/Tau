using System.Text.RegularExpressions;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentRetryOptions(int MaxAttempts, int BaseDelayMilliseconds)
{
    private const string RetryAttemptsEnvironmentVariable = "TAU_CODING_AGENT_AUTO_RETRY_ATTEMPTS";
    private const string RetryBaseDelayEnvironmentVariable = "TAU_CODING_AGENT_AUTO_RETRY_BASE_DELAY_MS";

    public static CodingAgentRetryOptions Disabled { get; } = new(0, 0);
    public static CodingAgentRetryOptions Default { get; } = new(3, 2000);

    public bool IsEnabled => MaxAttempts > 0;

    public TimeSpan GetDelay(int attempt)
    {
        if (BaseDelayMilliseconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        var delay = Math.Min(BaseDelayMilliseconds * multiplier, int.MaxValue);
        return TimeSpan.FromMilliseconds(delay);
    }

    public static CodingAgentRetryOptions FromEnvironment()
    {
        var maxAttempts = ReadNonNegativeEnvironmentInt(
            RetryAttemptsEnvironmentVariable,
            Default.MaxAttempts);
        if (maxAttempts <= 0)
        {
            return Disabled;
        }

        return new CodingAgentRetryOptions(
            maxAttempts,
            ReadNonNegativeEnvironmentInt(
                RetryBaseDelayEnvironmentVariable,
                Default.BaseDelayMilliseconds));
    }

    public static CodingAgentRetryOptions FromSettingsOrEnvironment(CodingAgentSettingsSnapshot? settings)
    {
        if (settings?.RetryMaxAttempts is { } configuredMaxAttempts)
        {
            if (configuredMaxAttempts <= 0)
            {
                return Disabled;
            }

            var baseDelay = settings.RetryBaseDelayMilliseconds ?? Default.BaseDelayMilliseconds;
            return new CodingAgentRetryOptions(configuredMaxAttempts, Math.Max(0, baseDelay));
        }

        return FromEnvironment();
    }

    private static int ReadNonNegativeEnvironmentInt(string name, int defaultValue)
    {
        var configured = Environment.GetEnvironmentVariable(name);
        if (!int.TryParse(configured, out var value) || value < 0)
        {
            return defaultValue;
        }

        return value;
    }
}

internal static partial class CodingAgentRetryClassifier
{
    public static bool IsRetryable(string? errorMessage, int contextWindow)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        // Context overflow has a separate compaction-and-retry path upstream.
        if (IsContextOverflow(errorMessage))
        {
            return false;
        }

        return RetryableErrorPattern().IsMatch(errorMessage);
    }

    public static bool IsContextOverflow(string? errorMessage) =>
        ContextOverflowDetector.IsContextOverflowError(errorMessage);

    [GeneratedRegex(
        "overloaded|provider.?returned.?error|rate.?limit|too many requests|429|500|502|503|504|service.?unavailable|server.?error|internal.?error|network.?error|connection.?error|connection.?refused|connection.?lost|other side closed|fetch failed|upstream.?connect|reset before headers|socket hang up|ended without|timed? out|timeout|terminated|retry delay",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetryableErrorPattern();
}
