// 作者：xxx
using Tau.Ai.Auth;

namespace Tau.Ai.Providers;

public sealed partial class Models
{
    /// <summary>【AI】【认证检查】读取带取消保护的原始凭据，将存储失败统一归为认证错误。</summary>
    /// <param name="providerId">提供方标识。</param><param name="token">取消信号。</param><returns>原始凭据或空值。</returns>
    private async Task<ProviderCredential?> ReadCredentialForCheckAsync(string providerId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var credential = await _credentialStore.ReadAsync(providerId, token).WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return credential;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new ModelsError("auth", $"Credential store read failed for {providerId}.", error); }
    }

    /// <summary>【AI】【认证归属检查】按原始凭据类型选择处理器，OAuth 仅检查支持情况，不刷新或派生令牌。</summary>
    /// <param name="provider">已选中的提供方定义。</param><param name="credential">原始凭据快照。</param><param name="environment">请求环境覆盖。</param>
    /// <param name="token">取消信号。</param><param name="explicitKey">可选显式密钥，空字符串也代表已指定。</param><returns>认证状态或不支持。</returns>
    private async Task<ProviderAuthStatus?> CheckProviderAuthStatusAsync(ProviderDefinition provider, ProviderCredential? credential,
        IReadOnlyDictionary<string, string>? environment, CancellationToken token, string? explicitKey = null)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (provider.Auth is null) return _authResolver.GetStatus(provider.Id, explicitApiKey: explicitKey, env: environment);
            if (credential is ProviderCredential.OAuth)
                return provider.Auth.OAuth is null ? null : new(provider.Id, true, "OAuth", true, true, "OAuth credentials found in the provider credential store.");
            var apiKey = provider.Auth.ApiKey;
            if (apiKey is null) return null;
            var apiCredential = credential is ProviderCredential.ApiKey api ? api.Value : null;
            if (apiCredential is not null && environment is not null) apiCredential = apiCredential with { Env = MergeEnvironment(apiCredential.Env, environment) };

            // 1. 【AI】【密钥检查】优先使用无副作用检查；未提供检查时使用同一认证解析契约
            if (apiKey.CheckAsync is { } check)
            {
                var status = await check(new(provider.Id, apiCredential, environment, token)).WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return status;
            }
            var resolved = await ResolveProviderAuthAsync(provider, explicitKey, environment, token, null).WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return resolved is null ? new(provider.Id, false, "none", false, apiKey.LoginAsync is not null, "No credentials found.")
                : new(provider.Id, true, resolved.Source ?? apiKey.Name, false, apiKey.LoginAsync is not null, "Credentials are available.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (ModelsError) { throw; }
        catch (Exception error) { throw new ModelsError("auth", $"Authentication check failed for provider '{provider.Id}'.", error); }
    }
}
