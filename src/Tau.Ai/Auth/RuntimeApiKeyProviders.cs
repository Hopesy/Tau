// 作者：xxx
namespace Tau.Ai.Auth;

/// <summary>【AI】【认证快照】不包含秘密的配置状态及凭据类型。</summary>
public sealed record ProviderAuthMetadata(ProviderAuthStatus Status, bool HasStoredCredential, bool UsesOAuth);

public sealed partial class ProviderAuthResolver
{
    /// <summary>【AI】【提供方认证】替换本会话的原生 API key 认证定义。</summary>
    /// <param name="providers">完整注册快照。</param>
    public void SetRuntimeApiKeyProviders(IReadOnlyDictionary<string, ApiKeyAuthDefinition> providers)
    {
        lock (_runtimeAuthenticationGate) Volatile.Write(ref _runtimeAuthentication, _runtimeAuthentication with
            { ApiKey = new Dictionary<string, ApiKeyAuthDefinition>(providers, StringComparer.OrdinalIgnoreCase) });
    }

    /// <summary>【AI】【提供方认证】读取原生 API key 定义，供状态、请求及交互登录使用。</summary>
    /// <param name="provider">提供方。</param><returns>认证定义或空值。</returns>
    public ApiKeyAuthDefinition? GetApiKeyProvider(string provider) => Volatile.Read(ref _runtimeAuthentication).ApiKey.GetValueOrDefault(provider);

    /// <summary>【AI】【提供方认证】保存交互登录产生的密钥及环境凭据。</summary>
    /// <param name="provider">提供方。</param><param name="credential">登录凭据。</param>
    public void SaveApiKeyCredential(string provider, ApiKeyCredential credential) => _credentialStore.SaveApiKey(provider, credential);

    /// <summary>【AI】【认证元数据】检查是否存在持久凭据，不执行认证解析。</summary>
    /// <param name="provider">提供方。</param><returns>是否存在凭据。</returns>
    public bool HasStoredCredentials(string provider) => GetStoredAuthEntry(provider) is not null;
    /// <summary>【AI】【认证元数据】检查存储的凭据是否属于 OAuth。</summary>
    /// <param name="provider">提供方。</param><returns>是否正在使用 OAuth 凭据。</returns>
    public bool IsUsingOAuth(string provider) => GetStoredAuthEntry(provider)?.OAuth is not null;

    /// <summary>【AI】【凭据目录】读取已保存凭据的标识和类型，包括已经注销的提供方。</summary>
    /// <returns>不包含密钥、令牌或环境值的凭据目录。</returns>
    public IReadOnlyList<ProviderCredentialInfo> ListStoredCredentials() => _credentialStore.LoadEntries()
        .Select(entry => new ProviderCredentialInfo(entry.Key, entry.Value.OAuth is not null ? "oauth" : "api_key"))
        .ToArray();

    /// <summary>【AI】【认证快照】一次读取凭据文件，生成全部提供方的非秘密状态。</summary>
    /// <param name="providers">提供方集合。</param><param name="selected">当前模型。</param><param name="explicitApiKey">仅用于当前模型的显式密钥。</param>
    /// <returns>独立元数据快照。</returns>
    public IReadOnlyList<ProviderAuthMetadata> GetMetadataSnapshot(IEnumerable<string> providers, Model selected, string? explicitApiKey = null)
    {
        var entries = _credentialStore.LoadEntries();
        return providers.Select(provider =>
        {
            var isSelected = provider.Equals(selected.Provider, StringComparison.OrdinalIgnoreCase);
            var status = ResolveStatus(provider, isSelected ? selected : null, isSelected ? explicitApiKey : null, storedEntries: entries);
            var stored = entries.GetValueOrDefault(provider);
            return new ProviderAuthMetadata(status, stored is not null, stored?.OAuth is not null);
        }).ToArray();
    }
}
