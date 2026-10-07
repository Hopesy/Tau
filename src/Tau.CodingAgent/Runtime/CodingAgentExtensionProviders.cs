// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.Ai.Registry;
using Tau.Ai.Providers;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Auth;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentExtensionCommandStore
{
    /// <summary>【CodingAgent】【提供方启动】在选择初始模型之前绑定扩展提供方配置。</summary>
    /// <param name="catalog">当前会话独立模型目录。</param>
    /// <param name="modelsStore">可选持久模型目录存储。</param>
    public void ConfigureModelCatalog(ModelCatalog catalog, Tau.Ai.Registry.ModelsStore? modelsStore = null)
    {
        _javaScriptRuntime.ConfigureModelCatalog(catalog);
        if (modelsStore is not null) _javaScriptRuntime.ConfigureModelsStore(modelsStore);
    }

    /// <summary>【CodingAgent】【启动目录】在选择初始模型前执行扩展离线刷新。</summary>
    /// <param name="token">启动取消信号。</param><returns>刷新任务。</returns>
    internal async Task InitializeProviderModelsAsync(CancellationToken token) =>
        await _javaScriptRuntime.RefreshProviderModelsAsync(null, allowNetwork: false, force: false, token).ConfigureAwait(false);
}

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    private ModelCatalog? _providerCatalog;
    private JsonElement? _providerRegistrations;
    private string? _providerRegistrationFingerprint;
    private ProviderRegistry? _providerRegistry;
    private IReadOnlyDictionary<string, IStreamProvider> _baselineProviderImplementations = new Dictionary<string, IStreamProvider>();
    private ModelCatalog? _factoryDefaults;
    private readonly object _providerRegistrationGate = new();

    /// <summary>【CodingAgent】【工厂校验】提供原始协议和地址供同步注册校验，不执行认证或传递密钥。</summary>
    /// <param name="writer">请求 JSON 写入器。</param>
    private void WriteFactoryProviderDefaults(Utf8JsonWriter writer)
    {
        var catalog = _providerCatalog ?? (_factoryDefaults ??= new(configurationStore: new ModelConfigurationStore([])));
        writer.WriteStartObject("providerDefaults");
        foreach (var provider in catalog.GetAllProviders())
        {
            writer.WriteStartArray(provider);
            foreach (var model in catalog.GetRuntimeBaselineModels(provider))
            {
                writer.WriteStartObject();
                writer.WriteString("id", model.Id);
                writer.WriteString("type", ModelTypes.GetModelType(model));
                writer.WriteString("api", model.Api);
                writer.WriteString("baseUrl", model.BaseUrl);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    /// <summary>【CodingAgent】【提供方启动】将工厂阶段的注册应用到会话目录。</summary>
    /// <param name="catalog">会话独立目录。</param>
    /// <param name="registry">可选会话协议及提供方路由表。</param>
    internal void ConfigureModelCatalog(ModelCatalog catalog, ProviderRegistry? registry = null)
    {
        if (ReferenceEquals(_providerCatalog, catalog) && (registry is null || ReferenceEquals(_providerRegistry, registry))) return;
        _providerCatalog = catalog;
        if (registry is not null && !ReferenceEquals(_providerRegistry, registry))
        {
            _providerRegistry = registry;
            _baselineProviderImplementations = registry.GetModelProviders();
        }
        _providerRegistrationFingerprint = null;
        ApplyProviderRegistrations(_providerRegistrations);
    }

    /// <summary>【CodingAgent】【提供方同步】应用完整扩展注册快照，先验证目录再保存桥接状态。</summary>
    /// <param name="registrations">当前注册数组；空值表示移除全部扩展覆盖。</param>
    internal void ApplyProviderRegistrations(JsonElement? registrations)
    {
        lock (_providerRegistrationGate) ApplyProviderRegistrationsCore(registrations);
    }

    /// <summary>【CodingAgent】【注册提交】在提供方状态锁内替换目录及实现。</summary>
    /// <param name="registrations">完整注册快照。</param>
    private void ApplyProviderRegistrationsCore(JsonElement? registrations)
    {
        var fingerprint = registrations?.GetRawText() ?? "[]";
        if (_providerRegistrationFingerprint == fingerprint) return;
        var providers = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var virtuals = new List<Model>();
        if (registrations is { ValueKind: JsonValueKind.Array } entries)
            foreach (var entry in entries.EnumerateArray())
            {
                if (!ReadBool(entry, "isVirtualOnly")) providers[entry.GetProperty("id").GetString()!] = entry.GetProperty("config");
                if (entry.TryGetProperty("virtualModels", out var routes))
                    foreach (var route in routes.EnumerateArray()) virtuals.Add(ReadVirtualModel(route.GetProperty("model")));
            }
        _providerCatalog?.SetRuntimeProviders(providers);
        _providerCatalog?.SetVirtualModels(virtuals);
        var oauthProviders = new Dictionary<string, IOAuthProvider>(StringComparer.OrdinalIgnoreCase);
        if (registrations is { ValueKind: JsonValueKind.Array } authEntries)
            foreach (var entry in authEntries.EnumerateArray())
                if (entry.TryGetProperty("oauth", out var oauth) && oauth.ValueKind == JsonValueKind.Object)
                {
                    var id = entry.GetProperty("id").GetString()!;
                    oauthProviders[id] = new CodingAgentJavaScriptOAuthProvider(this, entry.GetProperty("filePath").GetString()!, id,
                        ReadString(oauth, "name") ?? id, ReadInt(entry, "version"), ResetGeneration, ReadBool(oauth, "usesCallbackServer"),
                        ReadBool(oauth, "isSubscription"), ReadString(oauth, "loginLabel"));
                }
        var apiKeyProviders = new Dictionary<string, ApiKeyAuthDefinition>(StringComparer.OrdinalIgnoreCase);
        if (registrations is { ValueKind: JsonValueKind.Array } apiKeyEntries)
            foreach (var entry in apiKeyEntries.EnumerateArray())
                if (entry.TryGetProperty("apiKeyAuth", out var auth) && auth.ValueKind == JsonValueKind.Object)
                    apiKeyProviders[entry.GetProperty("id").GetString()!] = CreateApiKeyAuthDefinition(entry, auth);
        foreach (var id in virtuals.Select(model => model.Provider).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!providers.ContainsKey(id) && _providerCatalog?.GetPhysicalModels(id).Count == 0)
                apiKeyProviders[id] = new("Virtual model", _ => Task.FromResult<ProviderAuthResult?>(new(Source: "virtual")),
                    _ => Task.FromResult<ProviderAuthStatus?>(new(id, true, "virtual", false, false, "Virtual models route to an authenticated physical model.")));
        var nativeAuthProviders = registrations is { ValueKind: JsonValueKind.Array } nativeEntries
            ? nativeEntries.EnumerateArray().Where(entry => ReadBool(entry, "isNative")).Select(entry => entry.GetProperty("id").GetString()!).ToArray() : [];
        _providerCatalog?.AuthResolver.SetRuntimeAuthenticationProviders(oauthProviders, apiKeyProviders, nativeAuthProviders);
        var implementations = new Dictionary<string, IStreamProvider>(_baselineProviderImplementations, StringComparer.OrdinalIgnoreCase);
        if (registrations is { ValueKind: JsonValueKind.Array } implementationsJson)
            foreach (var entry in implementationsJson.EnumerateArray())
                if (ReadBool(entry, "hasStreamSimple") || ReadBool(entry, "hasStream"))
                {
                    var id = entry.GetProperty("id").GetString()!;
                    implementations[id] = new CodingAgentJavaScriptStreamProvider(this, entry.GetProperty("filePath").GetString()!, id, ResetGeneration,
                        ReadString(entry.GetProperty("config"), "api"), ReadBool(entry, "isNative"), _providerRegistry);
                }
        _providerRegistry?.SetModelProviders(implementations);
        foreach (var provider in _providerRefreshErrors.Keys)
            if (!providers.ContainsKey(provider)) _providerRefreshErrors.TryRemove(provider, out _);
        _providerRegistrations = registrations?.Clone();
        _providerRegistrationFingerprint = fingerprint;
        CancelObsoleteProviderRefreshes();
        _sessionBridge?.InvalidateProviderModels();
    }

    /// <summary>【CodingAgent】【提供方恢复】将已覆盖提供方的原目录写入扩展快照，不包含认证配置。</summary>
    /// <param name="writer">JSON 写入器。</param>
    internal void WriteProviderBaselines(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("providerBaselines");
        if (_providerRegistrations is { ValueKind: JsonValueKind.Array } entries && _providerCatalog is not null)
            foreach (var entry in entries.EnumerateArray())
            {
                var name = entry.GetProperty("id").GetString()!;
                writer.WriteStartArray(name);
                foreach (var model in _providerCatalog.GetRuntimeBaselineModels(name)) CodingAgentExtensionSessionBridge.SerializeExtensionModel(model).WriteTo(writer);
                writer.WriteEndArray();
            }
        writer.WriteEndObject();
    }
}

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【能力目录】读取当前会话的全部模型类型。</summary>
    /// <returns>聊天、图像和分类模型。</returns>
    internal IReadOnlyList<Model> GetAllRegistryModels() => _modelCatalog.GetAllModels();

    /// <summary>【CodingAgent】【物理目录】提供不含虚拟覆盖的目录，供同步注销恢复。</summary>
    /// <returns>实际物理模型。</returns>
    internal IReadOnlyList<Model> GetPhysicalRegistryModels() => _modelCatalog.GetPhysicalModels();

    /// <summary>【CodingAgent】【提供方绑定】为已创建的运行器连接实时注册及重载恢复。</summary>
    /// <param name="extensions">扩展运行时。</param>
    internal void BindExtensionProviders(CodingAgentJavaScriptExtensionRuntime extensions)
    {
        _virtualModelRuntime = extensions;
        extensions.ConfigureModelCatalog(_modelCatalog, _config.ProviderRegistry);
    }
}

internal sealed partial class CodingAgentExtensionSessionBridge
{
    /// <summary>【CodingAgent】【提供方刷新】使下一次目录查询反映注册、注销或重载结果。</summary>
    internal void InvalidateProviderModels() => _extensionModels = null;
}
