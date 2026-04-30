namespace Tau.Ai.Providers.OpenAiResponses;

public record OpenAiResponsesOptions : StreamOptions
{
    public string? ReasoningEffort { get; init; }
    public string? ReasoningSummary { get; init; }
    public string? ServiceTier { get; init; }
}

public record OpenAiCodexResponsesOptions : StreamOptions
{
    public string? ReasoningEffort { get; init; }
    public string? ReasoningSummary { get; init; }
    public string? ServiceTier { get; init; }
    public string? TextVerbosity { get; init; }
}

public record AzureOpenAiResponsesOptions : StreamOptions
{
    public string? ReasoningEffort { get; init; }
    public string? ReasoningSummary { get; init; }
    public string? AzureApiVersion { get; init; }
    public string? AzureResourceName { get; init; }
    public string? AzureBaseUrl { get; init; }
    public string? AzureDeploymentName { get; init; }
}
