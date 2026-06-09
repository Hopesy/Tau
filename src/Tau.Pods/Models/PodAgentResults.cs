namespace Tau.Pods.Models;

public sealed record PodAgentPromptPlan(
    string PodId,
    string ModelName,
    string UpstreamModel,
    int Port,
    string BaseUrl,
    string Api,
    string ApiKeySource,
    string SystemPrompt,
    IReadOnlyList<string> UserArgs,
    IReadOnlyList<string> AgentArgs);

public sealed record PodAgentRunResult(
    string PodId,
    string ModelName,
    string ProviderId,
    string ModelId,
    bool Success,
    int ExitCode,
    string Summary,
    string Command,
    IReadOnlyList<string> Arguments,
    string ModelsConfigPath,
    string StdOut,
    string StdErr,
    TimeSpan Duration,
    bool Started,
    bool Cancelled);
