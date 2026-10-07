// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Cloudflare;
using Tau.Ai.Providers.OpenRouter;
using Tau.Ai.Providers.TypeSafe;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【能力调用】独立调用图像或分类模型，保留配置、完整认证与请求回调。</summary>
    /// <param name="request">原生模型、上下文及选项。</param><param name="extensions">扩展运行时。</param>
    /// <param name="token">调用取消信号。</param><returns>原生结果对象。</returns>
    internal async Task<JsonElement> CallRegistryCapabilityAsync(JsonElement request, CodingAgentJavaScriptExtensionRuntime extensions, CancellationToken token)
    {
        var type = request.GetProperty("type").GetString()!;
        var model = _modelCatalog.GetModelOfType(type, request.GetProperty("provider").GetString()!, request.GetProperty("modelId").GetString()!)
            ?? throw new InvalidOperationException("The requested model capability is not registered.");
        var options = ReadRegistryOptions(request.GetProperty("options"), string.Empty, simple: false);
        if (options.ApiKey is null && model.Provider == Model.Provider) options = options with { ApiKey = _config.StreamOptions?.ApiKey };
        options = extensions.BindRegistryCallbacks(request, model, options with { Signal = token });
        var native = extensions.CreateCapabilityProvider(request, model);
        var images = new Dictionary<string, IImagesProvider>();
        var classifiers = new Dictionary<string, IClassifierProvider>();
        // 1. 【CodingAgent】【能力路由】原生实现优先，兼容配置沿用已有 .NET 协议
        if (type == ModelTypes.Image)
            images[model.Api] = (IImagesProvider?)native ?? (model.Api == "openrouter-images" ? new OpenRouterImagesProvider() : throw new InvalidOperationException("Unsupported image API: " + model.Api));
        else
            classifiers[model.Api] = (IClassifierProvider?)native ?? (model.Api switch
            {
                "typesafe-system-one" => new TypeSafeSystemOneProvider(),
                "cloudflare-workers-ai-system-one" => new CloudflareWorkersAiSystemOneProvider(),
                _ => throw new InvalidOperationException("Unsupported classifier API: " + model.Api)
            });
        var runtime = new Models([new ProviderDefinition(model.Provider, models: [model], images: images, classifiers: classifiers)],
            _authResolver, configurationStore: _modelCatalog.ConfigurationStore);
        var context = request.GetProperty("context");
        // 2. 【CodingAgent】【能力请求】复用独立调用钩子，只有实际请求阶段解析认证
        if (type == ModelTypes.Image)
        {
            var input = CodingAgentCapabilityJson.ReadMessage(context.GetProperty("input"), null).Content;
            var imageOptions = ReadRegistryCapabilityOptions<ImagesOptions>(request.GetProperty("options"));
            var result = await runtime.GenerateImagesAsync(model, new(input), imageOptions with
            {
                ApiKey = options.ApiKey, Signal = token,
                OnPayload = options.OnPayload is null ? null : (value, actual) => options.OnPayload(value, actual),
                OnResponse = options.OnResponse is null ? null : (value, actual) => options.OnResponse(value, actual)
            }).ConfigureAwait(false);
            return CodingAgentCapabilityJson.WriteResult(result);
        }
        var classifierOptions = ReadRegistryCapabilityOptions<ClassifierOptions>(request.GetProperty("options"));
        var classified = await runtime.ClassifyAsync(model, context.Deserialize<ClassifierContext>(RegistryStreamJson)!, classifierOptions with
        {
            ApiKey = options.ApiKey, Signal = token,
            OnPayload = options.OnPayload is null ? null : async (value, actual) =>
            {
                var changed = await options.OnPayload(value, actual).ConfigureAwait(false);
                return changed is null ? null : JsonSerializer.SerializeToElement(changed);
            },
            OnResponse = options.OnResponse is null ? null : (value, actual) => options.OnResponse(value, actual)
        }).ConfigureAwait(false);
        return CodingAgentCapabilityJson.WriteResult(classified);
    }

    /// <summary>【CodingAgent】【能力参数】按实际能力反序列化选项，保留双精度参数和所有未知字段。</summary>
    /// <typeparam name="T">图像或分类选项。</typeparam><param name="value">原生选项。</param><returns>对应能力的配置。</returns>
    private static T ReadRegistryCapabilityOptions<T>(JsonElement value)
    {
        var node = JsonNode.Parse(value.GetRawText())!.AsObject();
        foreach (var (native, managed) in new[] { ("timeoutMs", "timeout"), ("maxRetryDelayMs", "maxRetryDelay") })
            if (node.Remove(native, out var milliseconds) && milliseconds is not null) node[managed] = TimeSpan.FromMilliseconds(milliseconds.GetValue<double>()).ToString("c");
        return node.Deserialize<T>(RegistryStreamJson)!;
    }
}

/// <summary>【CodingAgent】【能力协议】复用会话的原生内容与用量映射，补充非聊天结果字段。</summary>
internal static class CodingAgentCapabilityJson
{
    internal static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    /// <summary>【CodingAgent】【能力协议】读取原生内容和用量，复用已有消息转换器。</summary>
    /// <param name="content">原生内容数组。</param><param name="result">可选完整结果。</param><returns>携带内容及用量的内部消息。</returns>
    internal static AssistantMessage ReadMessage(JsonElement? content, JsonElement? result)
    {
        var node = result is { } value ? JsonNode.Parse(value.GetRawText())!.AsObject() : new JsonObject();
        node.Remove("output");
        node.Remove("answers");
        node["role"] = "assistant";
        node["content"] = content is { } blocks ? JsonNode.Parse(blocks.GetRawText()) : new JsonArray();
        return (AssistantMessage)CodingAgentSessionStore.ToMessage(node.Deserialize(CodingAgentSessionJsonContext.Default.CodingAgentSessionMessage)!)!;
    }

    /// <summary>【CodingAgent】【能力协议】写入毫秒时间、原生内容、用量和非聊天结果。</summary>
    /// <param name="result">图像或分类结果。</param><returns>独立原生 JSON。</returns>
    internal static JsonElement WriteResult(object result)
    {
        var image = result as AssistantImages;
        var classifier = result as ClassifierResult;
        var message = new AssistantMessage { Content = image?.Output ?? [], Usage = image?.Usage ?? classifier?.Usage,
            Timestamp = image?.Timestamp ?? classifier!.Timestamp };
        var native = JsonSerializer.SerializeToNode(CodingAgentSessionStore.FromMessage(message), CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage)!;
        var node = JsonSerializer.SerializeToNode(result, result.GetType(), Options)!.AsObject();
        node["usage"] = native["usage"]?.DeepClone();
        node["timestamp"] = message.Timestamp!.Value.ToUnixTimeMilliseconds();
        node["stopReason"] = (image?.StopReason.ToString() ?? classifier!.StopReason.ToString()).ToLowerInvariant();
        if (image is not null) node["output"] = native["content"]?.DeepClone();
        return JsonSerializer.SerializeToElement(node);
    }
}

/// <summary>【CodingAgent】【原生能力】将已准备好认证的非聊天请求交给 Node 提供方。</summary>
internal sealed class CodingAgentJavaScriptCapabilityProvider(
    CodingAgentJavaScriptExtensionRuntime runtime, JsonElement request, string file, string provider, int version, long generation, string api)
    : IImagesProvider, IClassifierProvider
{
    public string Api => api;

    /// <summary>【CodingAgent】【原生图像】调用扩展图像实现并保留内容、用量和错误终值。</summary>
    /// <param name="model">真实模型。</param><param name="context">图像输入。</param><param name="options">已解析配置。</param><returns>图像结果。</returns>
    public async Task<AssistantImages> GenerateImagesAsync(ImagesModel model, ImagesContext context, ImagesOptions options)
    {
        var value = await runtime.InvokeCapabilityProviderAsync(request, file, provider, version, generation, model, options, options.Signal).ConfigureAwait(false);
        var message = CodingAgentCapabilityJson.ReadMessage(value.GetProperty("output"), value);
        return new() { Api = model.Api, Provider = model.Provider, Model = model.Id, Output = message.Content,
            Usage = message.Usage, Timestamp = message.Timestamp ?? DateTimeOffset.UtcNow, ResponseId = message.ResponseId,
            ErrorMessage = message.ErrorMessage, StopReason = Enum.Parse<ImagesStopReason>(value.GetProperty("stopReason").GetString()!, true) };
    }

    /// <summary>【CodingAgent】【原生分类】调用扩展分类实现并恢复判别联合答案。</summary>
    /// <param name="model">真实模型。</param><param name="context">结构化输入。</param><param name="options">已解析配置。</param><returns>分类结果。</returns>
    public async Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions options)
    {
        var value = await runtime.InvokeCapabilityProviderAsync(request, file, provider, version, generation, model, options, options.Signal).ConfigureAwait(false);
        var message = CodingAgentCapabilityJson.ReadMessage(null, value);
        return new() { Api = model.Api, Provider = model.Provider, Model = model.Id,
            Answers = value.GetProperty("answers").Deserialize<Dictionary<string, ClassifierAnswer>>(CodingAgentCapabilityJson.Options)!,
            Usage = message.Usage, Timestamp = message.Timestamp ?? DateTimeOffset.UtcNow, ErrorMessage = message.ErrorMessage,
            StopReason = Enum.Parse<ClassifierStopReason>(value.GetProperty("stopReason").GetString()!, true) };
    }
}

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【能力注册】捕获当前原生提供方版本，禁止请求错误地使用后续替换实现。</summary>
    /// <param name="request">目录调用。</param><param name="model">所选模型。</param><returns>原生实现，不存在时为空。</returns>
    internal CodingAgentJavaScriptCapabilityProvider? CreateCapabilityProvider(JsonElement request, Model model)
    {
        lock (_providerRegistrationGate)
        {
            var entry = _providerRegistrations?.EnumerateArray().FirstOrDefault(entry => ReadString(entry, "id") == model.Provider);
            var capability = model is ImagesModel ? "hasGenerateImages" : "hasClassify";
            return entry is { ValueKind: JsonValueKind.Object } value && ReadBool(value, capability)
                ? new(this, request.Clone(), value.GetProperty("filePath").GetString()!, model.Provider, ReadInt(value, "version"), ResetGeneration, model.Api) : null;
        }
    }

    /// <summary>【CodingAgent】【能力桥接】发送准备后的模型和选项，原始上下文及回调身份保持不变。</summary>
    /// <param name="request">原请求。</param><param name="file">扩展路径。</param><param name="provider">提供方。</param><param name="version">注册版本。</param>
    /// <param name="generation">进程代次。</param><param name="model">实际模型。</param><param name="options">实际配置。</param><param name="token">取消信号。</param>
    /// <returns>原生结果。</returns>
    internal async Task<JsonElement> InvokeCapabilityProviderAsync(JsonElement request, string file, string provider, int version, long generation,
        Model model, object options, CancellationToken token)
    {
        var node = JsonNode.Parse(request.GetRawText())!.AsObject();
        node["providerId"] = provider;
        node["version"] = version;
        node["model"] = JsonNode.Parse(CodingAgentExtensionSessionBridge.SerializeExtensionModel(model).GetRawText());
        var settings = JsonSerializer.SerializeToNode(options, options.GetType(), CodingAgentCapabilityJson.Options)!.AsObject();
        foreach (var (managed, native) in new[] { ("timeout", "timeoutMs"), ("maxRetryDelay", "maxRetryDelayMs") })
            if (settings.Remove(managed, out var duration) && duration is not null) settings[native] = TimeSpan.Parse(duration.GetValue<string>()).TotalMilliseconds;
        node["options"] = settings;
        var result = await ExecuteAsync(BuildPayload("capabilityProvider", file, _cwd, toolArgs: JsonSerializer.SerializeToElement(node)), token,
            expectedGeneration: generation, timeout: Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        using var document = JsonDocument.Parse(result.ResultJson);
        if (!ReadBool(document.RootElement, "ok")) throw new InvalidOperationException(ReadString(document.RootElement, "error"));
        return document.RootElement.GetProperty("value").Clone();
    }
}
