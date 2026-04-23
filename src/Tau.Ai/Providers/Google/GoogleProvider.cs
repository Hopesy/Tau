using System.Text;
using System.Text.Json;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.Google;

/// <summary>
/// Google Gemini streaming provider.
/// Endpoint: POST /v1beta/models/{model}:streamGenerateContent?alt=sse
/// </summary>
public sealed class GoogleProvider : IStreamProvider
{
    private readonly HttpClient _httpClient;

    public GoogleProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public string Api => "google-generative-language";

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();

        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream, reasoning: null).ConfigureAwait(false);
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
        var stream = new AssistantMessageStream();

        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream, options.Reasoning).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                stream.Push(new ErrorEvent(ex.Message));
            }
        });

        return stream;
    }

    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        StreamOptions options,
        AssistantMessageStream stream,
        ThinkingLevel? reasoning)
    {
        var baseUrl = model.BaseUrl?.TrimEnd('/') ?? "https://generativelanguage.googleapis.com";
        var url = $"{baseUrl}/v1beta/models/{model.Id}:streamGenerateContent?alt=sse";

        var body = BuildRequestBody(context, options, model, reasoning);
        var json = JsonSerializer.Serialize(body, GoogleRequestJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var apiKey = options.ApiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);

        if (model.Headers is not null)
            foreach (var (key, value) in model.Headers)
                request.Headers.TryAddWithoutValidation(key, value);

        if (options.Headers is not null)
            foreach (var (key, value) in options.Headers)
                request.Headers.TryAddWithoutValidation(key, value);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            stream.Push(new ErrorEvent($"Google API error {(int)response.StatusCode}: {errorBody}"));
            return;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

        var initial = new AssistantMessage
        {
            Api = Api,
            Provider = model.Provider,
            Model = model.Id,
            Content = []
        };

        var parser = new GoogleStreamParser(initial, stream);
        parser.EmitStart();

        await foreach (var sse in SseParser.ParseAsync(responseStream))
        {
            if (string.IsNullOrEmpty(sse.Data))
                continue;
            parser.ParseChunk(sse.Data);
        }

        parser.EmitDone();
    }

    private static Dictionary<string, object> BuildRequestBody(
        LlmContext context,
        StreamOptions options,
        Model model,
        ThinkingLevel? reasoning)
    {
        var body = new Dictionary<string, object>
        {
            ["contents"] = GoogleMessageConverter.ConvertMessages(context.Messages)
        };

        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            body["systemInstruction"] = new Dictionary<string, object>
            {
                ["role"] = "system",
                ["parts"] = new List<object>
                {
                    new Dictionary<string, object> { ["text"] = context.SystemPrompt! }
                }
            };
        }

        if (context.Tools is { Count: > 0 })
            body["tools"] = GoogleMessageConverter.ConvertTools(context.Tools);

        var generationConfig = new Dictionary<string, object>();
        if (options.Temperature.HasValue)
            generationConfig["temperature"] = options.Temperature.Value;
        if (options.MaxTokens.HasValue)
            generationConfig["maxOutputTokens"] = options.MaxTokens.Value;
        else if (model.MaxOutputTokens.HasValue)
            generationConfig["maxOutputTokens"] = model.MaxOutputTokens.Value;
        if (options.TopP.HasValue)
            generationConfig["topP"] = options.TopP.Value;

        if (reasoning.HasValue && model.Reasoning)
        {
            var budget = reasoning.Value switch
            {
                ThinkingLevel.Minimal => 512,
                ThinkingLevel.Low => 2048,
                ThinkingLevel.Medium => 8192,
                ThinkingLevel.High => 16384,
                ThinkingLevel.ExtraHigh => 24576,
                _ => 2048
            };
            generationConfig["thinkingConfig"] = new Dictionary<string, object>
            {
                ["includeThoughts"] = true,
                ["thinkingBudget"] = budget
            };
        }

        if (generationConfig.Count > 0)
            body["generationConfig"] = generationConfig;

        return body;
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object>))]
internal partial class GoogleRequestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
