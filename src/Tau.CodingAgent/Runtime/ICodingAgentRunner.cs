using Tau.AgentCore;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Observability;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentQueuedMessages(
    IReadOnlyList<string> Steering,
    IReadOnlyList<string> FollowUp);

public interface ICodingAgentRunner
{
    IReadOnlyList<ChatMessage> Messages { get; }
    Model Model { get; }
    string? SessionName { get; set; }
    ThinkingLevel? ThinkingLevel { get; set; }
    AgentQueueMode SteeringMode { get; set; }
    AgentQueueMode FollowUpMode { get; set; }
    int PendingMessageCount { get; }
    bool IsStreaming { get; }
    bool IsCompacting { get; }
    IReadOnlyList<string> GetProviders();
    /// <summary>【CodingAgent】【认证目录】读取登录提供方，兼容运行器默认使用模型提供方列表。</summary>
    /// <returns>认证提供方标识。</returns>
    IReadOnlyList<string> GetAuthProviders() => GetProviders();
    IReadOnlyList<Model> GetModels(string provider);
    /// <summary>【CodingAgent】【模型权限】按提供方账户权限过滤目录，基础运行器默认保留全部模型。</summary>
    /// <param name="provider">提供方。</param><param name="models">待筛选模型。</param><returns>账户允许的模型。</returns>
    IReadOnlyList<Model> FilterAvailableModels(string provider, IReadOnlyList<Model> models) => models;
    Model SelectModel(string? providerId, string? modelId);
    ProviderAuthStatus GetAuthStatus(string? providerId = null);
    IOAuthProvider? GetOAuthProvider(string providerId);
    /// <summary>【CodingAgent】【提供方名称】获取认证界面和扩展共用的显示名称。</summary>
    /// <param name="providerId">提供方标识。</param><returns>显示名，未知提供方回退标识。</returns>
    string GetProviderDisplayName(string providerId) => CodingAgentProviderDisplayNames.TryGetBuiltIn(providerId) ?? GetOAuthProvider(providerId)?.Name ?? providerId;
    /// <summary>【CodingAgent】【凭据目录】读取可登出的持久凭据；兼容运行器通过状态提供目录。</summary>
    /// <returns>只包含提供方和凭据类型的列表。</returns>
    IReadOnlyList<ProviderCredentialInfo> ListStoredCredentials() => GetProviders().Select(provider => GetAuthStatus(provider))
        .Where(status => status.Source.StartsWith("auth.json", StringComparison.Ordinal))
        .Select(status => new ProviderCredentialInfo(status.Provider, status.UsesOAuth ? "oauth" : "api_key")).ToArray();
    void SaveOAuthCredentials(string providerId, OAuthCredentials credentials);
    /// <summary>【CodingAgent】【凭据同步】保存 OAuth 并同步会话目录，兼容运行器回退同步保存。</summary>
    /// <param name="providerId">提供方。</param><param name="credentials">凭据。</param><param name="cancellationToken">取消信号。</param><returns>保存及同步任务。</returns>
    Task SaveOAuthCredentialsAsync(string providerId, OAuthCredentials credentials, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveOAuthCredentials(providerId, credentials);
        return Task.CompletedTask;
    }
    /// <summary>读取可选的原生 API key 登录定义。</summary><param name="providerId">提供方。</param><returns>认证定义。</returns>
    ApiKeyAuthDefinition? GetApiKeyProvider(string providerId) => null;
    /// <summary>保存原生 API key 登录结果。</summary><param name="providerId">提供方。</param><param name="credential">凭据。</param>
    void SaveApiKeyCredential(string providerId, ApiKeyCredential credential) => throw new NotSupportedException("API key login is not available.");
    /// <summary>【CodingAgent】【凭据同步】保存 API key 并同步会话目录，兼容运行器回退同步保存。</summary>
    /// <param name="providerId">提供方。</param><param name="credential">凭据。</param><param name="cancellationToken">取消信号。</param><returns>保存及同步任务。</returns>
    Task SaveApiKeyCredentialAsync(string providerId, ApiKeyCredential credential, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveApiKeyCredential(providerId, credential);
        return Task.CompletedTask;
    }
    bool Logout(string providerId);
    /// <summary>【CodingAgent】【凭据同步】删除凭据并同步会话目录，兼容运行器回退同步登出。</summary>
    /// <param name="providerId">提供方。</param><param name="cancellationToken">取消信号。</param><returns>是否删除已有凭据。</returns>
    Task<bool> LogoutAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Logout(providerId));
    }
    bool RefreshSkills(IReadOnlyList<CodingAgentSkill> skills);
    bool RefreshSystemPromptResources(
        IReadOnlyList<CodingAgentSkill> skills,
        IReadOnlyList<CodingAgentContextFile> contextFiles);
    CodingAgentSessionStats GetSessionStats(string? sessionFile = null);
    Task<CodingAgentCompactionResult> CompactAsync(string? customInstructions = null, CancellationToken cancellationToken = default);
    Task<CodingAgentBranchSummaryResult> SummarizeBranchAsync(
        IReadOnlyList<ChatMessage> messages,
        string? customInstructions = null,
        bool replaceInstructions = false,
        CancellationToken cancellationToken = default);
    void Steer(string input);
    void Steer(IReadOnlyList<ContentBlock> input);
    void Steer(ChatMessage input);
    void FollowUp(string input);
    void FollowUp(IReadOnlyList<ContentBlock> input);
    void FollowUp(ChatMessage input);
    CodingAgentQueuedMessages DrainQueuedMessages();
    void AppendMessage(ChatMessage message);
    Task PublishLifecycleEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken = default);
    void RestoreSession(CodingAgentSessionSnapshot snapshot);
    void ResetSession();
    IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentEvent> RunAsync(IReadOnlyList<ContentBlock> input, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentEvent> RunAsync(ChatMessage input, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentEvent> RunAsync(IReadOnlyList<ChatMessage> input, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentEvent> RunAsync(
        string input,
        TauRuntimeLogContext? logContext,
        CancellationToken cancellationToken) =>
        RunAsync(input, cancellationToken);
    IAsyncEnumerable<AgentEvent> RunAsync(
        IReadOnlyList<ContentBlock> input,
        TauRuntimeLogContext? logContext,
        CancellationToken cancellationToken) =>
        RunAsync(input, cancellationToken);
    IAsyncEnumerable<AgentEvent> RunAsync(
        ChatMessage input,
        TauRuntimeLogContext? logContext,
        CancellationToken cancellationToken) =>
        RunAsync(input, cancellationToken);
    IAsyncEnumerable<AgentEvent> RunAsync(
        IReadOnlyList<ChatMessage> input,
        TauRuntimeLogContext? logContext,
        CancellationToken cancellationToken) =>
        RunAsync(input, cancellationToken);
}
