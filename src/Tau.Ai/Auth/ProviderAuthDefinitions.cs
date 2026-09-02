namespace Tau.Ai.Auth;

/// <summary>
/// provider 请求所需的认证结果。
/// </summary>
/// <param name="ApiKey">发送给 provider 的 key 或 bearer token。</param>
/// <param name="Headers">由认证流程生成的请求头。</param>
/// <param name="BaseUrl">由凭据决定的请求地址覆盖。</param>
/// <param name="Env">provider 专用环境配置。</param>
/// <param name="Source">用于状态展示的来源名称。</param>
public sealed record ProviderAuthResult(
    string? ApiKey = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? BaseUrl = null,
    IReadOnlyDictionary<string, string>? Env = null,
    string? Source = null);

/// <summary>
/// provider 持久化 API key 凭据。
/// </summary>
/// <param name="Key">可选密钥；ambient-only provider 可以为空。</param>
/// <param name="Env">provider 专用环境字段。</param>
public sealed record ApiKeyCredential(
    string? Key = null,
    IReadOnlyDictionary<string, string>? Env = null);

/// <summary>
/// provider 持久化 OAuth 凭据。
/// </summary>
/// <param name="Refresh">刷新令牌。</param>
/// <param name="Access">访问令牌。</param>
/// <param name="ExpiresAt">访问令牌过期时间。</param>
/// <param name="Metadata">provider 特有的非敏感元数据。</param>
public sealed record ProviderOAuthCredential(
    string Refresh,
    string Access,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// provider 认证凭据的统一类型标记。
/// </summary>
public abstract record ProviderCredential
{
    private ProviderCredential() { }

    /// <summary>API key 凭据包装。</summary>
    /// <param name="Value">API key 凭据。</param>
    public sealed record ApiKey(ApiKeyCredential Value) : ProviderCredential;

    /// <summary>OAuth 凭据包装。</summary>
    /// <param name="Value">OAuth 凭据。</param>
    public sealed record OAuth(ProviderOAuthCredential Value) : ProviderCredential;
}

/// <summary>不暴露密钥值的凭据目录项。</summary>
/// <param name="ProviderId">provider id。</param>
/// <param name="Type">凭据类型。</param>
public sealed record ProviderCredentialInfo(string ProviderId, string Type);

/// <summary>
/// provider 认证解析输入。
/// </summary>
public sealed record ProviderAuthResolveContext(
    string ProviderId,
    ApiKeyCredential? Credential,
    IReadOnlyDictionary<string, string>? Environment,
    CancellationToken CancellationToken);

/// <summary>
/// provider 认证状态检查输入。
/// </summary>
public sealed record ProviderAuthCheckContext(
    string ProviderId,
    ApiKeyCredential? Credential,
    IReadOnlyDictionary<string, string>? Environment,
    CancellationToken CancellationToken);

/// <summary>
/// API key 认证实现。
/// </summary>
public sealed class ApiKeyAuthDefinition
{
    /// <summary>创建 API key 认证实现。</summary>
    /// <param name="name">认证方式显示名称。</param>
    /// <param name="resolve">解析请求认证的委托。</param>
    /// <param name="check">可选的无副作用状态检查委托。</param>
    /// <param name="login">可选的交互式登录委托。</param>
    public ApiKeyAuthDefinition(
        string name,
        Func<ProviderAuthResolveContext, Task<ProviderAuthResult?>> resolve,
        Func<ProviderAuthCheckContext, Task<ProviderAuthStatus?>>? check = null,
        Func<AuthInteraction, Task<ApiKeyCredential>>? login = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        ResolveAsync = resolve ?? throw new ArgumentNullException(nameof(resolve));
        CheckAsync = check;
        LoginAsync = login;
    }

    /// <summary>认证方式显示名称。</summary>
    public string Name { get; }

    /// <summary>解析请求认证。</summary>
    public Func<ProviderAuthResolveContext, Task<ProviderAuthResult?>> ResolveAsync { get; }

    /// <summary>检查认证是否已配置。</summary>
    public Func<ProviderAuthCheckContext, Task<ProviderAuthStatus?>>? CheckAsync { get; }

    /// <summary>执行 API key 登录。</summary>
    public Func<AuthInteraction, Task<ApiKeyCredential>>? LoginAsync { get; }
}

/// <summary>
/// OAuth 认证实现。
/// </summary>
public sealed class OAuthAuthDefinition
{
    /// <summary>创建 OAuth 认证实现。</summary>
    /// <param name="name">认证方式显示名称。</param>
    /// <param name="login">登录委托。</param>
    /// <param name="refresh">刷新委托。</param>
    /// <param name="toAuth">将 OAuth 凭据转换为请求认证的委托。</param>
    public OAuthAuthDefinition(
        string name,
        Func<AuthInteraction, Task<ProviderOAuthCredential>> login,
        Func<ProviderOAuthCredential, CancellationToken, Task<ProviderOAuthCredential>> refresh,
        Func<ProviderOAuthCredential, Task<ProviderAuthResult>> toAuth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        LoginAsync = login ?? throw new ArgumentNullException(nameof(login));
        RefreshAsync = refresh ?? throw new ArgumentNullException(nameof(refresh));
        ToAuthAsync = toAuth ?? throw new ArgumentNullException(nameof(toAuth));
    }

    /// <summary>认证方式显示名称。</summary>
    public string Name { get; }

    /// <summary>执行 OAuth 登录。</summary>
    public Func<AuthInteraction, Task<ProviderOAuthCredential>> LoginAsync { get; }

    /// <summary>刷新 OAuth 令牌。</summary>
    public Func<ProviderOAuthCredential, CancellationToken, Task<ProviderOAuthCredential>> RefreshAsync { get; }

    /// <summary>从 OAuth 凭据派生请求认证。</summary>
    public Func<ProviderOAuthCredential, Task<ProviderAuthResult>> ToAuthAsync { get; }
}

/// <summary>
/// provider 自有认证定义。
/// </summary>
public sealed class ProviderAuthDefinition
{
    /// <summary>创建 provider 认证定义。</summary>
    /// <param name="apiKey">API key 认证定义。</param>
    /// <param name="oauth">OAuth 认证定义。</param>
    public ProviderAuthDefinition(ApiKeyAuthDefinition? apiKey = null, OAuthAuthDefinition? oauth = null)
    {
        if (apiKey is null && oauth is null)
        {
            throw new ArgumentException("At least one provider authentication method is required.");
        }

        ApiKey = apiKey;
        OAuth = oauth;
    }

    /// <summary>API key 认证定义。</summary>
    public ApiKeyAuthDefinition? ApiKey { get; }

    /// <summary>OAuth 认证定义。</summary>
    public OAuthAuthDefinition? OAuth { get; }
}

/// <summary>
/// 供 Models 使用的异步凭据存储接口。
/// </summary>
public interface IProviderCredentialStore
{
    /// <summary>读取 provider 当前凭据。</summary>
    /// <param name="providerId">provider id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>凭据，不存在时为 null。</returns>
    Task<ProviderCredential?> ReadAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>列出凭据元数据，不读取密钥内容。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>凭据元数据列表。</returns>
    Task<IReadOnlyList<ProviderCredentialInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>在 provider 级别串行执行读改写。</summary>
    /// <param name="providerId">provider id。</param>
    /// <param name="mutation">根据当前凭据生成新凭据；返回 null 表示保持不变。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入后的凭据。</returns>
    Task<ProviderCredential?> ModifyAsync(
        string providerId,
        Func<ProviderCredential?, Task<ProviderCredential?>> mutation,
        CancellationToken cancellationToken = default);

    /// <summary>删除 provider 凭据。</summary>
    /// <param name="providerId">provider id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DeleteAsync(string providerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 线程安全的进程内凭据存储，适用于测试和宿主注入。
/// </summary>
public sealed class InMemoryProviderCredentialStore : IProviderCredentialStore
{
    private readonly Dictionary<string, ProviderCredential> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <inheritdoc />
    public Task<ProviderCredential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_values.TryGetValue(providerId, out var value) ? value : null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProviderCredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ProviderCredentialInfo>>(_values.Select(pair =>
                new ProviderCredentialInfo(pair.Key, pair.Value switch
                {
                    ProviderCredential.ApiKey => "api_key",
                    ProviderCredential.OAuth => "oauth",
                    _ => "unknown"
                })).ToArray());
        }
    }

    /// <inheritdoc />
    public async Task<ProviderCredential?> ModifyAsync(
        string providerId,
        Func<ProviderCredential?, Task<ProviderCredential?>> mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var gate = GetLock(providerId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderCredential? current;
            lock (_gate) _values.TryGetValue(providerId, out current);
            var next = await mutation(current).ConfigureAwait(false);
            if (next is not null)
            {
                lock (_gate) _values[providerId] = next;
                return next;
            }

            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _values.Remove(providerId);
        return Task.CompletedTask;
    }

    private SemaphoreSlim GetLock(string providerId)
    {
        lock (_gate)
        {
            if (_locks.TryGetValue(providerId, out var gate)) return gate;
            gate = new SemaphoreSlim(1, 1);
            _locks[providerId] = gate;
            return gate;
        }
    }
}

/// <summary>
/// 登录交互回调，兼容文本、秘密、选择和通知事件。
/// </summary>
public interface AuthInteraction
{
    /// <summary>整个登录流程的取消令牌。</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>向用户请求一项输入。</summary>
    /// <param name="prompt">提示内容。</param>
    /// <returns>用户输入。</returns>
    Task<string> PromptAsync(string prompt);

    /// <summary>发送登录过程通知。</summary>
    /// <param name="message">通知内容。</param>
    void Notify(string message);
}
