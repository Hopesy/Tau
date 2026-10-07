namespace Tau.Ai.Auth.OAuth;

public sealed record OAuthProviderInfo(
    string Id,
    string Name,
    bool Available,
    bool UsesCallbackServer)
{
    /// <summary>此认证方式使用提供方订阅。</summary>
    public bool IsSubscription { get; init; }
    /// <summary>可选登录菜单文案。</summary>
    public string? LoginLabel { get; init; }
}
