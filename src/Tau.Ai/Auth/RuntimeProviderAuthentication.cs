// 作者：xxx
using Tau.Ai.Auth.OAuth;

namespace Tau.Ai.Auth;

public sealed partial class ProviderAuthResolver
{
    private readonly object _runtimeAuthenticationGate = new();
    private RuntimeAuthentication _runtimeAuthentication = new(
        new Dictionary<string, IOAuthProvider>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, ApiKeyAuthDefinition>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>【AI】【认证注册】同时发布认证实现和原生提供方归属，读取方不会观察到半更新的方式集合。</summary>
    /// <param name="oauthProviders">OAuth 实现。</param><param name="apiKeyProviders">API key 实现。</param>
    /// <param name="nativeProviders">完全拥有自身认证方式、禁止继承内置认证的原生提供方。</param>
    public void SetRuntimeAuthenticationProviders(IReadOnlyDictionary<string, IOAuthProvider> oauthProviders,
        IReadOnlyDictionary<string, ApiKeyAuthDefinition> apiKeyProviders, IEnumerable<string> nativeProviders)
    {
        var next = new RuntimeAuthentication(new Dictionary<string, IOAuthProvider>(oauthProviders, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ApiKeyAuthDefinition>(apiKeyProviders, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(nativeProviders, StringComparer.OrdinalIgnoreCase));
        lock (_runtimeAuthenticationGate) Volatile.Write(ref _runtimeAuthentication, next);
    }

    /// <summary>【AI】【认证归属】判断原生扩展是否完整替换此提供方的认证契约。</summary>
    /// <param name="provider">提供方标识。</param><returns>是否禁止使用内置认证回退。</returns>
    public bool HasNativeAuthentication(string provider) => Volatile.Read(ref _runtimeAuthentication).NativeProviders.Contains(provider);

    /// <summary>【AI】【认证目录】列出已注册认证提供方，包括尚未发现模型的提供方。</summary>
    /// <returns>独立的提供方标识列表。</returns>
    public IReadOnlyList<string> GetAuthenticationProviderIds()
    {
        var runtime = Volatile.Read(ref _runtimeAuthentication);
        return _oauthProviders.Providers.Select(provider => provider.Id).Concat(runtime.OAuth.Keys).Concat(runtime.ApiKey.Keys)
            .Concat(runtime.NativeProviders).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>【AI】【认证快照】只在构造时复制字典，发布后保持只读。</summary>
    /// <param name="OAuth">运行时 OAuth。</param><param name="ApiKey">运行时密钥认证。</param><param name="NativeProviders">原生认证归属。</param>
    private sealed record RuntimeAuthentication(IReadOnlyDictionary<string, IOAuthProvider> OAuth,
        IReadOnlyDictionary<string, ApiKeyAuthDefinition> ApiKey, IReadOnlySet<string> NativeProviders);
}
