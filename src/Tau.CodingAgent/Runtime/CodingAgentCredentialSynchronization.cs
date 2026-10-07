// 作者：xxx
using System.Collections.Concurrent;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【凭据同步】表示持久凭据已提交，但会话的本地模型或认证状态同步失败。</summary>
public sealed class CodingAgentCredentialSynchronizationException : InvalidOperationException
{
    /// <summary>【CodingAgent】【同步错误】保留操作及原因，错误文本不包含凭据内容。</summary>
    /// <param name="providerId">提供方。</param><param name="operation">login 或 logout。</param><param name="credential">已提交凭据，登出为空。</param><param name="cause">同步失败原因。</param>
    internal CodingAgentCredentialSynchronizationException(string providerId, string operation, JsonElement? credential, Exception cause)
        : base($"Credential {operation} committed for {providerId}, but local synchronization failed", cause)
    {
        ProviderId = providerId;
        Operation = operation;
        Credential = credential?.Clone();
    }

    public string ProviderId { get; }
    public string Operation { get; }
    public JsonElement? Credential { get; }
}

public sealed partial class RuntimeCodingAgentRunner
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _credentialOperations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>【CodingAgent】【OAuth 保存】串行提交同一提供方的凭据，并完成离线模型及权限同步。</summary>
    /// <param name="providerId">提供方。</param><param name="credentials">OAuth 凭据。</param><param name="cancellationToken">包含等待队列和同步阶段的取消信号。</param><returns>提交及同步任务。</returns>
    public Task SaveOAuthCredentialsAsync(string providerId, OAuthCredentials credentials, CancellationToken cancellationToken = default) =>
        CommitCredentialAsync(providerId, "login", () => { _authResolver.SaveOAuthCredentials(providerId, credentials); return true; }, cancellationToken);

    /// <summary>【CodingAgent】【密钥保存】串行提交同一提供方的密钥，并完成离线模型及权限同步。</summary>
    /// <param name="providerId">提供方。</param><param name="credential">API key 凭据。</param><param name="cancellationToken">包含等待队列和同步阶段的取消信号。</param><returns>提交及同步任务。</returns>
    public Task SaveApiKeyCredentialAsync(string providerId, ApiKeyCredential credential, CancellationToken cancellationToken = default) =>
        CommitCredentialAsync(providerId, "login", () => { _authResolver.SaveApiKeyCredential(providerId, credential); return true; }, cancellationToken);

    /// <summary>【CodingAgent】【凭据登出】串行删除凭据，即使记录原本不存在也重新同步认证状态。</summary>
    /// <param name="providerId">提供方。</param><param name="cancellationToken">包含等待队列和同步阶段的取消信号。</param><returns>是否删除了已有凭据。</returns>
    public Task<bool> LogoutAsync(string providerId, CancellationToken cancellationToken = default) =>
        CommitCredentialAsync(providerId, "logout", () => _authResolver.Logout(providerId), cancellationToken);

    /// <summary>【CodingAgent】【提交同步】先等待提供方操作队列，再持久化；提交后的失败明确标记为同步错误。</summary>
    /// <param name="provider">提供方。</param><param name="operation">凭据操作。</param><param name="commit">持久化操作。</param><param name="token">取消信号。</param><returns>持久化操作的结果。</returns>
    private async Task<bool> CommitCredentialAsync(string provider, string operation, Func<bool> commit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var gate = _credentialOperations.GetOrAdd(provider, static _ => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // 1. 【CodingAgent】【凭据提交】排队取消不得写入存储，写入失败也不得标记为已经提交
            token.ThrowIfCancellationRequested();
            var result = commit();
            JsonElement? credential = null;
            try
            {
                credential = _authResolver.ReadStoredRefreshCredential(provider);
                token.ThrowIfCancellationRequested();
                if (_virtualModelRuntime is { } runtime)
                {
                    // 2. 【CodingAgent】【本地同步】只刷新发生变化的提供方，禁止网络刷新和 OAuth 续期
                    var refresh = await runtime.RefreshProviderModelsAsync([provider], allowNetwork: false, force: false, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (refresh.Errors.TryGetValue(provider, out var error)) throw new InvalidOperationException(error);
                    var status = _authResolver.GetStatus(provider);
                    if (status.IsConfigured || status.UsesOAuth && _authResolver.GetOAuthProvider(provider) is not null)
                        await runtime.FilterNativeChatModelsAsync(provider, _modelCatalog.GetModels(provider), token).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                throw new CodingAgentCredentialSynchronizationException(provider, operation, credential, error);
            }
            return result;
        }
        finally { gate.Release(); }
    }
}
