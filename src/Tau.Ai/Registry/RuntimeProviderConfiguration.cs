// 作者：xxx
using System.Text.Json;

namespace Tau.Ai.Registry;

public sealed partial class ModelConfigurationStore
{
    private IReadOnlyDictionary<string, JsonElement> _runtimeProviders = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    /// <summary>【AI】【扩展认证】判断扩展是否明确覆盖密钥配置，覆盖值优先于默认环境。</summary>
    /// <param name="provider">提供方标识。</param><returns>是否存在运行时密钥配置。</returns>
    internal bool HasRuntimeApiKey(string provider) => Volatile.Read(ref _runtimeProviders).TryGetValue(provider, out var config) && config.TryGetProperty("apiKey", out _);

    /// <summary>【AI】【配置认证】使用统一配置解析器读取提供方密钥，支持环境变量和命令。</summary>
    /// <param name="provider">提供方标识。</param><param name="env">本次环境覆盖。</param><returns>密钥或空值。</returns>
    internal string? ResolveProviderApiKey(string provider, IReadOnlyDictionary<string, string>? env) =>
        ResolveRequestConfiguration(new Model { Id = "", Name = "", Api = "", Provider = provider }, env).ApiKey;

    /// <summary>【AI】【扩展配置】复制文件来源和内存覆盖，不写入用户配置文件。</summary>
    /// <returns>独立的会话配置存储。</returns>
    public ModelConfigurationStore CreateSessionCopy()
    {
        var copy = new ModelConfigurationStore(_searchPaths);
        copy.SetRuntimeProviders(Volatile.Read(ref _runtimeProviders));
        return copy;
    }

    /// <summary>【AI】【配置重载】复制文件来源并排除运行时覆盖，供基础目录重建。</summary>
    /// <returns>只读取文件的配置存储。</returns>
    internal ModelConfigurationStore CreateFileOnlyCopy() => new(_searchPaths);

    /// <summary>【AI】【扩展配置】一次替换内存覆盖，读请求始终取得完整快照。</summary>
    /// <param name="providers">已验证的提供方配置。</param>
    internal void SetRuntimeProviders(IReadOnlyDictionary<string, JsonElement> providers) =>
        Volatile.Write(ref _runtimeProviders, providers.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase));

    /// <summary>【AI】【扩展配置】合并磁盘配置和扩展覆盖，认证信息只在内存及请求链路使用。</summary>
    /// <returns>调用方负责释放的有效配置文档。</returns>
    private JsonDocument? LoadDocument()
    {
        var file = LoadFileDocument();
        var runtime = Volatile.Read(ref _runtimeProviders);
        if (runtime.Count == 0) return file;
        using (file)
        {
            var providers = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (file is not null && TryGetProviders(file.RootElement, out var configured))
                foreach (var property in configured.EnumerateObject()) providers[property.Name] = property.Value;
            foreach (var (name, value) in runtime)
                providers[name] = MergeRuntimeConfiguration(providers.GetValueOrDefault(name), value);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                if (file?.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var property in file.RootElement.EnumerateObject())
                        if (property.Name != "providers") property.WriteTo(writer);
                writer.WriteStartObject("providers");
                foreach (var (name, value) in providers) { writer.WritePropertyName(name); value.WriteTo(writer); }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return JsonDocument.Parse(stream.ToArray());
        }
    }

    /// <summary>【AI】【扩展配置】覆盖顶层属性并合并请求头，模型数组按扩展定义整体替换。</summary>
    /// <param name="baseline">原始配置或未定义值。</param><param name="overlay">扩展配置。</param>
    /// <returns>拥有独立生命周期的合并对象。</returns>
    private static JsonElement MergeRuntimeConfiguration(JsonElement baseline, JsonElement overlay)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (baseline.ValueKind == JsonValueKind.Object)
            foreach (var property in baseline.EnumerateObject()) fields[property.Name] = property.Value;
        foreach (var property in overlay.EnumerateObject())
            fields[property.Name] = property.Name == "headers" && property.Value.ValueKind == JsonValueKind.Object
                ? MergeRuntimeConfiguration(fields.GetValueOrDefault("headers"), property.Value) : property.Value;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in fields) { writer.WritePropertyName(name); value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}

public sealed partial class ModelCatalog
{
    private readonly object _catalogGate = new();
    private Dictionary<string, Dictionary<string, Model>>? _runtimeBaseline;
    private Dictionary<string, Dictionary<string, Model>> _customModels = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, JsonElement> _runtimeDefinitions = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    /// <summary>【AI】【提供方名称】读取扩展注册的显示名，不解析任何认证配置。</summary>
    /// <param name="provider">提供方标识。</param><returns>已注册名称；未指定时为空。</returns>
    public string? GetRegisteredProviderName(string provider)
    {
        lock (_catalogGate)
            return _runtimeDefinitions.TryGetValue(provider, out var definition)
                && definition.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() : null;
    }

    /// <summary>【AI】【提供方注册】检查是否存在运行时定义，用于避免默认认证覆盖扩展认证契约。</summary>
    /// <param name="provider">提供方标识。</param><returns>是否由当前扩展覆盖。</returns>
    public bool HasRuntimeProvider(string provider)
    {
        lock (_catalogGate) return _runtimeDefinitions.ContainsKey(provider);
    }

    /// <summary>【AI】【提供方目录】读取运行时注册标识，包括没有模型的提供方。</summary>
    /// <returns>独立注册标识数组。</returns>
    public IReadOnlyList<string> GetRegisteredProviderIds()
    {
        lock (_catalogGate) return _runtimeDefinitions.Keys.ToArray();
    }

    /// <summary>【AI】【目录恢复】读取运行时覆盖之前的聊天模型，用于同步扩展中的注销结果。</summary>
    /// <param name="provider">提供方标识。</param><returns>不包含请求头的模型列表。</returns>
    public IReadOnlyList<Model> GetRuntimeBaselineModels(string provider)
    {
        lock (_catalogGate)
            return ((_runtimeBaseline ?? _models).GetValueOrDefault(provider)?.Values.ToArray() ?? [])
                .Concat((_runtimeCapabilityBaseline ?? _capabilityModels).Where(model => model.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)))
                .Select(model => model with { Headers = null }).ToArray();
    }

    /// <summary>【AI】【会话目录】复制当前目录与认证配置，使扩展注册互不影响。</summary>
    /// <returns>可独立修改的模型目录。</returns>
    public ModelCatalog CreateSessionCopy()
    {
        lock (_catalogGate)
        {
            var configuration = ConfigurationStore.CreateSessionCopy();
            var copy = new ModelCatalog(_authResolver.WithConfigurationStore(configuration), configuration);
            copy._models = CopyModels(_models);
            copy._customModels = CopyModels(_customModels);
            copy._capabilityModels = _capabilityModels.ToArray();
            copy._virtualModels = _virtualModels.ToArray();
            copy._customCapabilityModels = _customCapabilityModels.ToArray();
            copy._runtimeBaseline = _runtimeBaseline is null ? null : CopyModels(_runtimeBaseline);
            copy._runtimeCapabilityBaseline = _runtimeCapabilityBaseline?.ToArray();
            copy._runtimeDefinitions = _runtimeDefinitions.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            return copy;
        }
    }

    /// <summary>【AI】【目录重载】重新读取模型文件，保留 SDK 模型并重新应用扩展原始定义。</summary>
    public void ReloadConfiguration()
    {
        lock (_catalogGate)
        {
            var files = ConfigurationStore.CreateFileOnlyCopy();
            var rebuilt = new ModelCatalog(_authResolver.WithConfigurationStore(files), files);
            foreach (var model in _customModels.Values.SelectMany(models => models.Values)) rebuilt.RegisterModel(model);
            foreach (var model in _customCapabilityModels) rebuilt.RegisterModel(model);
            var previous = _runtimeBaseline;
            var previousCapabilities = _runtimeCapabilityBaseline;
            _runtimeBaseline = CopyModels(rebuilt._models);
            _runtimeCapabilityBaseline = rebuilt._capabilityModels;
            try
            {
                SetRuntimeProviders(_runtimeDefinitions);
                // 1. 【AI】【配置诊断】目录重建成功后同步新文件诊断，修复文件不残留旧错误
                ConfigurationStore.Error = files.Error;
            }
            catch { _runtimeBaseline = previous; _runtimeCapabilityBaseline = previousCapabilities; throw; }
        }
    }

    /// <summary>【AI】【提供方注册】验证并原子替换扩展层，注销时恢复初始目录和磁盘认证配置。</summary>
    /// <param name="providers">以提供方标识为键的完整扩展配置快照。</param>
    public void SetRuntimeProviders(IReadOnlyDictionary<string, JsonElement> providers)
    {
        lock (_catalogGate)
        {
            var baseline = _runtimeBaseline ?? _models;
            var next = CopyModels(baseline);
            var normalized = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            // 1. 【AI】【注册校验】先构建完整候选状态，任何无效模型都不能破坏已有注册
            foreach (var (name, config) in providers)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                if (config.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Provider '{name}' requires a configuration object.");
                var existing = (baseline.GetValueOrDefault(name)?.Values.ToArray() ?? [])
                    .Concat((_runtimeCapabilityBaseline ?? _capabilityModels).Where(model => model.Provider.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
                var definition = NormalizeRuntimeModels(name, config, existing);
                normalized[name] = definition;
                if (config.TryGetProperty("models", out _)) next.Remove(name);
                ModelConfigurationStore.ApplyProvider(name, definition, next, ModelTypes.Chat);
                // 2. 【AI】【认证隔离】请求头由配置层在请求时解析，目录快照不携带扩展密钥或命令
                if (next.TryGetValue(name, out var models))
                    foreach (var (id, model) in models.ToArray()) models[id] = model with { Headers = null };
            }
            IReadOnlyList<Model> capabilities = [.. ApplyCapabilityProviders(_runtimeCapabilityBaseline ?? _capabilityModels, normalized, ModelTypes.Image),
                .. ApplyCapabilityProviders(_runtimeCapabilityBaseline ?? _capabilityModels, normalized, ModelTypes.Classifier)];
            ConfigurationStore.SetRuntimeProviders(normalized);
            _runtimeBaseline ??= CopyModels(baseline);
            _runtimeCapabilityBaseline ??= _capabilityModels;
            _capabilityModels = capabilities;
            _runtimeDefinitions = providers.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            _models = next;
        }
    }

    /// <summary>【AI】【提供方模型】使用原目录的协议和地址补足模型定义，保留其他扩展字段。</summary>
    /// <param name="provider">提供方标识。</param><param name="config">扩展配置。</param>
    /// <param name="baseline">覆盖前的模型列表。</param><returns>规范化的配置副本。</returns>
    private static JsonElement NormalizeRuntimeModels(string provider, JsonElement config, IReadOnlyList<Model> baseline)
    {
        if (!config.TryGetProperty("models", out var models)) return config.Clone();
        if (models.ValueKind != JsonValueKind.Array) throw new ArgumentException($"Provider '{provider}': models must be an array.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in config.EnumerateObject()) if (property.Name != "models") property.WriteTo(writer);
            writer.WriteStartArray("models");
            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object || Text(model, "id") is not { Length: > 0 } id)
                    throw new ArgumentException($"Provider '{provider}': each model requires an id.");
                var type = Text(model, "type") ?? ModelTypes.Chat;
                if (type is not (ModelTypes.Chat or ModelTypes.Image or ModelTypes.Classifier))
                    throw new ArgumentException($"Provider '{provider}', model '{id}': unknown model type '{type}'.");
                var api = Text(model, "api") ?? (type == ModelTypes.Chat ? Text(config, "api") : null);
                var candidates = baseline.Where(item => ModelTypes.GetModelType(item) == type).ToArray();
                var defaults = candidates.FirstOrDefault(item => item.Id == id)
                    ?? candidates.FirstOrDefault(item => api is not null && item.Api == ModelApiNames.Normalize(api))
                    ?? candidates.FirstOrDefault(item => item.Api == ModelApiNames.OpenAiChatCompletions) ?? candidates.FirstOrDefault();
                api ??= defaults?.Api;
                var baseUrl = Text(model, "baseUrl") ?? Text(config, "baseUrl") ?? defaults?.BaseUrl;
                if (string.IsNullOrWhiteSpace(api)) throw new ArgumentException($"Provider '{provider}', model '{id}': api is required.");
                if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException($"Provider '{provider}', model '{id}': baseUrl is required.");
                writer.WriteStartObject();
                foreach (var property in model.EnumerateObject()) if (property.Name is not "api" and not "baseUrl") property.WriteTo(writer);
                writer.WriteString("api", api);
                writer.WriteString("baseUrl", baseUrl);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>【AI】【提供方模型】安全读取可选字符串。</summary>
    /// <param name="value">配置对象。</param><param name="name">属性名称。</param><returns>字符串或空值。</returns>
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    /// <summary>【AI】【目录快照】复制可变索引，模型记录继续共享不可变值。</summary>
    /// <param name="models">源目录。</param><returns>独立索引。</returns>
    private static Dictionary<string, Dictionary<string, Model>> CopyModels(Dictionary<string, Dictionary<string, Model>> models) =>
        models.ToDictionary(pair => pair.Key, pair => new Dictionary<string, Model>(pair.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
}
