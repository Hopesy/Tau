using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai.Streaming;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.Mistral;

public sealed partial class MistralProvider : IStreamProvider
{
    private const int MistralToolCallIdLength = 9;
    private readonly HttpClient _httpClient;

    public MistralProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? TauHttpClientFactory.Create();
    }

    public string Api => "mistral-conversations";
    /// <summary>【Mistral】【会话声明】由本协议按模型能力处理系统消息，避免入口提前折叠。</summary>
    public bool SupportsTranscriptContext => true;

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();
        var state = new MistralStreamState(model, Api, stream);
        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, state).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (options.Signal.IsCancellationRequested)
            {
                state.Fail(StreamOptionHelpers.AbortedErrorMessage, StopReason.Aborted);
            }
            catch (Exception ex)
            {
                state.Fail(ex.Message);
            }
        });
        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
    {
        options = SimpleTokenOptions.WithContextLimit(model, context, options);
        var thinking = ResolveThinkingOptions(model, options.Reasoning);
        var nativeOptions = new MistralOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens ?? model.MaxOutputTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            Signal = options.Signal,
            OnResponse = options.OnResponse,
            OnPayload = options.OnPayload,
            TransformHeaders = options.TransformHeaders,
            OnProviderStreamEvent = options.OnProviderStreamEvent,
            CacheRetention = StreamOptionHelpers.ResolveCacheRetention(options),
            SessionId = options.SessionId,
            Headers = options.Headers,
            Timeout = options.Timeout,
            MaxRetryDelay = options.MaxRetryDelay,
            MaxRetries = options.MaxRetries,
            WebSocketConnectTimeout = options.WebSocketConnectTimeout,
            Metadata = options.Metadata,
            Env = options.Env,
            SamplingParams = options.SamplingParams,
            Deferred = options.Deferred,
            ToolChoice = ConvertToolChoice(options.ToolChoice),
            PromptMode = thinking.PromptMode,
            ReasoningEffort = thinking.ReasoningEffort
        };
        return Stream(model, context, nativeOptions);
    }

    /// <summary>【Mistral】【请求传输】组装原生请求、应用覆盖并将 SSE 和传输错误写入同一状态。</summary>
    /// <param name="model">目标模型。</param><param name="context">会话。</param><param name="options">请求选项。</param>
    /// <param name="state">本次请求状态。</param><returns>流读取任务。</returns>
    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        StreamOptions options,
        MistralStreamState state)
    {
        options.Signal.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(options.ApiKey)) throw new InvalidOperationException($"No API key for provider: {model.Provider}");
        var url = BuildEndpoint(model.BaseUrl);
        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(model, context, options)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, MistralJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyAuthHeader(request, options.ApiKey);
        request.Headers.TryAddWithoutValidation("User-Agent", ProviderHttpHeaders.UserAgent());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ProviderHttpHeaders.Apply(request, model.Headers);
        ProviderHttpHeaders.Apply(request, options.Headers);
        if (UsesPromptCaching(options) && !HasAffinityOverride(model.Headers) && !HasAffinityOverride(options.Headers))
        {
            request.Headers.TryAddWithoutValidation("x-affinity", options.SessionId);
        }

        using var requestTimeout = StreamOptionHelpers.CreateRequestTimeout(options with { Timeout = options.Timeout ?? TimeSpan.FromSeconds(60) });
        try
        {
            await StreamOptionHelpers.ApplyHeadersCallbackAsync(options, model, request).ConfigureAwait(false);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestTimeout.Token).ConfigureAwait(false);
            await StreamOptionHelpers.InvokeResponseCallbackAsync(options, model, response).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
                state.Fail(FormatHttpError(response, errorBody));
                return;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);
            state.Start();
            await foreach (var sse in SseParser.ParseAsync(responseStream, requestTimeout.Token))
            {
                if (sse.Data == "[DONE]") break;
                await StreamOptionHelpers.InvokeProviderStreamEventAsync(options, model, sse.Data).ConfigureAwait(false);
                requestTimeout.Token.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(sse.Data);
                state.Process(document.RootElement);
            }
            requestTimeout.Token.ThrowIfCancellationRequested();
            state.Complete();
        }
        catch (OperationCanceledException ex) when (requestTimeout.IsTimeoutCancellation)
        {
            state.Fail(requestTimeout.CreateTimeoutException(ex).Message);
        }
        catch (OperationCanceledException) when (options.Signal.IsCancellationRequested)
        {
            state.Fail(StreamOptionHelpers.AbortedErrorMessage, StopReason.Aborted);
        }
        catch (Exception error)
        {
            state.Fail(error.Message);
        }
    }

    /// <summary>【Mistral】【请求转换】按系统消息能力转换会话，并发送当前完整工具集合。</summary>
    /// <param name="model">模型及兼容能力。</param>
    /// <param name="context">包含旧字段或原生声明的上下文。</param>
    /// <param name="options">请求选项。</param>
    /// <returns>Mistral 请求报文。</returns>
    private static Dictionary<string, object> BuildRequestBody(Model model, LlmContext context, StreamOptions options)
    {
        // 1. 【Mistral】【会话声明】仅显式启用时保留中途声明，否则重放成开场基线
        context = Transcript.ResolveTranscript(context, model.Compat?.SupportsMidConvoSystemMessages == true);
        context = MessageTransformer.DowngradeUnsupportedImages(context, model);
        var normalizer = new MistralToolCallIdNormalizer();
        context = context with { Messages = MessageTransformer.TransformMessages(context.Messages, model, (id, _, _) => normalizer.Normalize(id)) };
        var body = new Dictionary<string, object>
        {
            ["model"] = model.Id,
            ["stream"] = true,
            ["messages"] = ConvertMessages(context)
        };

        // 2. 【Mistral】【工具声明】上游协议始终发送最终工具集合，不发送位置锚定的工具增量
        var tools = ConvertTools(Transcript.GetCurrentTools(context.Messages));
        if (tools.Count > 0)
        {
            body["tools"] = tools;
        }

        if (options.Temperature.HasValue)
        {
            body["temperature"] = options.Temperature.Value;
        }

        if (options.MaxTokens.HasValue)
        {
            body["max_tokens"] = options.MaxTokens.Value;
        }

        if (options.TopP.HasValue)
        {
            body["top_p"] = options.TopP.Value;
        }

        StreamOptionHelpers.ApplySamplingParams(body, model, options);
        if (UsesPromptCaching(options)) body["prompt_cache_key"] = options.SessionId!;

        if (options is MistralOptions mistralOptions)
        {
            if (mistralOptions.ToolChoice is { } toolChoice)
            {
                body["tool_choice"] = MapToolChoice(toolChoice);
            }

            if (!string.IsNullOrWhiteSpace(mistralOptions.PromptMode))
            {
                body["prompt_mode"] = mistralOptions.PromptMode!;
            }

            if (!string.IsNullOrWhiteSpace(mistralOptions.ReasoningEffort))
            {
                body["reasoning_effort"] = mistralOptions.ReasoningEffort!;
            }
        }

        return body;
    }

    private static object MapToolChoice(MistralToolChoice choice)
    {
        if (choice.IsFunction)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object>
                {
                    ["name"] = choice.FunctionName!
                }
            };
        }

        return choice.Kind;
    }

    /// <summary>【Mistral】【工具声明】解析工具严格采样要求并生成对应的参数 Schema。</summary>
    /// <param name="tools">当前工具列表。</param><returns>协议函数工具数组。</returns>
    private static List<object> ConvertTools(IReadOnlyList<Tool>? tools)
    {
        var result = new List<object>();
        if (tools is null)
        {
            return result;
        }

        foreach (var tool in tools)
        {
            var strict = ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, supportsStrictMode: true);
            result.Add(new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict),
                    ["strict"] = strict ?? false
                }
            });
        }

        return result;
    }

    private static string SanitizeText(string text) =>
        UnicodeTextSanitizer.RemoveUnpairedSurrogates(text);

    private static void ApplyAuthHeader(HttpRequestMessage request, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || Auth.EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey))
        {
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }



    private sealed class MistralToolCallIdNormalizer
    {
        private readonly Dictionary<string, string> _idMap = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _reverseMap = new(StringComparer.Ordinal);

        /// <summary>【Mistral】【标识映射】每次请求保持确定性，并为规范化碰撞分配不同标识。</summary>
        /// <param name="id">原始标识。</param><returns>映射后的调用 ID。</returns>
        public string Normalize(string id)
        {
            if (_idMap.TryGetValue(id, out var existing))
            {
                return existing;
            }

            var attempt = 0;
            while (true)
            {
                var candidate = Derive(id, attempt);
                if (!_reverseMap.TryGetValue(candidate, out var owner) || owner == id)
                {
                    _idMap[id] = candidate;
                    _reverseMap[candidate] = id;
                    return candidate;
                }

                attempt++;
            }
        }

        /// <summary>【Mistral】【标识派生】使用主线短摘要与规范化种子派生九字符 ID。</summary>
        /// <param name="id">原始标识。</param><param name="attempt">碰撞重试序号。</param><returns>派生 ID。</returns>
        internal static string Derive(string id, int attempt)
        {
            var normalized = new string(id.Where(char.IsAsciiLetterOrDigit).ToArray());
            if (attempt == 0 && normalized.Length == MistralToolCallIdLength)
            {
                return normalized;
            }

            var seedBase = normalized.Length == 0 ? id : normalized;
            var seed = attempt == 0 ? seedBase : $"{seedBase}:{attempt}";
            return new string(ShortHash.Compute(seed)
                .Where(char.IsAsciiLetterOrDigit)
                .Take(MistralToolCallIdLength)
                .ToArray());
        }
    }
}

public record MistralOptions : StreamOptions
{
    public MistralToolChoice? ToolChoice { get; init; }
    public string? PromptMode { get; init; }
    public string? ReasoningEffort { get; init; }
}

public sealed record MistralToolChoice
{
    private MistralToolChoice(string kind, string? functionName)
    {
        Kind = kind;
        FunctionName = functionName;
    }

    public string Kind { get; }
    public string? FunctionName { get; }
    public bool IsFunction => FunctionName is not null;

    public static MistralToolChoice FromString(string choice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(choice);
        return new MistralToolChoice(choice, functionName: null);
    }

    public static MistralToolChoice Function(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new MistralToolChoice("function", name);
    }

    public static implicit operator MistralToolChoice(string choice) =>
        FromString(choice);

    public static implicit operator string?(MistralToolChoice? choice) =>
        choice?.ToString();

    public override string ToString() =>
        IsFunction ? $"function:{FunctionName}" : Kind;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(float?))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(decimal?))]
internal partial class MistralJsonContext : JsonSerializerContext;
