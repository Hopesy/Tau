// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai.Auth;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【提示预检】在启动钩子和自动压缩之前检查认证配置，不提前解析已配置的秘密或刷新 OAuth。</summary>
    /// <param name="token">本次提示取消信号。</param><returns>认证检查完成任务；缺少认证时抛出异常。</returns>
    private async Task ValidatePromptAuthenticationAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // 1. 【CodingAgent】【认证来源】宿主提供的动态密钥入口由核心循环按实际请求模型调用
        if (_config.GetApiKeyAsync is not null) return;
        var provider = Model.Provider;
        if (_authResolver.IsUsingOAuth(provider) && _authResolver.GetOAuthProvider(provider) is not null) return;
        if (GetAuthStatus().IsConfigured) return;
        // 2. 【CodingAgent】【实时认证】未配置快照再次调用原生检查，允许运行期间新增环境凭据
        var environment = _config.ConfigurationStore?.GetRequestEnvironment(Model, _config.StreamOptions?.Env) ?? _config.StreamOptions?.Env;
        if (_virtualModelRuntime is { } extensions && await extensions.CheckNativePromptAuthAsync(provider, environment, token).ConfigureAwait(false) == true) return;
        token.ThrowIfCancellationRequested();
        if (_authResolver.IsUsingOAuth(provider))
            throw new ProviderAuthException("oauth", $"Authentication failed for \"{provider}\". Credentials may have expired or network is unavailable. Run '/login {provider}' to re-authenticate.");
        throw new ProviderAuthException("api_key", $"No API key found for \"{provider}\". Use '/login {provider}' or configure provider credentials.");
    }
}

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【原生认证预检】捕获提供方版本和原始凭据，执行可取消的 check 或 resolve 回退。</summary>
    /// <param name="provider">提供方标识。</param><param name="environment">请求环境覆盖。</param><param name="token">取消信号。</param>
    /// <returns>是否配置认证；非原生提供方返回空值。</returns>
    internal async Task<bool?> CheckNativePromptAuthAsync(string provider, IReadOnlyDictionary<string, string>? environment, CancellationToken token)
    {
        JsonElement entry;
        long generation;
        lock (_providerRegistrationGate)
        {
            entry = _providerRegistrations?.EnumerateArray().FirstOrDefault(item => ReadString(item, "id") == provider) ?? default;
            if (entry.ValueKind != JsonValueKind.Object || !ReadBool(entry, "isNative")) return null;
            entry = entry.Clone(); generation = ResetGeneration;
        }
        var credential = _providerCatalog is null ? null : await _providerCatalog.AuthResolver.ResolveRefreshCredentialAsync(provider, allowNetwork: false, token).ConfigureAwait(false);
        var fields = new JsonObject
        {
            ["credential"] = credential is { } value ? JsonNode.Parse(value.GetRawText()) : null,
            ["environment"] = environment is null ? null : JsonSerializer.SerializeToNode(environment)
        };
        var result = await InvokeProviderAuthAsync(entry.GetProperty("filePath").GetString()!, provider, ReadInt(entry, "version"), generation,
            "checkAuth", null, null, token, fields).ConfigureAwait(false);
        return result.GetBoolean();
    }
}
