using System.Net.Http.Headers;
using System.Text.Json;
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
    int MaxTokens);

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
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{NormalizeGatewayUrl(gateway)}/v1/config");
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Could not load Radius config from {gateway}: {(int)response.StatusCode}: {Truncate(body)}");
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("baseUrl", out var baseUrlElement) || !document.RootElement.TryGetProperty("models", out var modelsElement) || modelsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Invalid Radius config from {gateway}");
        var models = new List<RadiusGatewayModel>();
        foreach (var item in modelsElement.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || !item.TryGetProperty("name", out var name) || !item.TryGetProperty("reasoning", out var reasoning) || !item.TryGetProperty("contextWindow", out var contextWindow) || !item.TryGetProperty("maxTokens", out var maxTokens)) continue;
            if (id.ValueKind != JsonValueKind.String || name.ValueKind != JsonValueKind.String || (reasoning.ValueKind != JsonValueKind.True && reasoning.ValueKind != JsonValueKind.False) || !contextWindow.TryGetInt32(out var contextValue) || !maxTokens.TryGetInt32(out var maxValue)) continue;
            var idText = id.GetString()!;
            var nameText = name.GetString()!;
            var reasoningValue = reasoning.ValueKind == JsonValueKind.True;
            var input = item.TryGetProperty("input", out var inputElement) && inputElement.ValueKind == JsonValueKind.Array ? inputElement.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : ["text"];
            var cost = ParseCost(item);
            models.Add(new RadiusGatewayModel(idText, nameText, reasoningValue, input, cost, contextValue, maxValue));
        }
        return new RadiusGatewayConfig(baseUrlElement.GetString() ?? NormalizeGatewayUrl(gateway), models);
    }

    private static ModelCost ParseCost(JsonElement element) => element.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object
        ? new ModelCost(ReadDecimal(cost, "input"), ReadDecimal(cost, "output"), ReadDecimal(cost, "cacheRead"), ReadDecimal(cost, "cacheWrite"))
        : new ModelCost(0, 0, 0, 0);
    private static decimal ReadDecimal(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.TryGetDecimal(out var result) ? result : 0;
    private static string Truncate(string value) => value.Trim().Length <= 512 ? value.Trim() : value.Trim()[..512] + "...";
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
    /// <summary>当前动态模型目录。</summary>
    public IReadOnlyList<Model> Models { get { lock (_gate) return _models.ToArray(); } }

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
        var refreshed = config.Models.Select(model => new Model { Id = model.Id, Name = model.Name, Api = Api, Provider = providerId, BaseUrl = config.BaseUrl, Reasoning = model.Reasoning, InputModalities = model.Input, Cost = model.Cost, ContextWindow = model.ContextWindow, MaxOutputTokens = model.MaxTokens }).ToArray();
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
