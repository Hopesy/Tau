using System.Text.RegularExpressions;

namespace Tau.Ai;

public sealed partial class TauSecretRedactor
{
    public const string Placeholder = "[redacted]";

    public const string CodingAgentEnvironmentVariable = "TAU_CODING_AGENT_REDACT_SECRETS";
    public const string WebUiEnvironmentVariable = "TAU_WEBUI_REDACT_SECRETS";
    public const string TauLogEnvironmentVariable = "TAU_LOG_REDACT_SECRETS";

    private static readonly (Regex Pattern, string Label)[] Patterns =
    [
        (AwsAccessKeyRegex(), "AWS access key"),
        (GitHubTokenRegex(), "GitHub token"),
        (SlackTokenRegex(), "Slack token"),
        (AnthropicKeyRegex(), "Anthropic key"),
        (OpenAiKeyRegex(), "OpenAI key"),
        (BearerTokenRegex(), "Bearer token"),
        (JwtRegex(), "JWT")
    ];

    private readonly bool _enabled;

    public TauSecretRedactor(bool enabled)
    {
        _enabled = enabled;
    }

    public bool Enabled => _enabled;

    public string Redact(string? input)
    {
        if (!_enabled || string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var current = input;
        foreach (var (pattern, _) in Patterns)
        {
            current = pattern.Replace(current, Placeholder);
        }

        return current;
    }

    public static TauSecretRedactor ForEnvironmentVariable(string environmentVariable)
    {
        return new TauSecretRedactor(IsEnabledFromEnvironment(Environment.GetEnvironmentVariable(environmentVariable)));
    }

    public static bool IsEnabledFromEnvironment(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        return !string.Equals(raw, "0", StringComparison.Ordinal) &&
               !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
    }

    // AWS access key IDs: AKIA / ASIA / AIDA / AROA / AGPA / AIPA followed by 16 base32 chars.
    [GeneratedRegex(@"\b(?:AKIA|ASIA|AIDA|AROA|AGPA|AIPA|ANPA|ANVA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKeyRegex();

    // GitHub PAT (ghp_), OAuth user-to-server (gho_, ghu_), server-to-server (ghs_), refresh (ghr_).
    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{36,255}\b")]
    private static partial Regex GitHubTokenRegex();

    // Slack tokens: xoxb-/xoxa-/xoxp-/xoxs-/xoxr- followed by digits and identifiers.
    [GeneratedRegex(@"\bxox[abprs]-[0-9A-Za-z-]{10,}\b")]
    private static partial Regex SlackTokenRegex();

    // Anthropic API keys: sk-ant-...
    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_-]{20,}\b")]
    private static partial Regex AnthropicKeyRegex();

    // OpenAI API keys: sk-... (general, but exclude the more specific Anthropic prefix
    // by requiring the next char not be 'a' so 'sk-ant-...' is captured by the Anthropic
    // pattern that runs first).
    [GeneratedRegex(@"\bsk-(?!ant-)[A-Za-z0-9_-]{20,}\b")]
    private static partial Regex OpenAiKeyRegex();

    // Bearer <token> with at least 20 chars of base64url-ish content.
    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{20,}")]
    private static partial Regex BearerTokenRegex();

    // JWT three-part dotted base64url; demands base64 lookalike chars in each segment.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b")]
    private static partial Regex JwtRegex();
}
