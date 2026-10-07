namespace Tau.Ai.Auth.OAuth;

public interface IOAuthProvider
{
    string Id { get; }
    string Name { get; }
    bool UsesCallbackServer => false;
    /// <summary>该认证方式是否使用提供方订阅。</summary>
    bool IsSubscription => false;
    /// <summary>可选登录选择器说明。</summary>
    string? LoginLabel => null;

    Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default);
    /// <summary>【AI】【OAuth 登录选项】向需要安装标识的提供方传递可选配置，旧实现保留原入口。</summary>
    /// <param name="callbacks">交互。</param><param name="options">登录选项。</param><param name="cancellationToken">取消信号。</param><returns>凭据。</returns>
    Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, OAuthLoginOptions? options, CancellationToken cancellationToken = default) => LoginAsync(callbacks, cancellationToken);
    Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default);
    string GetApiKey(OAuthCredentials credentials);
    /// <summary>【AI】【OAuth 认证】派生完整请求认证，兼容只提供密钥的旧实现。</summary>
    /// <param name="credentials">当前凭据。</param><param name="token">取消信号。</param><returns>请求认证。</returns>
    ProviderAuthResult ResolveAuth(OAuthCredentials credentials, CancellationToken token = default) => new(ApiKey: GetApiKey(credentials));
    Model ModifyModel(Model model, OAuthCredentials credentials) => model;
}
