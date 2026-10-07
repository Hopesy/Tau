// 作者：xxx
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tau.Ai.Registry;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【模型刷新】刷新状态及逐提供方错误。</summary>
internal sealed record CodingAgentProviderRefreshResult(
    [property: JsonPropertyName("aborted")] bool Aborted,
    [property: JsonPropertyName("errors")] IReadOnlyDictionary<string, string> Errors);

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    private Tau.Ai.Registry.ModelsStore _modelsStore = new Tau.Ai.Registry.InMemoryModelsStore();
    private readonly ConcurrentDictionary<string, ProviderRefreshCall> _providerRefreshCalls = new();
    private readonly Dictionary<string, long> _providerRefreshSequences = new(StringComparer.Ordinal);
    private long _providerRefreshSequence;
    private readonly ConcurrentDictionary<string, CodingAgentExtensionDiagnostic> _providerRefreshErrors = new(StringComparer.Ordinal);

    /// <summary>【CodingAgent】【刷新诊断】当前提供方的刷新错误，成功刷新或注销后移除。</summary>
    internal IReadOnlyList<CodingAgentExtensionDiagnostic> ProviderRefreshDiagnostics => _providerRefreshErrors.Values.ToArray();

    /// <summary>【CodingAgent】【目录缓存】配置独立会话使用的模型缓存。</summary>
    /// <param name="store">模型目录存储。</param>
    internal void ConfigureModelsStore(Tau.Ai.Registry.ModelsStore store) => _modelsStore = store;

    /// <summary>【CodingAgent】【模型刷新】重新读取文件并并行刷新独立提供方，同提供方的新刷新主动取消旧操作。</summary>
    /// <param name="providers">可选提供方筛选。</param><param name="allowNetwork">是否允许回调联网。</param>
    /// <param name="force">是否忽略提供方新鲜度检查。</param><param name="token">共享取消信号。</param>
    /// <returns>取消状态及逐提供方错误。</returns>
    internal async Task<CodingAgentProviderRefreshResult> RefreshProviderModelsAsync(IReadOnlyList<string>? providers, bool allowNetwork, bool force, CancellationToken token)
    {
        var errors = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        if (token.IsCancellationRequested) return new(true, errors);
        JsonElement[] entries;
        lock (_providerRegistrationGate)
        {
            _providerCatalog?.ReloadConfiguration();
            _sessionBridge?.InvalidateProviderModels();
            entries = _providerRegistrations is { ValueKind: JsonValueKind.Array } registrations
                ? registrations.EnumerateArray().Where(entry => ReadBool(entry, "hasRefreshModels") &&
                    (providers is null || providers.Contains(entry.GetProperty("id").GetString()!, StringComparer.Ordinal))).Select(entry => entry.Clone()).ToArray() : [];
        }
        await Task.WhenAll(entries.Select(async entry =>
        {
            if (token.IsCancellationRequested) return;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var provider = entry.GetProperty("id").GetString()!;
            var callId = Guid.NewGuid().ToString("N");
            ProviderRefreshCall call;
            lock (_providerRegistrationGate)
            {
                var sequence = ++_providerRefreshSequence;
                _providerRefreshSequences[provider] = sequence;
                call = new(provider, ReadInt(entry, "version"), ResetGeneration, sequence, lifetime.Token, lifetime);
                _providerRefreshCalls[callId] = call;
                CancelObsoleteProviderRefreshes();
            }
            try
            {
                var auth = _providerCatalog?.AuthResolver;
                if (ReadBool(entry, "isNative"))
                {
                    // 1. 【CodingAgent】【离线优先】即使凭据读取失败，也先让提供方恢复可用的模型缓存
                    JsonElement? storedCredential = null;
                    Exception? credentialError = null;
                    try { storedCredential = auth?.ReadStoredRefreshCredential(provider); }
                    catch (Exception error) { credentialError = error; }
                    await RunProviderRefreshPhaseAsync(entry, callId, call, false, null, storedCredential, checkAuth: !allowNetwork).ConfigureAwait(false);
                    if (credentialError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(credentialError).Throw();
                    bool current;
                    lock (_providerRegistrationGate) current = IsCurrentProviderRefresh(call);
                    if (allowNetwork && current)
                    {
                        // 2. 【CodingAgent】【联网认证】有效的原生认证解析才允许联网刷新，离线阶段不传 force
                        var oauth = storedCredential is { } original && ReadString(original, "type") == "oauth";
                        var canResolve = oauth ? entry.TryGetProperty("oauth", out _) :
                            entry.TryGetProperty("apiKeyAuth", out var apiKey) && ReadBool(apiKey, "hasResolve");
                        var credential = canResolve && auth is not null
                            ? await auth.ResolveRefreshCredentialAsync(provider, true, call.Token).ConfigureAwait(false) : null;
                        await RunProviderRefreshPhaseAsync(entry, callId, call, credential is not null, credential is null ? null : force,
                            credential ?? storedCredential, checkAuth: true, metadataOnly: credential is null).ConfigureAwait(false);
                    }
                }
                else
                {
                    var credential = auth is null ? null : await auth.ResolveRefreshCredentialAsync(provider, allowNetwork, call.Token,
                        resolveLegacyOfflineConfiguration: true).ConfigureAwait(false);
                    await RunProviderRefreshPhaseAsync(entry, callId, call, allowNetwork, force, credential, checkAuth: true).ConfigureAwait(false);
                }
                lock (_providerRegistrationGate)
                {
                    if (IsCurrentProviderRefresh(call)) _providerRefreshErrors.TryRemove(provider, out _);
                }
            }
            catch (OperationCanceledException) when (call.Token.IsCancellationRequested) { }
            catch (Exception error)
            {
                lock (_providerRegistrationGate)
                    if (IsCurrentProviderRefresh(call))
                    {
                        errors[provider] = error.Message;
                        _providerRefreshErrors[provider] = new("error",
                            $"Provider '{provider}' refresh failed: {error.Message}", entry.GetProperty("filePath").GetString()!, "temporary");
                    }
            }
            finally { _providerRefreshCalls.TryRemove(callId, out _); }
        })).ConfigureAwait(false);
        return new(token.IsCancellationRequested, new Dictionary<string, string>(errors, StringComparer.Ordinal));
    }

    /// <summary>【CodingAgent】【旧刷新取消】在注册锁内标记已经失效的操作，回调在线程池执行以免阻塞注册提交。</summary>
    private void CancelObsoleteProviderRefreshes()
    {
        foreach (var call in _providerRefreshCalls.Values)
            if (!IsCurrentProviderRefresh(call) && !call.Token.IsCancellationRequested) _ = CancelProviderRefreshAsync(call);
    }

    /// <summary>【CodingAgent】【刷新取消观察】等待取消回调，允许操作已完成释放或外部回调失败，不影响替代操作。</summary>
    /// <param name="call">被替代的刷新。</param><returns>取消回调完成任务。</returns>
    private static async Task CancelProviderRefreshAsync(ProviderRefreshCall call)
    {
        try { await call.Lifetime.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
    }

    /// <summary>【CodingAgent】【刷新单阶段】重新读取缓存并运行离线、联网或仅认证快照更新，保留同一次刷新的代次。</summary>
    /// <param name="entry">原始注册。</param><param name="callId">发布标识。</param><param name="call">刷新上下文。</param>
    /// <param name="allowNetwork">是否为联网阶段。</param><param name="force">联网强制刷新标志。</param>
    /// <param name="credential">当前阶段凭据。</param><param name="checkAuth">是否更新认证可用性。</param>
    /// <param name="metadataOnly">只更新认证元数据，不重复执行提供方刷新。</param><returns>阶段完成任务。</returns>
    private async Task RunProviderRefreshPhaseAsync(JsonElement entry, string callId, ProviderRefreshCall call,
        bool allowNetwork, bool? force, JsonElement? credential, bool checkAuth, bool metadataOnly = false)
    {
        lock (_providerRegistrationGate) if (!IsCurrentProviderRefresh(call)) return;
        var stored = await _modelsStore.ReadAsync(call.Provider, new(call.Token)).ConfigureAwait(false);
        var payload = new JsonObject
        {
            ["providerId"] = call.Provider, ["callId"] = callId, ["version"] = call.Version,
            ["allowNetwork"] = allowNetwork, ["checkAuth"] = checkAuth, ["metadataOnly"] = metadataOnly,
            ["credential"] = credential is null ? null : JsonNode.Parse(credential.Value.GetRawText()),
            ["stored"] = SerializeStoredModels(stored)
        };
        if (force is not null) payload["force"] = force;
        var models = new JsonArray();
        foreach (var model in _providerCatalog?.GetAllModels(call.Provider) ?? [])
            models.Add(JsonNode.Parse(CodingAgentExtensionSessionBridge.SerializeExtensionModel(model).GetRawText()));
        payload["models"] = models;
        var result = await ExecuteAsync(BuildPayload("refreshProvider", entry.GetProperty("filePath").GetString()!, _cwd,
            toolArgs: JsonSerializer.SerializeToElement(payload)), call.Token, expectedGeneration: call.Generation, timeout: Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        using var document = JsonDocument.Parse(result.ResultJson);
        if (!ReadBool(document.RootElement, "ok")) throw new InvalidOperationException(ReadString(document.RootElement, "error"));
        lock (_providerRegistrationGate)
        {
            if (IsCurrentProviderRefresh(call) && document.RootElement.TryGetProperty("providers", out var updated))
            {
                var replacement = updated.EnumerateArray().FirstOrDefault(item => ReadString(item, "id") == call.Provider);
                if (replacement.ValueKind == JsonValueKind.Object) ApplyProviderRefreshSnapshot(call, replacement);
            }
        }
    }

    /// <summary>【CodingAgent】【目录发布】先检查并持久化，再接收 Node 已执行更新的目录快照；两阶段均拒绝过期刷新。</summary>
    /// <param name="request">刷新标识、可选 persist 对象或单提供方快照。</param><returns>发布是否仍然有效。</returns>
    private bool PublishProviderModels(JsonElement request)
    {
        if (!_providerRefreshCalls.TryGetValue(request.GetProperty("callId").GetString()!, out var call)) return false;
        lock (_providerRegistrationGate)
        {
            if (!IsCurrentProviderRefresh(call)) return false;
            if (request.TryGetProperty("persist", out var persist))
            {
                if (persist.ValueKind == JsonValueKind.Null) _modelsStore.DeleteAsync(call.Provider, new(call.Token)).GetAwaiter().GetResult();
                else
                {
                    var models = ModelConfigurationStore.ReadProviderModels(call.Provider, persist.GetProperty("models"));
                    var stored = new ModelsStoreEntry(models, ReadOptionalLong(persist, "lastModified"), ReadOptionalLong(persist, "checkedAt"), ReadString(persist, "etag"));
                    _modelsStore.WriteAsync(call.Provider, stored, new(call.Token)).GetAwaiter().GetResult();
                }
            }
            if (!IsCurrentProviderRefresh(call)) return false;
            if (request.TryGetProperty("provider", out var provider)) ApplyProviderRefreshSnapshot(call, provider);
            return true;
        }
    }

    /// <summary>【CodingAgent】【中间目录提交】只替换当前刷新提供方，保留其他提供方和独立注册的虚拟模型。</summary>
    /// <param name="call">已经校验的刷新上下文。</param><param name="provider">工作进程更新后的完整提供方快照。</param>
    private void ApplyProviderRefreshSnapshot(ProviderRefreshCall call, JsonElement provider)
    {
        if (provider.ValueKind != JsonValueKind.Object || ReadString(provider, "id") != call.Provider || ReadInt(provider, "version") != call.Version)
            throw new InvalidOperationException("Provider refresh publication does not match its registration.");
        var merged = new JsonArray();
        foreach (var current in _providerRegistrations!.Value.EnumerateArray())
        {
            var entry = JsonNode.Parse(current.GetRawText())!.AsObject();
            if (ReadString(current, "id") == call.Provider)
            {
                var updated = JsonNode.Parse(provider.GetRawText())!.AsObject();
                // 1. 【CodingAgent】【独立路由保留】刷新只拥有实体目录，不能覆盖刷新等待期间注册的虚拟路由
                if (entry["virtualModels"] is { } routes) updated["virtualModels"] = routes.DeepClone();
                else updated.Remove("virtualModels");
                entry = updated;
            }
            merged.Add(entry);
        }
        using var snapshot = JsonDocument.Parse(merged.ToJsonString());
        ApplyProviderRegistrationsCore(snapshot.RootElement);
    }

    /// <summary>【CodingAgent】【刷新代次】拒绝已取消、已重载、被新刷新替代或重新注册的结果。</summary>
    /// <param name="call">刷新上下文。</param><returns>仍属于当前提供方时为真。</returns>
    private bool IsCurrentProviderRefresh(ProviderRefreshCall call) => !call.Token.IsCancellationRequested && call.Generation == ResetGeneration &&
        _providerRefreshSequences.GetValueOrDefault(call.Provider) == call.Sequence &&
        _providerRegistrations is { ValueKind: JsonValueKind.Array } entries && entries.EnumerateArray().Any(entry =>
            ReadString(entry, "id") == call.Provider && ReadInt(entry, "version") == call.Version);

    /// <summary>【CodingAgent】【目录缓存】序列化原生模型和缓存时间，输入始终是独立存储快照。</summary>
    /// <param name="stored">存储条目。</param><returns>原生缓存对象，缺少时为空。</returns>
    private static JsonObject? SerializeStoredModels(ModelsStoreEntry? stored)
    {
        if (stored is null) return null;
        var models = new JsonArray();
        foreach (var model in stored.Models) models.Add(JsonNode.Parse(CodingAgentExtensionSessionBridge.SerializeExtensionModel(model).GetRawText()));
        return new() { ["models"] = models, ["lastModified"] = stored.LastModified, ["checkedAt"] = stored.CheckedAt, ["etag"] = stored.Etag };
    }

    /// <summary>【CodingAgent】【目录缓存】读取可选毫秒时间。</summary>
    /// <param name="value">对象。</param><param name="name">属性名称。</param><returns>整数或空值。</returns>
    private static long? ReadOptionalLong(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var result) ? result : null;

    /// <summary>【CodingAgent】【目录发布】处理无需会话模型已选定的内部发布请求。</summary>
    /// <param name="request">工作进程请求。</param><returns>包含发布结果的响应。</returns>
    private string HandleProviderPublication(JsonElement request)
    {
        try { return JsonSerializer.Serialize(new { id = request.GetProperty("id").GetString(), ok = true, value = PublishProviderModels(request) }); }
        catch (Exception error) { return JsonSerializer.Serialize(new { id = request.GetProperty("id").GetString(), ok = false, error = error.Message }); }
    }

    /// <summary>【CodingAgent】【刷新身份】将注册版本、刷新代次和独立取消生命周期绑定，保留可在释放后读取的令牌。</summary>
    private sealed record ProviderRefreshCall(string Provider, int Version, long Generation, long Sequence,
        CancellationToken Token, CancellationTokenSource Lifetime);
}
