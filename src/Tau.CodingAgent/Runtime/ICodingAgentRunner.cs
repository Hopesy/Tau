using Tau.Agent;
using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;

namespace Tau.CodingAgent.Runtime;

public interface ICodingAgentRunner
{
    IReadOnlyList<ChatMessage> Messages { get; }
    Model Model { get; }
    string? SessionName { get; set; }
    ThinkingLevel? ThinkingLevel { get; set; }
    AgentQueueMode SteeringMode { get; set; }
    AgentQueueMode FollowUpMode { get; set; }
    IReadOnlyList<string> GetProviders();
    IReadOnlyList<Model> GetModels(string provider);
    Model SelectModel(string? providerId, string? modelId);
    ProviderAuthStatus GetAuthStatus(string? providerId = null);
    IOAuthProvider? GetOAuthProvider(string providerId);
    void SaveOAuthCredentials(string providerId, OAuthCredentials credentials);
    bool Logout(string providerId);
    bool RefreshSkills(IReadOnlyList<CodingAgentSkill> skills);
    bool RefreshSystemPromptResources(
        IReadOnlyList<CodingAgentSkill> skills,
        IReadOnlyList<CodingAgentContextFile> contextFiles);
    CodingAgentSessionStats GetSessionStats(string? sessionFile = null);
    Task<CodingAgentCompactionResult> CompactAsync(string? customInstructions = null, CancellationToken cancellationToken = default);
    Task<CodingAgentBranchSummaryResult> SummarizeBranchAsync(
        IReadOnlyList<ChatMessage> messages,
        string? customInstructions = null,
        CancellationToken cancellationToken = default);
    void Steer(string input);
    void FollowUp(string input);
    void RestoreSession(CodingAgentSessionSnapshot snapshot);
    void ResetSession();
    IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentEvent> RunAsync(IReadOnlyList<ContentBlock> input, CancellationToken cancellationToken = default);
}
