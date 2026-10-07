// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth.Providers;
using Tau.Ai.Providers.PiMessages;
using Tau.Ai.Registry;

namespace Tau.Ai.Providers;

public static partial class BuiltInProviders
{
    /// <summary>【Radius】【提供方工厂】创建可独立加入 Models 的公共或自定义网关，目录与认证共用同一网关。</summary>
    /// <param name="id">提供方标识，默认为 radius。</param><param name="name">显示名称，默认为 Radius。</param>
    /// <param name="gateway">网关地址，仅默认公共网关带有内置基线。</param><param name="httpClient">可选共享 HTTP 客户端。</param>
    /// <returns>支持聊天、API key、OAuth 及持久化动态目录的提供方定义。</returns>
    public static ProviderDefinition CreateRadiusProvider(string id = "radius", string name = "Radius", string? gateway = null, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var normalized = RadiusGatewayConfigLoader.NormalizeGatewayUrl(gateway ?? RadiusGatewayConfigLoader.DefaultGateway);
        var client = httpClient ?? TauHttpClientFactory.Create();
        var implementation = new RadiusProvider(normalized, client);

        // 1. 【Radius】【基线目录】自定义提供方标识可以复用公共网关基线，自定义网关初始目录为空
        IReadOnlyList<Model> baseline = [];
        if (normalized == RadiusGatewayConfigLoader.DefaultGateway)
        {
            foreach (var catalog in new[] { BuiltInModels.Catalog, GeneratedBuiltInModels.Catalog })
                if (catalog.TryGetValue("radius", out var models)) baseline = RadiusProvider.MergeModels(baseline,
                    models.Values.Where(model => ModelTypes.GetModelType(model) == ModelTypes.Chat).Select(model => model with { Provider = id }).ToArray());
        }

        // 2. 【Radius】【网关认证】原生密钥定义只读取显式凭据和标准环境变量，OAuth 绑定相同网关
        var apiKey = new ApiKeyAuthDefinition("Radius API key", ResolveRadiusApiKeyAsync,
            login: interaction => LoginWithApiKeyAsync(interaction, "Radius API key"));
        var auth = new ProviderAuthDefinition(apiKey, CreateOAuthAuth(new RadiusOAuthProvider(normalized, name, id, client)));
        ProviderDefinition? definition = null;
        definition = new(id, implementation, baseline, name, auth: auth,
            refreshWithContext: context => implementation.RefreshModelsAsync(id, context, dynamicModels => definition!.SetModels(RadiusProvider.MergeModels(baseline, dynamicModels))));
        return definition;
    }

    /// <summary>【Radius】【密钥解析】已存密钥优先，否则读取请求环境或进程的 RADIUS_API_KEY。</summary>
    /// <param name="context">凭据、环境与取消信号。</param><returns>可用认证结果；没有密钥时为空。</returns>
    private static Task<ProviderAuthResult?> ResolveRadiusApiKeyAsync(ProviderAuthResolveContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(context.Credential?.Key)) return Task.FromResult<ProviderAuthResult?>(
            new(context.Credential.Key, Env: context.Credential.Env, Source: "stored credential"));
        var key = context.Environment is { } environment && environment.TryGetValue("RADIUS_API_KEY", out var value) && !string.IsNullOrEmpty(value)
            ? value : Environment.GetEnvironmentVariable("RADIUS_API_KEY");
        return Task.FromResult<ProviderAuthResult?>(string.IsNullOrEmpty(key) ? null : new(key, Source: "RADIUS_API_KEY"));
    }
}
