// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai.Auth;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【原生 API key】创建会话认证定义，状态检查只读取刷新阶段发布的非秘密快照。</summary>
    /// <param name="entry">提供方注册。</param><param name="metadata">认证元数据。</param><returns>认证解析定义。</returns>
    private ApiKeyAuthDefinition CreateApiKeyAuthDefinition(JsonElement entry, JsonElement metadata)
    {
        var provider = entry.GetProperty("id").GetString()!;
        var file = entry.GetProperty("filePath").GetString()!;
        var version = ReadInt(entry, "version");
        var generation = ResetGeneration;
        var configured = ReadBool(metadata, "configured");
        var source = ReadString(metadata, "source") ?? "extension";
        var hasLogin = ReadBool(metadata, "hasLogin");
        return new(ReadString(metadata, "name") ?? provider, async context =>
        {
            var credential = context.Credential is null ? null : new JsonObject { ["type"] = "api_key", ["key"] = context.Credential.Key,
                ["env"] = context.Credential.Env is null ? null : JsonSerializer.SerializeToNode(context.Credential.Env) };
            var fields = new JsonObject { ["apiKeyCredential"] = credential, ["environment"] = context.Environment is null ? null : JsonSerializer.SerializeToNode(context.Environment) };
            var value = await InvokeProviderAuthAsync(file, provider, version, generation, "resolveApiKey", null, null, context.CancellationToken, fields).ConfigureAwait(false);
            if (value.ValueKind == JsonValueKind.Null) return null;
            var auth = value.GetProperty("auth");
            return new(ReadString(auth, "apiKey"), ReadAuthDictionary(auth, "headers"), ReadString(auth, "baseUrl"), ReadAuthDictionary(value, "env"), ReadString(value, "source"));
        }, _ => Task.FromResult<ProviderAuthStatus?>(new(provider, configured, source, false, hasLogin,
            configured ? "Provider authentication is configured." : "Provider authentication is not configured.")), hasLogin ? async interaction =>
        {
            var value = await InvokeProviderAuthAsync(file, provider, version, generation, "loginApiKey", null,
                new CodingAgentInteractionOAuthCallbacks(interaction), interaction.CancellationToken).ConfigureAwait(false);
            if (ReadString(value, "type") != "api_key") throw new InvalidOperationException("API key login must return an api_key credential.");
            return new ApiKeyCredential(ReadString(value, "key"), ReadAuthDictionary(value, "env"));
        } : null);
    }

    /// <summary>【CodingAgent】【认证字段】读取字符串请求头或提供方环境字典。</summary>
    /// <param name="value">认证对象。</param><param name="name">属性。</param><returns>字段字典或空值。</returns>
    private static IReadOnlyDictionary<string, string>? ReadAuthDictionary(JsonElement value, string name) =>
        value.TryGetProperty(name, out var fields) && fields.ValueKind == JsonValueKind.Object
            ? fields.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.GetString()!, StringComparer.OrdinalIgnoreCase) : null;
}
