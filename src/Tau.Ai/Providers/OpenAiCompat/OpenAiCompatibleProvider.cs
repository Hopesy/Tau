using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tau.Ai.Streaming;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.OpenAiCompat;

internal sealed class OpenAiCompatibleProvider : IStreamProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _api;
    private readonly string _defaultBaseUrl;
    private readonly string _requestPath;
    private readonly string _authHeaderName;
    private readonly string? _authHeaderPrefix;

    public OpenAiCompatibleProvider(
        string api,
        string defaultBaseUrl,
        string requestPath = "/chat/completions",
        string authHeaderName = "Authorization",
        string? authHeaderPrefix = "Bearer ",
        HttpClient? httpClient = null)
    {
        _api = api;
        _defaultBaseUrl = defaultBaseUrl;
        _requestPath = requestPath;
        _authHeaderName = authHeaderName;
        _authHeaderPrefix = authHeaderPrefix;
        _httpClient = httpClient ?? TauHttpClientFactory.Create();
    }

    public string Api => _api;

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

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
        Stream(model, context, SimpleTokenOptions.WithContextLimit(model, context, options));

    /// <summary>【AI】【兼容协议】发送请求并使用相同的 grammar 映射处理历史与响应。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">会话上下文。</param>
    /// <param name="options">生成和传输选项。</param>
    /// <param name="stream">输出事件流。</param>
    /// <returns>HTTP 请求与响应解析任务。</returns>
    private async Task StreamInternalAsync(Model model, LlmContext context, StreamOptions options, AssistantMessageStream stream)
    {
        if (StreamOptionHelpers.PushAbortedIfCanceled(options, stream, model, Api))
        {
            return;
        }

        var baseUrl = string.IsNullOrWhiteSpace(model.BaseUrl) ? _defaultBaseUrl : model.BaseUrl!.TrimEnd('/');
        var url = $"{baseUrl}{_requestPath}";

        var grammarInputs = OpenAi.OpenAiMessageConverter.CreateGrammarToolInputProperties(model, context);
        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            OpenAi.OpenAiProvider.BuildRequestBody(model, context, options, grammarInputs)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, OpenAi.OpenAiRequestJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        ApplyAuthHeader(request, options.ApiKey);
        SessionAffinityHeaders.Apply(request, model, options, "openai-completions");
        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);

        using var requestTimeout = StreamOptionHelpers.CreateRequestTimeout(options);
        try
        {
            await StreamOptionHelpers.ApplyHeadersCallbackAsync(options, model, request).ConfigureAwait(false);
            using var response = await ProviderHttpRetry.SendAsync(_httpClient, request, model, options, requestTimeout.Token,
                retryTransportErrors: true).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
                stream.Push(new ErrorEvent($"{_api} error {(int)response.StatusCode}: {ErrorBody.Format(errorBody)}"));
                return;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);

            await OpenAi.OpenAiStreamParser.ProcessStreamAsync(responseStream, stream, model, _api,
                grammarInputs, model.Compat?.SupportsFinishReason != false, requestTimeout.Token, options.OnProviderStreamEvent).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (requestTimeout.IsTimeoutCancellation)
        {
            throw requestTimeout.CreateTimeoutException(ex);
        }
    }

    private void ApplyAuthHeader(HttpRequestMessage request, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || Auth.EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey))
        {
            return;
        }

        if (_authHeaderName.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                (_authHeaderPrefix ?? "Bearer ").TrimEnd(),
                _authHeaderPrefix is null ? apiKey : apiKey);
            return;
        }

        var headerValue = _authHeaderPrefix is null ? apiKey : $"{_authHeaderPrefix}{apiKey}";
        request.Headers.TryAddWithoutValidation(_authHeaderName, headerValue);
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

}
