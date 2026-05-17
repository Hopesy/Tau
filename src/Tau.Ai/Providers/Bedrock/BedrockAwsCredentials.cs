namespace Tau.Ai.Providers.Bedrock;

internal sealed record BedrockAwsCredentials(
    string AccessKeyId,
    string SecretAccessKey,
    string? SessionToken,
    DateTimeOffset? ExpiresAt = null,
    string? Source = null);
