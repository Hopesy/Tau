using System.Net.Http.Headers;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.PiMessages;

/// <summary>Radius 网关动态模型描述。</summary>
public sealed record RadiusGatewayModel(
    string Id,
    string Name,
    bool Reasoning,
    IReadOnlyList<string> Input,
    ModelCost Cost,
    int ContextWindow,
    int MaxTokens)
{
    public IReadOnlyDictionary<string, string?>? ThinkingLevelMap { get; init; }
}

/// <summary>Radius /v1/config 返回的模型目录。</summary>
public sealed record RadiusGatewayConfig(string BaseUrl, IReadOnlyList<RadiusGatewayModel> Models);

/// <summary>Radius 配置加载器，兼容参考项目的 /v1/config 端点。</summary>
public static class RadiusGatewayConfigLoader
{
    /// <summary>默认 Radius 网关地址。</summary>
    public const string DefaultGateway = "https://radius.pi.dev";

    /// <summary>规范化网关 URL。</summary>
    /// <param name="gateway">带或不带 scheme 的地址。</param>
    /// <returns>去除末尾斜杠且带 https 的地址。</returns>
    public static string NormalizeGatewayUrl(string gateway)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateway);
        var value = gateway.Trim();
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value;
        return value.TrimEnd('/');
    }

    /// <summary>请求 Radius /v1/config 并校验模型目录。</summary>
    /// <param name="gateway">网关地址。</param>
    /// <param name="apiKey">可选 bearer key。</param>
    /// <param name="httpClient">HTTP 客户端。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>清洗后的网关配置。</returns>
    public static async Task<RadiusGatewayConfig> LoadAsync(string gateway, string? apiKey, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(NormalizeGatewayUrl(gateway)), "/v1/config"));
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Could not load Radius config from {gateway}: {(int)response.StatusCode}: {Truncate(body)}");
        using var document = JsonDocument.Parse(body);
        return Parse(document.RootElement) ?? throw new InvalidOperationException($"Invalid Radius config from {gateway}");
    }

    /// <summary>【Radius】【配置清洗】读取网络或旧 OAuth 凭据中的网关目录，无效模型条目不影响其余目录。</summary>
    /// <param name="value">配置 JSON。</param><returns>可用配置；顶层格式不符时为空。</returns>
    internal static RadiusGatewayConfig? Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("baseUrl", out var baseUrlElement) || baseUrlElement.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("models", out var modelsElement) || modelsElement.ValueKind != JsonValueKind.Array) return null;
        var models = new List<RadiusGatewayModel>();
        foreach (var item in modelsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || !item.TryGetProperty("name", out var name)
                || !item.TryGetProperty("reasoning", out var reasoning) || !item.TryGetProperty("input", out var inputElement) || inputElement.ValueKind != JsonValueKind.Array
                || !item.TryGetProperty("cost", out var costElement) || costElement.ValueKind != JsonValueKind.Object) continue;
            if (id.ValueKind != JsonValueKind.String || name.ValueKind != JsonValueKind.String || (reasoning.ValueKind != JsonValueKind.True && reasoning.ValueKind != JsonValueKind.False)
                || !TryReadInteger(item, "contextWindow", out var contextValue) || !TryReadInteger(item, "maxTokens", out var maxValue)) continue;
            var idText = id.GetString()!;
            var nameText = name.GetString()!;
            var reasoningValue = reasoning.ValueKind == JsonValueKind.True;
            var input = inputElement.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray();
            var cost = ParseCost(item);
            var thinking = item.TryGetProperty("thinkingLevelMap", out var map) && map.ValueKind == JsonValueKind.Object
                ? map.EnumerateObject().Where(property => property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                    .ToDictionary(property => property.Name, property => property.Value.GetString(), StringComparer.Ordinal) : null;
            models.Add(new RadiusGatewayModel(idText, nameText, reasoningValue, input, cost, contextValue, maxValue) { ThinkingLevelMap = thinking });
        }
        return new RadiusGatewayConfig(baseUrlElement.GetString()!, models);
    }

    /// <summary>【Radius】【模型映射】将清洗后的网关条目转换为指定提供方的模型，保留推理与价格元数据。</summary>
    /// <param name="providerId">提供方标识。</param><param name="config">网关配置。</param><returns>模型快照。</returns>
    internal static IReadOnlyList<Model> GetModelsFromConfig(string providerId, RadiusGatewayConfig config) => config.Models.Select(model => new Model
    {
        Id = model.Id, Name = model.Name, Api = "pi-messages", Provider = providerId, BaseUrl = config.BaseUrl,
        Reasoning = model.Reasoning, ThinkingLevelMap = model.ThinkingLevelMap, InputModalities = model.Input,
        Cost = model.Cost, ContextWindow = model.ContextWindow, MaxOutputTokens = model.MaxTokens
    }).ToArray();

    /// <summary>【Radius】【整数字段】接受 JSON 整数及值为整数的指数或小数表示，拒绝目标类型无法表达的值。</summary>
    /// <param name="value">模型对象。</param><param name="name">字段名。</param><param name="result">解析整数。</param><returns>是否可表达。</returns>
    private static bool TryReadInteger(JsonElement value, string name, out int result)
    {
        result = 0;
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out var number)
            || !double.IsFinite(number) || number < int.MinValue || number > int.MaxValue || Math.Truncate(number) != number) return false;
        result = (int)number; return true;
    }

    /// <summary>【Radius】【价格元数据】保留普通、缓存及分层价格。</summary><param name="element">模型对象。</param><returns>模型价格。</returns>
    private static ModelCost ParseCost(JsonElement element)
    {
        var cost = element.GetProperty("cost");
        var tiers = new List<ModelCostTier>();
        if (cost.TryGetProperty("tiers", out var raw) && raw.ValueKind == JsonValueKind.Array)
            foreach (var tier in raw.EnumerateArray())
            {
                if (tier.ValueKind != JsonValueKind.Object || !tier.TryGetProperty("inputTokensAbove", out var field)
                    || field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out var threshold) || !double.IsFinite(threshold)) continue;
                tiers.Add(new(ReadDecimal(tier, "input") ?? 0, ReadDecimal(tier, "output") ?? 0, ReadDecimal(tier, "cacheRead"), ReadDecimal(tier, "cacheWrite"), threshold));
            }
        return new(ReadDecimal(cost, "input") ?? 0, ReadDecimal(cost, "output") ?? 0,
            ReadDecimal(cost, "cacheRead") ?? 0, ReadDecimal(cost, "cacheWrite") ?? 0, tiers.Count == 0 ? null : tiers);
    }

    /// <summary>【Radius】【费率读取】安全读取数字费率，错误类型不抛出字段访问异常。</summary><param name="value">对象。</param><param name="name">字段。</param><returns>费率或空值。</returns>
    private static decimal? ReadDecimal(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out var result) ? result : null;

    /// <summary>【Radius】【HTTP 错误】按上游限制保留最多 512 个字符并附加省略号。</summary><param name="value">响应正文。</param><returns>有界正文。</returns>
    private static string Truncate(string value) => value.Trim().Length <= 512 ? value.Trim() : value.Trim()[..512] + "…";
}

/// <summary>带动态目录和持久化快照的 Radius pi-messages provider。</summary>
public sealed class RadiusProvider : IStreamProvider
{
    private readonly PiMessagesProvider _streamProvider;
    private readonly HttpClient _httpClient;
    private readonly IModelsStore _store;
    private readonly string _gateway;
    private readonly object _gate = new();
    private IReadOnlyList<Model> _models = [];
    private long _refreshGeneration;

    /// <summary>创建 Radius provider。</summary>
    /// <param name="gateway">网关地址。</param>
    /// <param name="httpClient">HTTP 客户端。</param>
    /// <param name="store">动态模型存储。</param>
    public RadiusProvider(string? gateway = null, HttpClient? httpClient = null, IModelsStore? store = null)
    {
        _gateway = RadiusGatewayConfigLoader.NormalizeGatewayUrl(gateway ?? RadiusGatewayConfigLoader.DefaultGateway);
        _httpClient = httpClient ?? TauHttpClientFactory.Create();
        _streamProvider = new PiMessagesProvider(_httpClient);
        _store = store ?? new Registry.InMemoryModelsStore();
    }

    /// <inheritdoc />
    public string Api => "pi-messages";
    /// <summary>【Radius】【会话声明】继承网关协议能力，防止公开入口提前折叠系统更新。</summary>
    public bool SupportsTranscriptContext => _streamProvider.SupportsTranscriptContext;
    /// <summary>当前动态模型目录。</summary>
    public IReadOnlyList<Model> Models { get { lock (_gate) return _models.ToArray(); } }

    /// <summary>【Radius】【目录刷新】恢复已保存目录、导入旧 OAuth 配置并按上下文发布远端目录。</summary>
    /// <param name="providerId">目录所属提供方。</param><param name="context">宿主管理的刷新阶段。</param>
    /// <param name="update">发布成功时同步更新公开模型集合。</param><returns>本阶段完成任务。</returns>
    internal async Task RefreshModelsAsync(string providerId, RefreshModelsContext context, Action<IReadOnlyList<Model>> update)
    {
        // 1. 【Radius】【缓存恢复】只恢复当前提供方条目，拒绝发布后立即结束此阶段
        if (context.Stored is { } stored)
        {
            var restored = stored.Models.Where(model => model.Provider == providerId).ToArray();
            if (!await context.PublishAsync(new() { Update = () => ApplyModels(restored, update) }).ConfigureAwait(false)) return;
        }
        else if (context.Credential is ProviderCredential.OAuth oauth
            && oauth.Value.Properties.TryGetValue("gatewayConfig", out var raw)
            && RadiusGatewayConfigLoader.Parse(raw) is { } legacyConfig)
        {
            // 2. 【Radius】【旧目录导入】仅无缓存且旧凭据含有效条目时迁入新的目录存储
            var legacy = RadiusGatewayConfigLoader.GetModelsFromConfig(providerId, legacyConfig);
            if (legacy.Count > 0 && !await context.PublishAsync(new()
            {
                Persist = new(legacy, CheckedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                Update = () => ApplyModels(legacy, update)
            }).ConfigureAwait(false)) return;
        }

        // 3. 【Radius】【联网更新】使用本阶段有效凭据，取消后不尝试发布迟到的响应
        if (!context.AllowNetwork || context.Signal.IsCancellationRequested) return;
        var key = context.Credential switch { ProviderCredential.OAuth value => value.Value.Access, ProviderCredential.ApiKey value => value.Value.Key, _ => null };
        var config = await RadiusGatewayConfigLoader.LoadAsync(_gateway, key, _httpClient, context.Signal).ConfigureAwait(false);
        if (context.Signal.IsCancellationRequested) return;
        var refreshed = RadiusGatewayConfigLoader.GetModelsFromConfig(providerId, config);
        await context.PublishAsync(new()
        {
            Persist = new(refreshed, CheckedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            Update = () => ApplyModels(refreshed, update)
        }).ConfigureAwait(false);
    }

    /// <summary>【Radius】【目录发布】在宿主已验证代次并完成持久化后同步替换动态快照。</summary>
    /// <param name="models">已接受的动态目录。</param><param name="update">公开目录同步更新回调。</param>
    private void ApplyModels(IReadOnlyList<Model> models, Action<IReadOnlyList<Model>> update)
    {
        update(models);
        lock (_gate) _models = models;
    }

    /// <summary>【Radius】【目录合并】按大小写敏感的模型标识覆盖基线，新增模型保持动态目录顺序。</summary>
    /// <param name="baseline">内置或配置基线。</param><param name="dynamicModels">已接受的动态模型。</param><returns>合并目录。</returns>
    internal static IReadOnlyList<Model> MergeModels(IReadOnlyList<Model> baseline, IReadOnlyList<Model> dynamicModels)
    {
        var merged = baseline.ToList();
        foreach (var model in dynamicModels)
        {
            var index = merged.FindIndex(entry => entry.Id == model.Id);
            if (index >= 0) merged[index] = model; else merged.Add(model);
        }
        return merged;
    }

    /// <summary>加载本地快照并可选地刷新远端目录。</summary>
    /// <param name="providerId">目录 provider id。</param>
    /// <param name="apiKey">网关 API key。</param>
    /// <param name="allowNetwork">是否允许网络请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<Model>> RefreshModelsAsync(string providerId = "radius", string? apiKey = null, bool allowNetwork = true, CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var stored = await _store.ReadAsync(providerId, new ModelsStoreOperationOptions(cancellationToken)).ConfigureAwait(false);
        if (stored is not null) lock (_gate) _models = stored.Models;
        if (!allowNetwork) return Models;
        var config = await RadiusGatewayConfigLoader.LoadAsync(_gateway, apiKey, _httpClient, cancellationToken).ConfigureAwait(false);
        var refreshed = RadiusGatewayConfigLoader.GetModelsFromConfig(providerId, config);
        if (generation != Volatile.Read(ref _refreshGeneration)) return Models;
        await _store.WriteAsync(providerId, new ModelsStoreEntry(refreshed, CheckedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), new ModelsStoreOperationOptions(cancellationToken)).ConfigureAwait(false);
        lock (_gate) _models = refreshed;
        return refreshed;
    }

    /// <inheritdoc />
    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) => _streamProvider.Stream(model with { Api = Api }, context, options);
    /// <inheritdoc />
    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => _streamProvider.StreamSimple(model with { Api = Api }, context, options);
}
