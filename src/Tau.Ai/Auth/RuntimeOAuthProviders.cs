// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai.Auth.OAuth;

namespace Tau.Ai.Auth;

public sealed partial class ProviderAuthResolver
{
    /// <summary>【AI】【扩展认证】替换本会话的 OAuth 覆盖，不修改共享内置注册表。</summary>
    /// <param name="providers">当前扩展完整认证注册快照。</param>
    public void SetRuntimeOAuthProviders(IReadOnlyDictionary<string, IOAuthProvider> providers)
    {
        lock (_runtimeAuthenticationGate) Volatile.Write(ref _runtimeAuthentication, _runtimeAuthentication with
            { OAuth = new Dictionary<string, IOAuthProvider>(providers, StringComparer.OrdinalIgnoreCase) });
    }

    /// <summary>【AI】【离线刷新凭据】只读取原始持久凭据，不解析环境、命令或认证回调，供缓存恢复阶段使用。</summary>
    /// <param name="provider">提供方。</param><returns>独立原生凭据对象，未存储时为空。</returns>
    public JsonElement? ReadStoredRefreshCredential(string provider)
    {
        var stored = GetStoredAuthEntry(provider);
        if (stored?.OAuth is { } oauth) return OAuthCredentialJson.Write(oauth);
        return stored is null ? null : WriteRefreshApiKeyCredential(stored.ApiKey, stored.Env);
    }

    /// <summary>【AI】【刷新认证】返回保留原始类型的刷新凭据，离线刷新绝不触发令牌网络刷新。</summary>
    /// <param name="provider">提供方。</param><param name="allowNetwork">是否允许刷新过期令牌。</param>
    /// <param name="token">取消信号。</param><param name="resolveLegacyOfflineConfiguration">兼容旧扩展离线回调对配置密钥的依赖；原生路径保持关闭。</param>
    /// <returns>原生凭据对象或空值。</returns>
    public async Task<JsonElement?> ResolveRefreshCredentialAsync(string provider, bool allowNetwork, CancellationToken token = default,
        bool resolveLegacyOfflineConfiguration = false)
    {
        token.ThrowIfCancellationRequested();
        var entry = GetStoredAuthEntry(provider);
        // 1. 【AI】【离线刷新凭据】仅恢复原始缓存凭据，不执行认证委托或命令配置
        if (!allowNetwork && !resolveLegacyOfflineConfiguration) return entry?.OAuth is { } offlineOAuth ? OAuthCredentialJson.Write(offlineOAuth)
            : entry is null ? null : WriteRefreshApiKeyCredential(entry.ApiKey, entry.Env);
        if (entry?.OAuth is { } credentials)
        {
            if (!allowNetwork) return OAuthCredentialJson.Write(credentials);
            var oauth = GetOAuthProvider(provider);
            if (oauth is null) return null;
            // 2. 【AI】【目录刷新认证】目录发现只刷新已过期令牌，不采用聊天请求的五分钟余量
            if (credentials.IsExpired(TimeSpan.Zero))
            {
                var refreshed = await _credentialStore.RefreshAsync(provider, oauth, token, TimeSpan.Zero, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                if (refreshed is null) return null;
                credentials = refreshed;
            }
            return OAuthCredentialJson.Write(credentials);
        }
        // 3. 【AI】【联网刷新凭据】原生认证解析可能只返回环境或请求头，非空解析结果仍代表已授权
        if (allowNetwork && GetApiKeyProvider(provider) is { } native)
        {
            var input = entry is null ? null : new ApiKeyCredential(entry.ApiKey, entry.Env);
            var resolved = await native.ResolveAsync(new(provider, input, null, token)).WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return resolved is null ? null : WriteRefreshApiKeyCredential(resolved.ApiKey, resolved.Env);
        }
        if (HasNativeAuthentication(provider)) return null;
        var key = entry?.ApiKey ?? (_configurationStore.HasRuntimeApiKey(provider)
            ? _configurationStore.ResolveProviderApiKey(provider, null)
            : EnvironmentApiKeyResolver.GetApiKey(provider) ?? _configurationStore.ResolveProviderApiKey(provider, null));
        if (key is null && entry?.Env is null) return null;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("type", "api_key");
            if (key is not null) writer.WriteString("key", key);
            if (entry?.Env is { } env)
            {
                writer.WriteStartObject("env");
                foreach (var (name, value) in env) writer.WriteString(name, value);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>【AI】【刷新凭据序列化】保留可选密钥和环境，允许无需密钥的已授权提供方返回空字段凭据。</summary>
    /// <param name="key">可选密钥。</param><param name="environment">可选提供方环境。</param><returns>独立 api_key 凭据。</returns>
    private static JsonElement WriteRefreshApiKeyCredential(string? key, IReadOnlyDictionary<string, string>? environment)
    {
        var value = new JsonObject { ["type"] = "api_key" };
        if (key is not null) value["key"] = key;
        if (environment is not null)
        {
            var fields = new JsonObject();
            foreach (var pair in environment) fields[pair.Key] = pair.Value;
            value["env"] = fields;
        }
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }
}
