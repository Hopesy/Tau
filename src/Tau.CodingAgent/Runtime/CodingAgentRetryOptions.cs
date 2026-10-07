using Tau.Ai;
using Tau.Ai.Utilities;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentRetryOptions(int MaxAttempts, int BaseDelayMilliseconds, int MaxAgentDelayMilliseconds = 60_000)
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

        return TimeSpan.FromMilliseconds(AssistantRetry.RetryDelayMilliseconds(ToPolicy(), attempt));
    }

    /// <summary>【CodingAgent】【重试策略】将会话配置转换为普通请求和摘要共用的策略。</summary>
    /// <returns>共享助手重试策略。</returns>
    public AssistantRetryPolicy ToPolicy() => new(IsEnabled, MaxAttempts, BaseDelayMilliseconds, MaxAgentDelayMilliseconds);

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
        var fallback = FromEnvironment();
        var count = settings?.RetryMaxAttempts ?? fallback.MaxAttempts;
        var delay = settings?.RetryBaseDelayMilliseconds ?? (settings?.RetryMaxAttempts is null ? fallback.BaseDelayMilliseconds : Default.BaseDelayMilliseconds);
        var maximum = CodingAgentNativeSettings.ReadInteger(settings?.Retry, "maxAgentDelayMs", "retry") ?? 60000;
        return new(Math.Max(0, count), Math.Max(0, delay), maximum);
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

        return AssistantRetry.IsRetryableError(errorMessage);
    }

    public static bool IsContextOverflow(string? errorMessage) =>
        ContextOverflowDetector.IsContextOverflowError(errorMessage);

}
