using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.OpenAiResponses;

public sealed class AzureOpenAiResponsesProvider : IStreamProvider
{
    private const string DefaultAzureApiVersion = "v1";
    private readonly HttpClient _httpClient;

    public AzureOpenAiResponsesProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? TauHttpClientFactory.Create();
    }

    public string Api => "azure-openai-responses";

    public bool SupportsTranscriptContext => true;

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();
        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (options.Signal.IsCancellationRequested)
            {
                StreamOptionHelpers.PushAborted(stream, model, Api);
            }
            catch (Exception ex)
            {
                stream.Push(new ErrorEvent(ex.Message));
            }
        });
        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
    {
        options = SimpleTokenOptions.WithContextLimit(model, context, options);
        var reasoningEffort = OpenAiResponsesShared.MapReasoningEffort(options.Reasoning, model);
        var azureOptions = new AzureOpenAiResponsesOptions
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
            CacheRetention = options.CacheRetention,
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
            ReasoningEffort = reasoningEffort,
            ToolChoice = options.ToolChoice
        };
        return Stream(model, context, azureOptions);
    }

    /// <summary>【AI】【Azure Responses 请求】用同一语法输入映射发送请求并还原工具事件。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">原始上下文。</param>
    /// <param name="options">请求选项。</param>
    /// <param name="stream">事件输出。</param>
    /// <returns>请求处理任务。</returns>
    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        StreamOptions options,
        AssistantMessageStream stream)
    {
        if (StreamOptionHelpers.PushAbortedIfCanceled(options, stream, model, Api))
        {
            return;
        }

        var deploymentName = ResolveDeploymentName(model, options);
        var config = ResolveAzureConfig(model, options);
        var url = $"{config.BaseUrl}/responses?api-version={Uri.EscapeDataString(config.ApiVersion)}";
        var grammarInputs = OpenAiResponsesShared.CreateGrammarToolInputProperties(model, context);
        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(model, context, options, deploymentName, grammarInputs)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, OpenAiResponsesJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyAuthHeader(request, ResolveApiKey(options));
        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var requestTimeout = StreamOptionHelpers.CreateRequestTimeout(options);
        try
        {
            await StreamOptionHelpers.ApplyHeadersCallbackAsync(options, model, request).ConfigureAwait(false);
            using var response = await ProviderHttpRetry.SendAsync(_httpClient, request, model, options, requestTimeout.Token,
                retryTransportErrors: true).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
                stream.Push(new ErrorEvent($"{Api} error {(int)response.StatusCode}: {errorBody}"));
                return;
            }

            var partial = new AssistantMessage
            {
                Api = Api,
                Provider = model.Provider,
                Model = model.Id,
                Content = []
            };
            stream.Push(new StartEvent(partial));

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);
            await OpenAiResponsesShared.ProcessResponsesStreamAsync(
                responseStream,
                partial,
                stream,
                cancellationToken: requestTimeout.Token,
                grammarToolInputProperties: grammarInputs,
                mapCancellation: exception => requestTimeout.IsTimeoutCancellation ? requestTimeout.CreateTimeoutException(exception) : exception,
                onProviderStreamEvent: json => StreamOptionHelpers.InvokeProviderStreamEventAsync(options, model, json)).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (requestTimeout.IsTimeoutCancellation)
        {
            throw requestTimeout.CreateTimeoutException(ex);
        }
    }

    /// <summary>【AI】【Azure Responses】按部署和协议能力组装系统声明及工具。</summary>
    /// <param name="model">请求模型。</param>
    /// <param name="context">请求上下文。</param>
    /// <param name="options">生成选项。</param>
    /// <param name="deploymentName">Azure 部署名。</param>
    /// <param name="grammarInputs">折叠前的语法输入映射。</param>
    /// <returns>Azure 请求报文。</returns>
    private static Dictionary<string, object> BuildRequestBody(
        Model model,
        LlmContext context,
        StreamOptions options,
        string deploymentName,
        IReadOnlyDictionary<string, string> grammarInputs)
    {
        context = Transcript.ResolveTranscript(context, model.Compat?.SupportsMidConvoSystemMessages == true);
        var transcriptTools = Transcript.ResolveTranscriptTools(context.Messages,
            model.Compat?.SupportsAdditionalTools == true || model.Compat?.SupportsToolSearch == true);
        var body = new Dictionary<string, object>
        {
            ["model"] = deploymentName,
            ["input"] = OpenAiResponsesShared.ConvertResponsesMessages(model, context, includeSystemPrompt: true, grammarInputs),
            ["stream"] = true
        };

        if (options.MaxTokens.HasValue)
        {
            body["max_output_tokens"] = options.MaxTokens.Value;
        }

        if (options.Temperature.HasValue)
        {
            body["temperature"] = options.Temperature.Value;
        }

        if (options.TopP.HasValue)
        {
            body["top_p"] = options.TopP.Value;
        }

        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            body["prompt_cache_key"] = OpenAiResponsesShared.ClampOpenAiPromptCacheKey(options.SessionId)!;
        }

        var tools = OpenAiResponsesShared.ConvertResponsesToolsForModel(transcriptTools.RequestTools, model);
        if (tools.Count > 0)
        {
            body["tools"] = tools;
        }

        if (options is AzureOpenAiResponsesOptions azureOptions && azureOptions.ToolChoice is not null)
        {
            body["tool_choice"] = azureOptions.ToolChoice;
        }

        OpenAiResponsesShared.AddResponsesReasoning(body, model,
            (options as AzureOpenAiResponsesOptions)?.ReasoningEffort, (options as AzureOpenAiResponsesOptions)?.ReasoningSummary,
            preserveCopilotDefault: false);

        StreamOptionHelpers.ApplySamplingParams(body, model, options);

        return body;
    }

    /// <summary>【AI】【Azure配置】协议字段来自派生选项，公共环境始终来自原始选项。</summary>
    /// <param name="model">模型默认地址。</param><param name="options">原生或通用请求选项。</param><returns>地址及 API 版本。</returns>
    private static AzureConfig ResolveAzureConfig(Model model, StreamOptions options)
    {
        var azure = options as AzureOpenAiResponsesOptions;
        var apiVersion = FirstNonEmpty(
            azure?.AzureApiVersion,
            ProviderEnvironment.GetValue("AZURE_OPENAI_API_VERSION", options.Env),
            DefaultAzureApiVersion)!;

        var baseUrl = FirstNonEmpty(
            azure?.AzureBaseUrl,
            ProviderEnvironment.GetValue("AZURE_OPENAI_BASE_URL", options.Env));
        var resourceName = FirstNonEmpty(
            azure?.AzureResourceName,
            ProviderEnvironment.GetValue("AZURE_OPENAI_RESOURCE_NAME", options.Env));

        if (string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(resourceName))
        {
            baseUrl = $"https://{resourceName}.openai.azure.com/openai/v1";
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = model.BaseUrl;
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "Azure OpenAI base URL is required. Set AZURE_OPENAI_BASE_URL or AZURE_OPENAI_RESOURCE_NAME, or pass AzureBaseUrl, AzureResourceName, or model.BaseUrl.");
        }

        return new AzureConfig(baseUrl.TrimEnd('/'), apiVersion);
    }

    /// <summary>【AI】【Azure部署】显式部署名优先，通用选项也能提供请求环境中的部署映射。</summary>
    /// <param name="model">模型标识。</param><param name="options">请求选项。</param><returns>部署名。</returns>
    private static string ResolveDeploymentName(Model model, StreamOptions options)
    {
        if (options is AzureOpenAiResponsesOptions azure && !string.IsNullOrWhiteSpace(azure.AzureDeploymentName))
        {
            return azure.AzureDeploymentName!;
        }

        var map = ParseDeploymentNameMap(ProviderEnvironment.GetValue("AZURE_OPENAI_DEPLOYMENT_NAME_MAP", options.Env));
        return map.TryGetValue(model.Id, out var deploymentName) ? deploymentName : model.Id;
    }

    private static Dictionary<string, string> ParseDeploymentNameMap(string? value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return map;
        }

        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                string.IsNullOrWhiteSpace(parts[0]) ||
                string.IsNullOrWhiteSpace(parts[1]))
            {
                continue;
            }

            map[parts[0]] = parts[1];
        }

        return map;
    }

    /// <summary>【AI】【Azure认证】公共 ApiKey 优先于请求环境，避免通用选项在类型转换时丢失。</summary>
    /// <param name="options">通用或专用请求选项。</param><returns>可用密钥或空值。</returns>
    private static string? ResolveApiKey(StreamOptions options) =>
        string.IsNullOrWhiteSpace(options.ApiKey)
            ? ProviderEnvironment.GetValue("AZURE_OPENAI_API_KEY", options.Env)
            : options.ApiKey;

    private static void ApplyAuthHeader(HttpRequestMessage request, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey))
        {
            throw new InvalidOperationException(
                "Azure OpenAI API key is required. Set AZURE_OPENAI_API_KEY environment variable or pass it as an argument.");
        }

        request.Headers.TryAddWithoutValidation("api-key", apiKey);
    }

    private static void ApplyHeaders(HttpRequestMessage request, IDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var (key, value) in headers)
        {
            request.Headers.Remove(key);
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private sealed record AzureConfig(string BaseUrl, string ApiVersion);
}
