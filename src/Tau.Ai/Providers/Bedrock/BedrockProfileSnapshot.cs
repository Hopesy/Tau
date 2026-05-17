namespace Tau.Ai.Providers.Bedrock;

internal sealed record BedrockProfileSnapshot
{
    public string Name { get; init; } = "";
    public string? AccessKeyId { get; init; }
    public string? SecretAccessKey { get; init; }
    public string? SessionToken { get; init; }
    public string? Region { get; init; }
    public string? RoleArn { get; init; }
    public string? SourceProfile { get; init; }
    public string? CredentialSource { get; init; }
    public string? RoleSessionName { get; init; }
    public string? ExternalId { get; init; }
    public string? WebIdentityTokenFile { get; init; }
    public string? CredentialProcess { get; init; }
    public string? MfaSerial { get; init; }
    public string? SsoSession { get; init; }
    public string? SsoStartUrl { get; init; }
    public string? SsoRegion { get; init; }
    public string? SsoAccountId { get; init; }
    public string? SsoRoleName { get; init; }

    public bool HasStaticCredentials =>
        !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(SecretAccessKey);
}
