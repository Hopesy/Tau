// 作者：xxx
using Tau.Ai.Auth;

namespace Tau.Ai.Providers.Cloudflare;

/// <summary>【AI】【Cloudflare 认证】统一入口按字段合并凭据和环境，同时要求 API key 与账户。</summary>
internal static class CloudflareWorkersAiAuth
{
    /// <summary>建立可登录、检查和解析的 Workers AI 认证定义。</summary>
    /// <param name="legacyResolver">兼容旧 auth.json 的解析器。</param>
    /// <returns>provider 自有认证定义。</returns>
    internal static ProviderAuthDefinition Create(ProviderAuthResolver legacyResolver) => new(apiKey: new ApiKeyAuthDefinition(
        "Cloudflare API key",
        context => Task.FromResult(Resolve(context, legacyResolver)),
        check: context =>
        {
            var auth = Resolve(new(context.ProviderId, context.Credential, context.Environment, context.CancellationToken), legacyResolver);
            return Task.FromResult<ProviderAuthStatus?>(new(context.ProviderId, auth is not null, auth?.Source ?? "none", false, true,
                auth is null ? "Cloudflare API key and account ID are required." : "Credentials are available."));
        },
        login: async interaction => new ApiKeyCredential(
            await interaction.PromptAsync(new ProviderAuthPrompt("secret", "Enter Cloudflare API key", Signal: interaction.CancellationToken)).ConfigureAwait(false),
            new Dictionary<string, string> { [CloudflareAuthResolver.AccountIdEnvironmentVariable] = await interaction.PromptAsync("Enter Cloudflare account ID").ConfigureAwait(false) })));

    /// <summary>凭据字段优先，缺少的字段依次从请求环境和进程环境读取。</summary>
    /// <param name="context">认证请求。</param>
    /// <param name="legacyResolver">旧存储解析器。</param>
    /// <returns>完整认证结果，缺少 key 或账户时返回 null。</returns>
    private static ProviderAuthResult? Resolve(ProviderAuthResolveContext context, ProviderAuthResolver legacyResolver)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var legacy = context.Credential is null ? legacyResolver.GetStoredAuthEntry(context.ProviderId) : null;
        var credential = context.Credential ?? (legacy?.ApiKey is { } storedKey ? new ApiKeyCredential(storedKey, legacy.Env) : null);
        var key = credential?.Key ?? ProviderEnvironment.GetValue(CloudflareAuthResolver.ApiKeyEnvironmentVariable, context.Environment);
        var account = ScopedValue(credential?.Env, CloudflareAuthResolver.AccountIdEnvironmentVariable)
            ?? ProviderEnvironment.GetValue(CloudflareAuthResolver.AccountIdEnvironmentVariable, context.Environment);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(account) || EnvironmentApiKeyResolver.IsAuthenticatedMarker(key)) return null;
        return new ProviderAuthResult(key,
            Env: new Dictionary<string, string> { [CloudflareAuthResolver.AccountIdEnvironmentVariable] = account },
            Source: credential is not null ? "stored credential" : CloudflareAuthResolver.ApiKeyEnvironmentVariable);
    }

    /// <summary>读取凭据内的字段，保留空字符串以阻止意外环境回退。</summary>
    /// <param name="environment">凭据环境。</param>
    /// <param name="name">字段名。</param>
    /// <returns>存在时返回原值，否则返回 null。</returns>
    private static string? ScopedValue(IReadOnlyDictionary<string, string>? environment, string name) =>
        environment?.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}
