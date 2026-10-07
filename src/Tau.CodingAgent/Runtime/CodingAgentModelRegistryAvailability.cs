// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai;
using Tau.Ai.Providers;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【模型权限】应用原生扩展或内置 Copilot 账户清单，虚拟路由保留独立权限。</summary>
    /// <param name="provider">提供方。</param><param name="models">目录或目录子集。</param><returns>账户可用模型，保持输入顺序。</returns>
    public IReadOnlyList<Model> FilterAvailableModels(string provider, IReadOnlyList<Model> models)
    {
        // 1. 【CodingAgent】【原生模型过滤】以完整目录执行聊天过滤，再与调用方子集相交，避免改变过滤上下文
        if (_virtualModelRuntime?.FilterNativeChatModelsAsync(provider, _modelCatalog.GetModels(provider), CancellationToken.None).GetAwaiter().GetResult() is { } native)
        {
            var ids = native.Select(model => model.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
            return models.Where(model => model.Api == "pi-virtual" || ids.Contains(model.Id)).ToArray();
        }
        if (provider != "github-copilot" || _authResolver.HasNativeAuthentication(provider)) return models;
        var allowed = GitHubCopilotModelAccess.FilterCredential(models, _authResolver.ReadStoredRefreshCredential(provider)).ToHashSet();
        return models.Where(model => model.Api == "pi-virtual" || allowed.Contains(model)).ToArray();
    }

    /// <summary>【CodingAgent】【权限快照】传输内置账户模型 ID，不传输令牌，扩展接管时由 Node 跳过内置规则。</summary>
    /// <param name="writer">运行时快照写入器。</param>
    internal void WriteBuiltInModelPermissions(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("builtInModelPermissions");
        if (GitHubCopilotModelAccess.ReadAvailableModelIds(_authResolver.ReadStoredRefreshCredential("github-copilot")) is { } ids)
        {
            writer.WriteStartArray("github-copilot");
            foreach (var id in ids) writer.WriteStringValue(id);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    /// <summary>【CodingAgent】【能力可用性】异步检查全部目标提供方，再按类型筛选其凭据允许使用的模型。</summary>
    /// <param name="request">能力类型与可选提供方。</param><param name="extensions">扩展运行时。</param><param name="token">取消信号。</param>
    /// <returns>原生模型数组，不包含凭据。</returns>
    internal async Task<IReadOnlyList<JsonElement>> GetAvailableRegistryModelsAsync(JsonElement request, CodingAgentJavaScriptExtensionRuntime extensions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var type = request.GetProperty("type").GetString();
        var provider = request.TryGetProperty("provider", out var field) ? field.GetString() : null;
        var groups = _modelCatalog.GetAllModels(provider).GroupBy(model => model.Provider).ToArray();
        var values = await Task.WhenAll(groups.Select(async group =>
        {
            // 1. 【CodingAgent】【能力可用性】原生 check/filter 在 Node 中执行，检查过程不刷新 OAuth
            var native = await extensions.GetAvailableNativeModelsAsync(group.Key, group.ToArray(), token).ConfigureAwait(false);
            if (native is not null) return native;
            token.ThrowIfCancellationRequested();
            var configured = _authResolver.IsUsingOAuth(group.Key) && _authResolver.GetOAuthProvider(group.Key) is not null ||
                _authResolver.GetStatus(group.Key, explicitApiKey: group.Key == Model.Provider ? _config.StreamOptions?.ApiKey : null).IsConfigured;
            return configured ? FilterAvailableModels(group.Key, group.ToArray()).Select(CodingAgentExtensionSessionBridge.SerializeExtensionModel).ToArray() : [];
        })).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return values.SelectMany(models => models).Where(model =>
            (model.TryGetProperty("type", out var modelType) && modelType.ValueKind == JsonValueKind.String ? modelType.GetString() : ModelTypes.Chat) == type).ToArray();
    }
}

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【聊天可用性】执行原生聊天过滤，不重复认证检查，也不刷新 OAuth。</summary>
    /// <param name="provider">提供方。</param><param name="models">完整聊天目录。</param><param name="token">取消信号。</param>
    /// <returns>过滤后的聊天模型；非原生提供方返回空引用。</returns>
    internal Task<IReadOnlyList<JsonElement>?> FilterNativeChatModelsAsync(string provider, IReadOnlyList<Model> models, CancellationToken token) =>
        InvokeNativeModelAvailabilityAsync(provider, models, "filterModels", token);

    /// <summary>【CodingAgent】【原生可用性】捕获注册版本和原始凭据，交给原生认证检查与模型过滤回调。</summary>
    /// <param name="provider">提供方。</param><param name="models">规范化目录。</param><param name="token">取消信号。</param>
    /// <returns>可用模型；非原生提供方返回空引用。</returns>
    internal Task<IReadOnlyList<JsonElement>?> GetAvailableNativeModelsAsync(string provider, IReadOnlyList<Model> models, CancellationToken token) =>
        InvokeNativeModelAvailabilityAsync(provider, models, "availableModels", token);

    /// <summary>【CodingAgent】【过滤桥接】捕获注册版本后释放状态锁，通过独立认证通道传递当前凭据。</summary>
    /// <param name="provider">提供方。</param><param name="models">待查询完整目录。</param><param name="operation">聊天过滤或跨能力检查。</param>
    /// <param name="token">取消信号。</param><returns>原生回调结果，非原生注册返回空引用。</returns>
    private async Task<IReadOnlyList<JsonElement>?> InvokeNativeModelAvailabilityAsync(string provider, IReadOnlyList<Model> models, string operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        JsonElement entry;
        long generation;
        lock (_providerRegistrationGate)
        {
            entry = _providerRegistrations?.EnumerateArray().FirstOrDefault(item => ReadString(item, "id") == provider) ?? default;
            if (entry.ValueKind != JsonValueKind.Object || !ReadBool(entry, "isNative")) return null;
            entry = entry.Clone();
            generation = ResetGeneration;
        }
        var credential = _providerCatalog is null ? null : await _providerCatalog.AuthResolver.ResolveRefreshCredentialAsync(provider, allowNetwork: false, token).ConfigureAwait(false);
        var fields = new JsonObject
        {
            ["credential"] = credential is { } value ? JsonNode.Parse(value.GetRawText()) : null,
            ["models"] = new JsonArray(models.Select(model => JsonNode.Parse(CodingAgentExtensionSessionBridge.SerializeExtensionModel(model).GetRawText())).ToArray())
        };
        var result = await InvokeProviderAuthAsync(entry.GetProperty("filePath").GetString()!, provider, ReadInt(entry, "version"), generation,
            operation, null, null, token, fields).ConfigureAwait(false);
        return result.EnumerateArray().Select(model => model.Clone()).ToArray();
    }
}
