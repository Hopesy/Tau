using System.Text;
using System.Text.Json;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.Google;

public sealed class GoogleGeminiCliProvider : IStreamProvider
{
    private readonly HttpClient _httpClient;

    public GoogleGeminiCliProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public string Api => "google-gemini-cli";

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
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            stream.Push(new ErrorEvent("Google Cloud Code Assist requires OAuth credentials. Import them into auth.json or provide an explicit apiKey payload."));
            return;
        }

        var credentials = ParseCredentials(options.ApiKey);
        var baseUrl = string.IsNullOrWhiteSpace(model.BaseUrl) ? "https://cloudcode-pa.googleapis.com" : model.BaseUrl!.TrimEnd('/');
        var url = $"{baseUrl}/v1internal:streamGenerateContent?alt=sse";

        var body = BuildRequestBody(model, context, credentials.ProjectId, options, reasoning);
        var json = JsonSerializer.Serialize(body, GoogleGeminiCliRequestJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credentials.Token);
        request.Headers.TryAddWithoutValidation("User-Agent", model.Provider.Equals("google-antigravity", StringComparison.OrdinalIgnoreCase)
            ? "antigravity/1.21.9 darwin/arm64"
            : "google-cloud-sdk vscode_cloudshelleditor/0.1");
        request.Headers.TryAddWithoutValidation("X-Goog-Api-Client", "gl-dotnet/tau");

        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            stream.Push(new ErrorEvent($"Google Gemini CLI API error {(int)response.StatusCode}: {errorBody}"));
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
            {
                continue;
            }

            using var doc = JsonDocument.Parse(sse.Data);
            if (doc.RootElement.TryGetProperty("response", out var responseElement))
            {
                parser.ParseChunk(responseElement.GetRawText());
            }
        }

        parser.EmitDone();
    }

    private static (string Token, string ProjectId) ParseCredentials(string apiKey)
    {
        using var doc = JsonDocument.Parse(apiKey);
        var token = doc.RootElement.TryGetProperty("token", out var tokenProp) ? tokenProp.GetString() : null;
        var projectId = doc.RootElement.TryGetProperty("projectId", out var projectProp) ? projectProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(projectId))
        {
            throw new InvalidOperationException("Google Gemini CLI credentials must contain token and projectId.");
        }

        return (token!, projectId!);
    }

    private static Dictionary<string, object> BuildRequestBody(
        Model model,
        LlmContext context,
        string projectId,
        StreamOptions options,
        ThinkingLevel? reasoning)
    {
        var request = new Dictionary<string, object>
        {
            ["contents"] = GoogleMessageConverter.ConvertMessages(context.Messages),
            ["sessionId"] = options.SessionId ?? $"tau-{Guid.NewGuid():N}"
        };

        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            request["systemInstruction"] = new Dictionary<string, object>
            {
                ["parts"] = new List<object> { new Dictionary<string, object> { ["text"] = context.SystemPrompt! } }
            };
        }

        if (context.Tools is { Count: > 0 })
        {
            request["tools"] = GoogleMessageConverter.ConvertTools(context.Tools);
        }

        var generationConfig = new Dictionary<string, object>();
        if (options.MaxTokens.HasValue)
        {
            generationConfig["maxOutputTokens"] = options.MaxTokens.Value;
        }
        if (options.Temperature.HasValue)
        {
            generationConfig["temperature"] = options.Temperature.Value;
        }
        if (reasoning.HasValue && model.Reasoning)
        {
            generationConfig["thinkingConfig"] = new Dictionary<string, object>
            {
                ["includeThoughts"] = true,
                ["thinkingBudget"] = reasoning.Value switch
                {
                    ThinkingLevel.Minimal => 1_024,
                    ThinkingLevel.Low => 2_048,
                    ThinkingLevel.Medium => 8_192,
                    ThinkingLevel.High => 16_384,
                    ThinkingLevel.ExtraHigh => 24_576,
                    _ => 2_048
                }
            };
        }
        if (generationConfig.Count > 0)
        {
            request["generationConfig"] = generationConfig;
        }

        return new Dictionary<string, object>
        {
            ["project"] = projectId,
            ["model"] = model.Id,
            ["request"] = request,
            ["userAgent"] = model.Provider.Equals("google-antigravity", StringComparison.OrdinalIgnoreCase)
                ? "antigravity"
                : "tau-coding-agent",
            ["requestId"] = $"tau-{Guid.NewGuid():N}"
        };
    }

    private static void ApplyHeaders(HttpRequestMessage request, IDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }
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
internal partial class GoogleGeminiCliRequestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
