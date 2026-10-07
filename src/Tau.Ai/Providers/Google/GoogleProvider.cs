using System.Text;
using System.Text.Json;
using Tau.Ai.Streaming;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.Google;

/// <summary>
/// Google Gemini streaming provider.
/// Endpoint: POST /v1beta/models/{model}:streamGenerateContent?alt=sse
/// </summary>
public sealed partial class GoogleProvider : IStreamProvider
{
    private readonly HttpClient _httpClient;

    public GoogleProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? TauHttpClientFactory.Create();
    }

    public string Api => "google-generative-language";

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

        var url = BuildEndpoint(model);

        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(context, options, model, reasoning)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, GoogleRequestJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var apiKey = options.ApiKey ??
            ProviderEnvironment.GetValue("GEMINI_API_KEY", options.Env);
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);

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
                parser.PushError($"Google API error {(int)response.StatusCode}: {errorBody}");
                return;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);

            parser.EmitStart();

            await foreach (var sse in SseParser.ParseAsync(responseStream, requestTimeout.Token))
            {
                if (string.IsNullOrEmpty(sse.Data))
                    continue;
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
                ["parts"] = new List<object>
                {
                    new Dictionary<string, object> { ["text"] = UnicodeTextSanitizer.RemoveUnpairedSurrogates(context.SystemPrompt!) }
                }
            };
        }

        GoogleMessageConverter.ApplyTools(body, model, context.Tools, options);

        var generationConfig = new Dictionary<string, object>();
        if (options.Temperature.HasValue)
            generationConfig["temperature"] = options.Temperature.Value;
        if (options.MaxTokens.HasValue)
            generationConfig["maxOutputTokens"] = options.MaxTokens.Value;
        else if (model.MaxOutputTokens.HasValue)
            generationConfig["maxOutputTokens"] = model.MaxOutputTokens.Value;
        if (options.TopP.HasValue)
            generationConfig["topP"] = options.TopP.Value;

        if (options is GoogleOptions { Thinking: { } thinking } && model.Reasoning)
        {
            generationConfig["thinkingConfig"] = GoogleThinking.BuildConfig(model, thinking);
        }
        else if (options is SimpleStreamOptions simple && model.Reasoning)
        {
            generationConfig["thinkingConfig"] = GoogleThinking.BuildConfig(model, GoogleThinking.ResolveSimple(model, reasoning, simple.ThinkingBudgets));
        }

        if (generationConfig.Count > 0)
            body["generationConfig"] = generationConfig;

        return body;
    }


}

public record GoogleThinkingOptions
{
    public bool Enabled { get; init; }
    public int? BudgetTokens { get; init; }
    public string? Level { get; init; }
}

public record GoogleOptions : StreamOptions
{
    public string? ToolChoice { get; init; }
    public GoogleThinkingOptions? Thinking { get; init; }
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
internal partial class GoogleRequestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
