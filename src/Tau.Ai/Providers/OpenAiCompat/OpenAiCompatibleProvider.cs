using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tau.Ai.Streaming;

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
        _httpClient = httpClient ?? new HttpClient();
    }

    public string Api => _api;

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

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
        Stream(model, context, options);

    private async Task StreamInternalAsync(Model model, LlmContext context, StreamOptions options, AssistantMessageStream stream)
    {
        if (StreamOptionHelpers.PushAbortedIfCanceled(options, stream, model, Api))
        {
            return;
        }

        var baseUrl = string.IsNullOrWhiteSpace(model.BaseUrl) ? _defaultBaseUrl : model.BaseUrl!.TrimEnd('/');
        var url = $"{baseUrl}{_requestPath}";

        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(model, context, options)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, OpenAiCompatJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        ApplyAuthHeader(request, options.ApiKey);
        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);

        using var requestTimeout = StreamOptionHelpers.CreateRequestTimeout(options);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestTimeout.Token).ConfigureAwait(false);
            await StreamOptionHelpers.InvokeResponseCallbackAsync(options, model, response).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
                stream.Push(new ErrorEvent($"{_api} error {(int)response.StatusCode}: {errorBody}"));
                return;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);

            var partial = new AssistantMessage
            {
                Api = _api,
                Provider = model.Provider,
                Model = model.Id,
                Content = []
            };
            stream.Push(new StartEvent(partial));

            var toolCallAccumulators = new Dictionary<int, OpenAi.OpenAiStreamParser.ToolCallAccumulator>();
            var contentIndex = 0;

            await foreach (var sse in SseParser.ParseAsync(responseStream, requestTimeout.Token))
            {
                if (sse.Data == "[DONE]")
                {
                    break;
                }

                OpenAi.OpenAiStreamParser.ParseChunk(
                    sse.Data,
                    stream,
                    ref partial,
                    ref toolCallAccumulators,
                    ref contentIndex);
            }
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

    private static Dictionary<string, object> BuildRequestBody(Model model, LlmContext context, StreamOptions options)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model.Id,
            ["stream"] = true,
            ["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true }
        };

        var messages = new List<object>();
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            messages.Add(new Dictionary<string, object>
            {
                ["role"] = "system",
                ["content"] = UnicodeTextSanitizer.RemoveUnpairedSurrogates(context.SystemPrompt!)
            });
        }

        foreach (var msg in OpenAi.OpenAiMessageConverter.ConvertMessages(
                     context.Messages,
                     requiresToolResultName: model.Compat?.RequiresToolResultName ?? false,
                     requiresAssistantAfterToolResult: model.Compat?.RequiresAssistantAfterToolResult ?? false).EnumerateArray())
        {
            messages.Add(msg);
        }

        body["messages"] = messages;

        if (context.Tools is { Count: > 0 })
        {
            body["tools"] = OpenAi.OpenAiMessageConverter.ConvertTools(context.Tools);
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

        return body;
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<object>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(object))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(float))]
[System.Text.Json.Serialization.JsonSerializable(typeof(float?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(decimal))]
[System.Text.Json.Serialization.JsonSerializable(typeof(decimal?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TimeSpan))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TimeSpan?))]
internal partial class OpenAiCompatJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
