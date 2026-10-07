// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.Ai.Providers;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【目录认证】显式请求实际认证值；普通目录快照从不调用此方法。</summary>
    /// <param name="provider">提供方。</param><param name="modelId">可选模型 ID，仅查询提供方时为空。</param>
    /// <param name="token">取消信号。</param><param name="type">模型能力，缺省为聊天。</param><returns>符合原生 AuthResult 的对象或空值。</returns>
    internal object? ResolveRegistryAuth(string provider, string? modelId, CancellationToken token, string type = ModelTypes.Chat)
    {
        var model = modelId is not null ? _modelCatalog.GetModelOfType(type, provider, modelId) ?? throw new KeyNotFoundException($"Model '{provider}/{modelId}' of type '{type}' is not registered.")
            : new Model { Id = string.Empty, Name = provider, Provider = provider, Api = string.Empty };
        var auth = StreamFunctions.ResolveRequestAuthentication(model, new StreamOptions { Signal = token,
            ApiKey = string.Equals(provider, Model.Provider, StringComparison.OrdinalIgnoreCase) ? _config.StreamOptions?.ApiKey : null }, _modelCatalog.ConfigurationStore, _authResolver);
        if (auth.ApiKey is null && auth.Headers is not { Count: > 0 } && auth.BaseUrl is null && !_authResolver.GetStatus(provider).IsConfigured) return null;
        return new { auth = new { apiKey = auth.ApiKey, headers = auth.Headers, baseUrl = auth.BaseUrl }, env = auth.Env, source = auth.Source };
    }

    /// <summary>【CodingAgent】【目录认证】写入非秘密状态，存储凭据与请求时有效性分开表示。</summary>
    /// <param name="writer">快照写入器。</param><returns>当前可用提供方，供同一个快照复用。</returns>
    internal IReadOnlyList<string> WriteRegistryAuthMetadata(Utf8JsonWriter writer)
    {
        var available = new List<string>();
        var virtualAvailable = new List<string>();
        writer.WriteStartObject("providerAuth");
        foreach (var metadata in _authResolver.GetMetadataSnapshot(GetAuthProviders(), Model, _config.StreamOptions?.ApiKey))
        {
            var status = metadata.Status;
            var stored = metadata.HasStoredCredential;
            if (status.IsConfigured || metadata.UsesOAuth && _authResolver.GetOAuthProvider(status.Provider) is not null) available.Add(status.Provider);
            if (status.IsConfigured && status.Source == "virtual") virtualAvailable.Add(status.Provider);
            writer.WriteStartObject(status.Provider);
            writer.WriteBoolean("configured", stored || status.IsConfigured);
            writer.WriteBoolean("usesOAuth", metadata.UsesOAuth);
            if (stored) writer.WriteString("source", "stored");
            else if (status.IsConfigured)
            {
                writer.WriteString("source", status.Source switch { "explicit" => "runtime", "models.json" or "extension" => status.Message.Contains("command-backed", StringComparison.Ordinal) ? "models_json_command" : "models_json_key", _ => "environment" });
                writer.WriteString("label", status.Source);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        // 1. 【CodingAgent】【虚拟认证归属】标记仅由路由生成的免密状态，实时注销时不影响实体提供方的认证
        writer.WriteStartArray("virtualAvailableProviders");
        foreach (var provider in virtualAvailable) writer.WriteStringValue(provider);
        writer.WriteEndArray();
        return available;
    }
}
