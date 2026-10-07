using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.OpenAiResponses;

public sealed class OpenAiResponsesProvider : IStreamProvider
{
    private readonly HttpClient _httpClient;

    public OpenAiResponsesProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? TauHttpClientFactory.Create();
    }

    public string Api => "openai-responses";

    public bool SupportsTranscriptContext => true;

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        options = StreamOptionHelpers.WithCacheDefaults(options);
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
        var responseOptions = new OpenAiResponsesOptions
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
            ReasoningEffort = reasoningEffort,
            ToolChoice = options.ToolChoice
        };
        return Stream(model, context, responseOptions);
    }

    /// <summary>【AI】【Responses 请求】共用原始语法映射完成报文发送与响应还原。</summary>
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

        var baseUrl = model.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
        var url = $"{baseUrl}/responses";
        var grammarInputs = OpenAiResponsesShared.CreateGrammarToolInputProperties(model, context);
        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(model, context, options, grammarInputs)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, OpenAiResponsesJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyAuthHeader(request, options.ApiKey);
        SessionAffinityHeaders.Apply(request, model, options, "openai-responses");
        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, ResolveDynamicHeaders(model, context));
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
                requestedServiceTier: (options as OpenAiResponsesOptions)?.ServiceTier,
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

    /// <summary>【AI】【Responses】按模型能力组装会话声明与顶层工具。</summary>
    /// <param name="model">请求模型。</param>
    /// <param name="context">请求上下文。</param>
    /// <param name="options">生成选项。</param>
    /// <param name="grammarInputs">折叠前的语法输入映射。</param>
    /// <returns>Responses 请求报文。</returns>
    private static Dictionary<string, object> BuildRequestBody(Model model, LlmContext context, StreamOptions options, IReadOnlyDictionary<string, string> grammarInputs)
    {
        context = Transcript.ResolveTranscript(context, model.Compat?.SupportsMidConvoSystemMessages == true);
        var transcriptTools = Transcript.ResolveTranscriptTools(context.Messages,
            model.Compat?.SupportsAdditionalTools == true || model.Compat?.SupportsToolSearch == true);
        var body = new Dictionary<string, object>
        {
            ["model"] = model.Id,
            ["input"] = OpenAiResponsesShared.ConvertResponsesMessages(model, context, includeSystemPrompt: true, grammarInputs),
            ["stream"] = true,
            ["store"] = false
        };

        var tools = OpenAiResponsesShared.ConvertResponsesToolsForModel(transcriptTools.RequestTools, model);
        if (tools.Count > 0)
        {
            body["tools"] = tools;
            body["tool_choice"] = "auto";
            body["parallel_tool_calls"] = true;
        }

        if (options is OpenAiResponsesOptions responseOptionsWithToolChoice && responseOptionsWithToolChoice.ToolChoice is not null)
        {
            body["tool_choice"] = responseOptionsWithToolChoice.ToolChoice;
        }

        OpenAiResponsesShared.AddBaseParameters(body, model, options);
        OpenAiResponsesShared.AddResponsesReasoning(body, model,
            (options as OpenAiResponsesOptions)?.ReasoningEffort, (options as OpenAiResponsesOptions)?.ReasoningSummary);
        if (options is OpenAiResponsesOptions responseOptions)
        {
            AddResponsesOptions(body, responseOptions);
        }

        StreamOptionHelpers.ApplySamplingParams(body, model, options);

        return body;
    }

    /// <summary>【AI】【Responses 请求】附加工具选择和服务等级。</summary>
    /// <param name="body">待填充的请求体。</param>
    /// <param name="options">供应商专用选项。</param>
    private static void AddResponsesOptions(Dictionary<string, object> body, OpenAiResponsesOptions options)
    {
        if (options.ToolChoice is not null)
        {
            body["tool_choice"] = options.ToolChoice;
        }

        if (!string.IsNullOrWhiteSpace(options.ServiceTier))
        {
            body["service_tier"] = options.ServiceTier!;
        }
    }

    private static void ApplyAuthHeader(HttpRequestMessage request, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey))
        {
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private static IDictionary<string, string>? ResolveDynamicHeaders(Model model, LlmContext context)
    {
        return string.Equals(model.Provider, "github-copilot", StringComparison.OrdinalIgnoreCase)
            ? GitHubCopilotHeaders.BuildDynamicHeaders(context.Messages)
            : null;
    }
}
