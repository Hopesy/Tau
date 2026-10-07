using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Streaming;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.Google;

public sealed class GoogleVertexProvider : IStreamProvider
{
    private readonly HttpClient _httpClient;
    private readonly GoogleVertexAccessTokenResolver _accessTokenResolver;

    public GoogleVertexProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? TauHttpClientFactory.Create();
        _accessTokenResolver = new GoogleVertexAccessTokenResolver(_httpClient);
    }

    public string Api => "google-vertex";

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();
        var parser = GoogleStreamParser.Create(model, Api, stream);
        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream, parser, reasoning: null).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (options.Signal.IsCancellationRequested)
            {
                parser.PushError(StreamOptionHelpers.AbortedErrorMessage, StopReason.Aborted);
            }
            catch (Exception ex)
            {
                parser.PushError(ex.Message);
            }
        });
        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
    {
        options = SimpleTokenOptions.WithContextLimit(model, context, options);
        var stream = new AssistantMessageStream();
        var parser = GoogleStreamParser.Create(model, Api, stream);
        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream, parser, options.Reasoning).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (options.Signal.IsCancellationRequested)
            {
                parser.PushError(StreamOptionHelpers.AbortedErrorMessage, StopReason.Aborted);
            }
            catch (Exception ex)
            {
                parser.PushError(ex.Message);
            }
        });
        return stream;
    }

    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        StreamOptions options,
        AssistantMessageStream stream,
        GoogleStreamParser parser,
        ThinkingLevel? reasoning)
    {
        options.Signal.ThrowIfCancellationRequested();

        var apiKey = options.ApiKey;
        string? accessToken = null;
        if (EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey) ||
            (string.IsNullOrWhiteSpace(apiKey) && GoogleVertexAccessTokenResolver.HasCredentialsFile(options)))
        {
            accessToken = await _accessTokenResolver.ResolveAsync(options, options.Signal).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken) && EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey))
            {
                parser.PushError("Vertex ADC credentials were requested, but no access token could be resolved. Set GOOGLE_APPLICATION_CREDENTIALS or provide GOOGLE_CLOUD_API_KEY.");
                return;
            }
        }

        var url = BuildUrl(model, options);
        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(context, options, model, reasoning)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, GoogleVertexRequestJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        else if (!string.IsNullOrWhiteSpace(apiKey) && !EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        }

        request.Headers.TryAddWithoutValidation("User-Agent", ProviderHttpHeaders.UserAgent());
        ProviderHttpHeaders.Apply(request, model.Headers);
        ProviderHttpHeaders.Apply(request, options.Headers);

        using var requestTimeout = StreamOptionHelpers.CreateRequestTimeout(options);
        try
        {
            await StreamOptionHelpers.ApplyHeadersCallbackAsync(options, model, request).ConfigureAwait(false);
            using var response = await ProviderHttpRetry.SendAsync(_httpClient, request, model, options, requestTimeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
                parser.PushError($"Google Vertex API error {(int)response.StatusCode}: {errorBody}");
                return;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);
            parser.EmitStart();
            await foreach (var sse in SseParser.ParseAsync(responseStream, requestTimeout.Token))
            {
                if (string.IsNullOrEmpty(sse.Data))
                {
                    continue;
                }

                await StreamOptionHelpers.InvokeProviderStreamEventAsync(options, model, sse.Data).ConfigureAwait(false);
                requestTimeout.Token.ThrowIfCancellationRequested();
                if (parser.ParseChunk(sse.Data))
                {
                    return;
                }
            }

            requestTimeout.Token.ThrowIfCancellationRequested();
            parser.EmitDone();
        }
        catch (OperationCanceledException ex) when (requestTimeout.IsTimeoutCancellation)
        {
            throw requestTimeout.CreateTimeoutException(ex);
        }
    }

    private static string BuildUrl(Model model, StreamOptions options)
    {
        if (!string.IsNullOrWhiteSpace(model.BaseUrl))
        {
            return $"{model.BaseUrl.TrimEnd('/')}/models/{model.Id}:streamGenerateContent?alt=sse";
        }

        var project = GoogleVertexAccessTokenResolver.ResolveProjectId(options) ??
                      ProviderEnvironment.GetValue("GOOGLE_CLOUD_PROJECT", options.Env) ??
                      ProviderEnvironment.GetValue("GCLOUD_PROJECT", options.Env);
        var location = GoogleVertexAccessTokenResolver.ResolveLocation(options) ??
                       ProviderEnvironment.GetValue("GOOGLE_CLOUD_LOCATION", options.Env);
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(location))
        {
            throw new InvalidOperationException("Vertex AI requires GOOGLE_CLOUD_PROJECT/GCLOUD_PROJECT and GOOGLE_CLOUD_LOCATION when model.BaseUrl is not set.");
        }

        return $"https://{location}-aiplatform.googleapis.com/v1/projects/{project}/locations/{location}/publishers/google/models/{model.Id}:streamGenerateContent?alt=sse";
    }

    private static Dictionary<string, object> BuildRequestBody(
        LlmContext context,
        StreamOptions options,
        Model model,
        ThinkingLevel? reasoning)
    {
        context = Transcript.ResolveContext(context);
        context = MessageTransformer.DowngradeUnsupportedImages(context, model);
        var body = new Dictionary<string, object>
        {
            ["contents"] = GoogleMessageConverter.ConvertMessages(model, context.Messages)
        };

        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            body["systemInstruction"] = new Dictionary<string, object>
            {
                ["role"] = "system",
                ["parts"] = new List<object> { new Dictionary<string, object> { ["text"] = UnicodeTextSanitizer.RemoveUnpairedSurrogates(context.SystemPrompt!) } }
            };
        }

        GoogleMessageConverter.ApplyTools(body, model, context.Tools, options);

        var generationConfig = new Dictionary<string, object>();
        if (options.Temperature.HasValue)
        {
            generationConfig["temperature"] = options.Temperature.Value;
        }

        if (options.MaxTokens.HasValue)
        {
            generationConfig["maxOutputTokens"] = options.MaxTokens.Value;
        }
        else if (model.MaxOutputTokens.HasValue)
        {
            generationConfig["maxOutputTokens"] = model.MaxOutputTokens.Value;
        }

        if (options.TopP.HasValue)
        {
            generationConfig["topP"] = options.TopP.Value;
        }

        if (options is GoogleVertexOptions { Thinking: { } thinking } && model.Reasoning)
        {
            generationConfig["thinkingConfig"] = GoogleThinking.BuildConfig(model, thinking);
        }
        else if (options is SimpleStreamOptions simple && model.Reasoning)
        {
            generationConfig["thinkingConfig"] = GoogleThinking.BuildConfig(model, GoogleThinking.ResolveSimple(model, reasoning, simple.ThinkingBudgets));
        }

        if (generationConfig.Count > 0)
        {
            body["generationConfig"] = generationConfig;
        }

        return body;
    }


}

public record GoogleVertexOptions : StreamOptions
{
    public string? AccessToken { get; init; }
    public string? CredentialsFile { get; init; }
    public string? Project { get; init; }
    public string? Location { get; init; }
    public string? ToolChoice { get; init; }
    public GoogleThinkingOptions? Thinking { get; init; }
}

public record GoogleVertexSimpleOptions : SimpleStreamOptions
{
    public string? AccessToken { get; init; }
    public string? CredentialsFile { get; init; }
    public string? Project { get; init; }
    public string? Location { get; init; }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
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
internal partial class GoogleVertexRequestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
