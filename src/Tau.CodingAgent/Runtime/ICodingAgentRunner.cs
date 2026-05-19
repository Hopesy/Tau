using Tau.Agent;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;

namespace Tau.CodingAgent.Runtime;

public interface ICodingAgentRunner
{
    IReadOnlyList<ChatMessage> Messages { get; }
    Model Model { get; }
    string? SessionName { get; set; }
    IReadOnlyList<string> GetProviders();
    IReadOnlyList<Model> GetModels(string provider);
    Model SelectModel(string? providerId, string? modelId);
    ProviderAuthStatus GetAuthStatus(string? providerId = null);
    IOAuthProvider? GetOAuthProvider(string providerId);
    void SaveOAuthCredentials(string providerId, OAuthCredentials credentials);
    CodingAgentSessionStats GetSessionStats(string? sessionFile = null);
    Task<CodingAgentCompactionResult> CompactAsync(string? customInstructions = null, CancellationToken cancellationToken = default);
    void RestoreSession(CodingAgentSessionSnapshot snapshot);
    void ResetSession();
    IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentEvent> RunAsync(IReadOnlyList<ContentBlock> input, CancellationToken cancellationToken = default);
}
