using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.OpenAi;

/// <summary>
/// OpenAI chat completions streaming provider.
/// Also works with OpenAI-compatible APIs (together.ai, groq, etc.) via Model.BaseUrl.
/// </summary>
public sealed class OpenAiProvider : IStreamProvider
{
    private readonly HttpClient _httpClient;

    public OpenAiProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public string Api => "openai-chat-completions";

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();

        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream).ConfigureAwait(false);
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
        return Stream(model, context, options);
    }

    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        StreamOptions options,
        AssistantMessageStream stream)
    {
        var baseUrl = model.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
        var url = $"{baseUrl}/chat/completions";

        var body = BuildRequestBody(model, context, options);
        var json = JsonSerializer.Serialize(body, OpenAiRequestJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var apiKey = options.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

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
            stream.Push(new ErrorEvent($"OpenAI API error {(int)response.StatusCode}: {errorBody}"));
            return;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

        var partial = new AssistantMessage
        {
            Api = Api,
            Provider = model.Provider,
            Model = model.Id,
            Content = []
        };
        stream.Push(new StartEvent(partial));

        var toolCallAccumulators = new Dictionary<int, OpenAiStreamParser.ToolCallAccumulator>();
        var contentIndex = 0;

        await foreach (var sse in SseParser.ParseAsync(responseStream))
        {
            if (sse.Data == "[DONE]")
                break;

            OpenAiStreamParser.ParseChunk(
                sse.Data, stream, ref partial, ref toolCallAccumulators, ref contentIndex);
        }
    }

    private static Dictionary<string, object> BuildRequestBody(
        Model model, LlmContext context, StreamOptions options)
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
                ["content"] = context.SystemPrompt!
            });
        }

        // Serialize existing messages
        var converted = OpenAiMessageConverter.ConvertMessages(context.Messages);
        foreach (var msg in converted.EnumerateArray())
            messages.Add(msg);

        body["messages"] = messages;

        if (context.Tools is { Count: > 0 })
        {
            var tools = OpenAiMessageConverter.ConvertTools(context.Tools);
            body["tools"] = tools;
        }

        if (options.Temperature.HasValue)
            body["temperature"] = options.Temperature.Value;
        if (options.MaxTokens.HasValue)
            body["max_tokens"] = options.MaxTokens.Value;
        if (options.TopP.HasValue)
            body["top_p"] = options.TopP.Value;

        return body;
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object>))]
internal partial class OpenAiRequestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
