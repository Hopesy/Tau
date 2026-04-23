using System.Text;
using System.Text.Json;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.Anthropic;

/// <summary>
/// Anthropic Messages API streaming provider.
/// Endpoint: POST /v1/messages with "stream": true.
/// </summary>
public sealed class AnthropicProvider : IStreamProvider
{
    private const string DefaultAnthropicVersion = "2023-06-01";
    private readonly HttpClient _httpClient;

    public AnthropicProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public string Api => "anthropic-messages";

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
        var baseUrl = model.BaseUrl?.TrimEnd('/') ?? "https://api.anthropic.com";
        var url = $"{baseUrl}/v1/messages";

        var body = BuildRequestBody(model, context, options, reasoning);
        var json = JsonSerializer.Serialize(body, AnthropicRequestJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var apiKey = options.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);

        request.Headers.TryAddWithoutValidation("anthropic-version", DefaultAnthropicVersion);

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
            stream.Push(new ErrorEvent($"Anthropic API error {(int)response.StatusCode}: {errorBody}"));
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

        var parser = new AnthropicStreamParser(initial, stream);

        await foreach (var sse in SseParser.ParseAsync(responseStream))
        {
            if (string.IsNullOrEmpty(sse.Data))
                continue;
            parser.ParseEvent(sse.EventType, sse.Data);
        }
    }

    private static Dictionary<string, object> BuildRequestBody(
        Model model,
        LlmContext context,
        StreamOptions options,
        ThinkingLevel? reasoning)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model.Id,
            ["stream"] = true,
            ["max_tokens"] = options.MaxTokens ?? model.MaxOutputTokens ?? 4096
        };

        if (!string.IsNullOrEmpty(context.SystemPrompt))
            body["system"] = context.SystemPrompt!;

        body["messages"] = AnthropicMessageConverter.ConvertMessages(context.Messages);

        if (context.Tools is { Count: > 0 })
            body["tools"] = AnthropicMessageConverter.ConvertTools(context.Tools);

        if (options.Temperature.HasValue)
            body["temperature"] = options.Temperature.Value;
        if (options.TopP.HasValue)
            body["top_p"] = options.TopP.Value;

        if (reasoning.HasValue && model.Reasoning)
        {
            var budget = reasoning.Value switch
            {
                ThinkingLevel.Minimal => 1024,
                ThinkingLevel.Low => 4096,
                ThinkingLevel.Medium => 8192,
                ThinkingLevel.High => 16384,
                ThinkingLevel.ExtraHigh => 32768,
                _ => 4096
            };
            body["thinking"] = new Dictionary<string, object>
            {
                ["type"] = "enabled",
                ["budget_tokens"] = budget
            };
        }

        return body;
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object>))]
internal partial class AnthropicRequestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
