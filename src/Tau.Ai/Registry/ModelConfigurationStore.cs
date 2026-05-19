using System.Diagnostics;
using System.Text.Json;

namespace Tau.Ai.Registry;

public sealed class ModelConfigurationStore
{
    private const string DefaultApi = "openai-chat-completions";
    private readonly string[] _searchPaths;

    public ModelConfigurationStore(IEnumerable<string>? searchPaths = null)
    {
        _searchPaths = searchPaths?.ToArray() ?? GetDefaultSearchPaths().ToArray();
    }

    internal void ApplyTo(Dictionary<string, Dictionary<string, Model>> models)
    {
        var doc = LoadDocument();
        if (doc is null)
        {
            return;
        }

        using (doc)
        {
            if (!TryGetProviders(doc.RootElement, out var providers))
            {
                return;
            }

            foreach (var providerProp in providers.EnumerateObject())
            {
                if (providerProp.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                ApplyProvider(providerProp.Name, providerProp.Value, models);
            }
        }
    }

    internal ModelRequestConfiguration ResolveRequestConfiguration(Model model)
    {
        var doc = LoadDocument();
        if (doc is null)
        {
            return ModelRequestConfiguration.Empty;
        }

        using (doc)
        {
            if (!TryGetProviders(doc.RootElement, out var providers) ||
                !TryGetObjectProperty(providers, model.Provider, out var providerConfig))
            {
                return ModelRequestConfiguration.Empty;
            }

            var apiKey = ResolveConfigValue(GetString(providerConfig, "apiKey"));
            var authHeader = GetBool(providerConfig, "authHeader") ?? false;
            var headers = ResolveHeaders(ParseStringDictionary(providerConfig, "headers"));

            if (providerConfig.TryGetProperty("modelOverrides", out var overrides) &&
                overrides.ValueKind == JsonValueKind.Object &&
                TryGetObjectProperty(overrides, model.Id, out var overrideConfig))
            {
                headers = MergeHeaders(headers, ResolveHeaders(ParseStringDictionary(overrideConfig, "headers")));
            }

            if (providerConfig.TryGetProperty("models", out var configuredModels) &&
                configuredModels.ValueKind == JsonValueKind.Array)
            {
                foreach (var configuredModel in configuredModels.EnumerateArray())
                {
                    if (configuredModel.ValueKind == JsonValueKind.Object &&
                        TryGetString(configuredModel, "id", out var modelId) &&
                        model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                    {
                        headers = MergeHeaders(headers, ResolveHeaders(ParseStringDictionary(configuredModel, "headers")));
                        break;
                    }
                }
            }

            return new ModelRequestConfiguration(apiKey, headers, authHeader);
        }
    }

    internal ModelRequestConfigurationStatus InspectRequestConfigurationStatus(Model model)
    {
        var doc = LoadDocument();
        if (doc is null)
        {
            return ModelRequestConfigurationStatus.Empty;
        }

        using (doc)
        {
            if (!TryGetProviders(doc.RootElement, out var providers) ||
                !TryGetObjectProperty(providers, model.Provider, out var providerConfig))
            {
                return ModelRequestConfigurationStatus.Empty;
            }

            var hasApiKey = HasConfiguredValue(GetString(providerConfig, "apiKey"));
            var hasCredentialHeader = HasCredentialHeader(ParseStringDictionary(providerConfig, "headers"));
            var hasCommandBackedSecret =
                IsCommandBackedValue(GetString(providerConfig, "apiKey")) ||
                HasCommandBackedCredentialHeader(ParseStringDictionary(providerConfig, "headers"));

            if (providerConfig.TryGetProperty("modelOverrides", out var overrides) &&
                overrides.ValueKind == JsonValueKind.Object &&
                TryGetObjectProperty(overrides, model.Id, out var overrideConfig))
            {
                var overrideHeaders = ParseStringDictionary(overrideConfig, "headers");
                hasCredentialHeader |= HasCredentialHeader(overrideHeaders);
                hasCommandBackedSecret |= HasCommandBackedCredentialHeader(overrideHeaders);
            }

            if (providerConfig.TryGetProperty("models", out var configuredModels) &&
                configuredModels.ValueKind == JsonValueKind.Array)
            {
                foreach (var configuredModel in configuredModels.EnumerateArray())
                {
                    if (configuredModel.ValueKind == JsonValueKind.Object &&
                        TryGetString(configuredModel, "id", out var modelId) &&
                        model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                    {
                        var modelHeaders = ParseStringDictionary(configuredModel, "headers");
                        hasCredentialHeader |= HasCredentialHeader(modelHeaders);
                        hasCommandBackedSecret |= HasCommandBackedCredentialHeader(modelHeaders);
                        break;
                    }
                }
            }

            return new ModelRequestConfigurationStatus(
                hasApiKey,
                hasCredentialHeader,
                hasCommandBackedSecret);
        }
    }

    private static void ApplyProvider(
        string providerName,
        JsonElement providerConfig,
        Dictionary<string, Dictionary<string, Model>> models)
    {
        var providerApi = NormalizeApi(GetString(providerConfig, "api"));
        var providerBaseUrl = GetString(providerConfig, "baseUrl");
        var providerHeaders = ParseStringDictionary(providerConfig, "headers");
        var providerCompat = ParseCompat(providerConfig, "compat");

        if (!models.TryGetValue(providerName, out var bucket))
        {
            bucket = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase);
            models[providerName] = bucket;
        }

        if (HasProviderOverride(providerBaseUrl, providerHeaders, providerCompat))
        {
            foreach (var (modelId, model) in bucket.ToArray())
            {
                bucket[modelId] = model with
                {
                    BaseUrl = providerBaseUrl ?? model.BaseUrl,
                    Headers = MergeHeaders(model.Headers, providerHeaders),
                    Compat = MergeCompat(model.Compat, providerCompat)
                };
            }
        }

        ApplyModelOverrides(providerConfig, bucket);
        ApplyCustomModels(providerName, providerConfig, bucket, providerApi, providerBaseUrl, providerHeaders, providerCompat);
    }

    private static void ApplyModelOverrides(JsonElement providerConfig, Dictionary<string, Model> bucket)
    {
        if (!providerConfig.TryGetProperty("modelOverrides", out var overrides) ||
            overrides.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var overrideProp in overrides.EnumerateObject())
        {
            if (overrideProp.Value.ValueKind != JsonValueKind.Object ||
                !bucket.TryGetValue(overrideProp.Name, out var existing))
            {
                continue;
            }

            bucket[overrideProp.Name] = ApplyModelOverride(existing, overrideProp.Value);
        }
    }

    private static void ApplyCustomModels(
        string providerName,
        JsonElement providerConfig,
        Dictionary<string, Model> bucket,
        string? providerApi,
        string? providerBaseUrl,
        IDictionary<string, string>? providerHeaders,
        ModelCompatibility? providerCompat)
    {
        if (!providerConfig.TryGetProperty("models", out var configuredModels) ||
            configuredModels.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var defaultApi = providerApi ?? bucket.Values.FirstOrDefault()?.Api ?? DefaultApi;
        foreach (var configuredModel in configuredModels.EnumerateArray())
        {
            if (configuredModel.ValueKind != JsonValueKind.Object ||
                !TryGetString(configuredModel, "id", out var modelId))
            {
                continue;
            }

            var model = new Model
            {
                Id = modelId!,
                Name = GetString(configuredModel, "name") ?? modelId!,
                Api = NormalizeApi(GetString(configuredModel, "api")) ?? defaultApi,
                Provider = providerName,
                BaseUrl = GetString(configuredModel, "baseUrl") ?? providerBaseUrl ?? string.Empty,
                Reasoning = GetBool(configuredModel, "reasoning") ?? false,
                InputModalities = ParseInputModalities(configuredModel),
                Cost = ParseCost(configuredModel, fallback: null) ?? new ModelCost(0m, 0m, 0m, 0m),
                ContextWindow = GetInt(configuredModel, "contextWindow") ?? 128_000,
                MaxOutputTokens = GetInt(configuredModel, "maxTokens") ?? GetInt(configuredModel, "maxOutputTokens") ?? 16_384,
                Headers = MergeHeaders(providerHeaders, ParseStringDictionary(configuredModel, "headers")),
                Compat = MergeCompat(providerCompat, ParseCompat(configuredModel, "compat"))
            };

            bucket[model.Id] = model;
        }
    }

    private static Model ApplyModelOverride(Model model, JsonElement overrideConfig)
    {
        return model with
        {
            Name = GetString(overrideConfig, "name") ?? model.Name,
            Reasoning = GetBool(overrideConfig, "reasoning") ?? model.Reasoning,
            InputModalities = TryGetInputModalities(overrideConfig, out var input) ? input : model.InputModalities,
            Cost = ParseCost(overrideConfig, model.Cost) ?? model.Cost,
            ContextWindow = GetInt(overrideConfig, "contextWindow") ?? model.ContextWindow,
            MaxOutputTokens = GetInt(overrideConfig, "maxTokens") ?? GetInt(overrideConfig, "maxOutputTokens") ?? model.MaxOutputTokens,
            Headers = MergeHeaders(model.Headers, ParseStringDictionary(overrideConfig, "headers")),
            Compat = MergeCompat(model.Compat, ParseCompat(overrideConfig, "compat"))
        };
    }

    private static ModelCost? ParseCost(JsonElement element, ModelCost? fallback)
    {
        if (!element.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        return new ModelCost(
            GetDecimal(cost, "input") ?? fallback?.InputPerMillion ?? 0m,
            GetDecimal(cost, "output") ?? fallback?.OutputPerMillion ?? 0m,
            GetDecimal(cost, "cacheRead") ?? fallback?.CacheReadPerMillion ?? 0m,
            GetDecimal(cost, "cacheWrite") ?? fallback?.CacheWritePerMillion ?? 0m);
    }

    private static ModelCompatibility? ParseCompat(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var compat) || compat.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ModelCompatibility
        {
            SupportsStore = GetBool(compat, "supportsStore"),
            SupportsDeveloperRole = GetBool(compat, "supportsDeveloperRole"),
            SupportsReasoningEffort = GetBool(compat, "supportsReasoningEffort"),
            ReasoningEffortMap = ParseStringDictionary(compat, "reasoningEffortMap"),
            SupportsUsageInStreaming = GetBool(compat, "supportsUsageInStreaming"),
            MaxTokensField = GetString(compat, "maxTokensField"),
            RequiresThinkingAsText = GetBool(compat, "requiresThinkingAsText"),
            ThinkingFormat = GetString(compat, "thinkingFormat"),
            OpenRouterRouting = ParseObjectDictionary(compat, "openRouterRouting"),
            VercelGatewayRouting = ParseVercelGatewayRouting(compat, "vercelGatewayRouting"),
            ZaiToolStream = GetBool(compat, "zaiToolStream"),
            SupportsStrictMode = GetBool(compat, "supportsStrictMode")
        };
    }

    private static ModelCompatibility? MergeCompat(ModelCompatibility? baseCompat, ModelCompatibility? overrideCompat)
    {
        if (baseCompat is null)
        {
            return overrideCompat;
        }

        if (overrideCompat is null)
        {
            return baseCompat;
        }

        return new ModelCompatibility
        {
            SupportsStore = overrideCompat.SupportsStore ?? baseCompat.SupportsStore,
            SupportsDeveloperRole = overrideCompat.SupportsDeveloperRole ?? baseCompat.SupportsDeveloperRole,
            SupportsReasoningEffort = overrideCompat.SupportsReasoningEffort ?? baseCompat.SupportsReasoningEffort,
            ReasoningEffortMap = MergeStringDictionaries(baseCompat.ReasoningEffortMap, overrideCompat.ReasoningEffortMap),
            SupportsUsageInStreaming = overrideCompat.SupportsUsageInStreaming ?? baseCompat.SupportsUsageInStreaming,
            MaxTokensField = overrideCompat.MaxTokensField ?? baseCompat.MaxTokensField,
            RequiresThinkingAsText = overrideCompat.RequiresThinkingAsText ?? baseCompat.RequiresThinkingAsText,
            ThinkingFormat = overrideCompat.ThinkingFormat ?? baseCompat.ThinkingFormat,
            OpenRouterRouting = MergeObjectDictionaries(baseCompat.OpenRouterRouting, overrideCompat.OpenRouterRouting),
            VercelGatewayRouting = MergeVercelGatewayRouting(baseCompat.VercelGatewayRouting, overrideCompat.VercelGatewayRouting),
            ZaiToolStream = overrideCompat.ZaiToolStream ?? baseCompat.ZaiToolStream,
            SupportsStrictMode = overrideCompat.SupportsStrictMode ?? baseCompat.SupportsStrictMode
        };
    }

    private static VercelGatewayRouting? ParseVercelGatewayRouting(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var routing) || routing.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new VercelGatewayRouting
        {
            Only = ParseStringArray(routing, "only"),
            Order = ParseStringArray(routing, "order")
        };
    }

    private static VercelGatewayRouting? MergeVercelGatewayRouting(
        VercelGatewayRouting? baseRouting,
        VercelGatewayRouting? overrideRouting)
    {
        if (baseRouting is null)
        {
            return overrideRouting;
        }

        if (overrideRouting is null)
        {
            return baseRouting;
        }

        return new VercelGatewayRouting
        {
            Only = overrideRouting.Only ?? baseRouting.Only,
            Order = overrideRouting.Order ?? baseRouting.Order
        };
    }

    private static Dictionary<string, string>? ParseStringDictionary(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in value.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                result[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static IDictionary<string, object>? ParseObjectDictionary(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in value.EnumerateObject())
        {
            result[prop.Name] = prop.Value.Clone();
        }

        return result.Count == 0 ? null : result;
    }

    private static IReadOnlyList<string> ParseInputModalities(JsonElement element)
    {
        return TryGetInputModalities(element, out var input) ? input : ["text"];
    }

    private static bool TryGetInputModalities(JsonElement element, out IReadOnlyList<string> input)
    {
        input = [];
        if (!element.TryGetProperty("input", out var inputElement) &&
            !element.TryGetProperty("inputModalities", out inputElement))
        {
            return false;
        }

        var parsed = ParseStringArray(inputElement);
        if (parsed.Count == 0)
        {
            return false;
        }

        input = parsed;
        return true;
    }

    private static IReadOnlyList<string>? ParseStringArray(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var array)
            ? ParseStringArray(array)
            : null;
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                result.Add(item.GetString()!);
            }
        }

        return result;
    }

    private static IDictionary<string, string>? MergeHeaders(
        IDictionary<string, string>? baseHeaders,
        IDictionary<string, string>? overrideHeaders)
    {
        if (baseHeaders is null || baseHeaders.Count == 0)
        {
            return overrideHeaders is null || overrideHeaders.Count == 0
                ? null
                : new Dictionary<string, string>(overrideHeaders, StringComparer.OrdinalIgnoreCase);
        }

        if (overrideHeaders is null || overrideHeaders.Count == 0)
        {
            return new Dictionary<string, string>(baseHeaders, StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(baseHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overrideHeaders)
        {
            result[key] = value;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string>? MergeStringDictionaries(
        IReadOnlyDictionary<string, string>? baseValues,
        IReadOnlyDictionary<string, string>? overrideValues)
    {
        if (baseValues is null || baseValues.Count == 0)
        {
            return overrideValues;
        }

        if (overrideValues is null || overrideValues.Count == 0)
        {
            return baseValues;
        }

        var result = new Dictionary<string, string>(baseValues, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overrideValues)
        {
            result[key] = value;
        }

        return result;
    }

    private static IDictionary<string, object>? MergeObjectDictionaries(
        IDictionary<string, object>? baseValues,
        IDictionary<string, object>? overrideValues)
    {
        if (baseValues is null || baseValues.Count == 0)
        {
            return overrideValues;
        }

        if (overrideValues is null || overrideValues.Count == 0)
        {
            return baseValues;
        }

        var result = new Dictionary<string, object>(baseValues, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overrideValues)
        {
            result[key] = value;
        }

        return result;
    }

    private static bool HasProviderOverride(
        string? providerBaseUrl,
        IDictionary<string, string>? providerHeaders,
        ModelCompatibility? providerCompat)
    {
        return !string.IsNullOrWhiteSpace(providerBaseUrl) ||
               providerHeaders is { Count: > 0 } ||
               providerCompat is not null;
    }

    private JsonDocument? LoadDocument()
    {
        var path = _searchPaths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool TryGetProviders(JsonElement root, out JsonElement providers)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("providers", out providers) &&
            providers.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        providers = default;
        return false;
    }

    private static bool TryGetObjectProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (candidate.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                candidate.Value.ValueKind == JsonValueKind.Object)
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static IDictionary<string, string>? ResolveHeaders(IDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            var resolved = ResolveConfigValue(value);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                result[key] = resolved;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static bool HasConfiguredValue(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool HasCredentialHeader(IDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return false;
        }

        foreach (var (key, value) in headers)
        {
            if (HasConfiguredValue(value) && IsCredentialHeaderName(key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCommandBackedCredentialHeader(IDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return false;
        }

        foreach (var (key, value) in headers)
        {
            if (IsCredentialHeaderName(key) && IsCommandBackedValue(value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCredentialHeaderName(string headerName)
    {
        return headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("api-key", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase) ||
               headerName.Contains("token", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommandBackedValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.TrimStart().StartsWith('!');

    private static string? ResolveConfigValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.StartsWith('!'))
        {
            return ResolveCommandValue(value[1..].Trim());
        }

        var environmentValue = Environment.GetEnvironmentVariable(value);
        return string.IsNullOrWhiteSpace(environmentValue) ? value : environmentValue;
    }

    private static string? ResolveCommandValue(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        try
        {
            var isWindows = OperatingSystem.IsWindows();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = isWindows ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : "/bin/sh",
                    Arguments = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000) || process.ExitCode != 0)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeApi(string? api)
    {
        if (string.IsNullOrWhiteSpace(api))
        {
            return null;
        }

        return api.Trim() switch
        {
            "openai-completions" => DefaultApi,
            "openai-compatible" => DefaultApi,
            var value => value
        };
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = GetString(element, propertyName);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static decimal? GetDecimal(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDecimal(out var value)
            ? value
            : null;
    }

    private static IEnumerable<string> GetDefaultSearchPaths()
    {
        var configured = Environment.GetEnvironmentVariable("TAU_MODELS_FILE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        yield return Path.Combine(Directory.GetCurrentDirectory(), ".tau", "models.json");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".tau", "models.json");
        }
    }
}

internal sealed record ModelRequestConfiguration(
    string? ApiKey,
    IDictionary<string, string>? Headers,
    bool AuthHeader)
{
    public static ModelRequestConfiguration Empty { get; } = new(null, null, false);
}

internal sealed record ModelRequestConfigurationStatus(
    bool HasApiKey,
    bool HasCredentialHeader,
    bool HasCommandBackedSecret)
{
    public static ModelRequestConfigurationStatus Empty { get; } = new(false, false, false);

    public bool IsConfigured => HasApiKey || HasCredentialHeader;
}
