using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.Google;

public sealed class GoogleVertexProvider : IStreamProvider
{
    private readonly HttpClient _httpClient;
    private readonly GoogleVertexAccessTokenResolver _accessTokenResolver;

    public GoogleVertexProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _accessTokenResolver = new GoogleVertexAccessTokenResolver(_httpClient);
    }

    public string Api => "google-vertex";

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();
        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream, reasoning: null).ConfigureAwait(false);
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
        var stream = new AssistantMessageStream();
        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream, options.Reasoning).ConfigureAwait(false);
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

    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        StreamOptions options,
        AssistantMessageStream stream,
        ThinkingLevel? reasoning)
    {
        if (StreamOptionHelpers.PushAbortedIfCanceled(options, stream, model, Api))
        {
            return;
        }

        var apiKey = options.ApiKey;
        string? accessToken = null;
        if (EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey) ||
            (string.IsNullOrWhiteSpace(apiKey) && GoogleVertexAccessTokenResolver.HasCredentialsFile(options)))
        {
            accessToken = await _accessTokenResolver.ResolveAsync(options, options.Signal).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken) && EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey))
            {
                stream.Push(new ErrorEvent("Vertex ADC credentials were requested, but no access token could be resolved. Set GOOGLE_APPLICATION_CREDENTIALS or provide GOOGLE_CLOUD_API_KEY."));
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
                stream.Push(new ErrorEvent($"Google Vertex API error {(int)response.StatusCode}: {errorBody}"));
                return;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);
            var initial = new AssistantMessage
            {
                Api = Api,
                Provider = model.Provider,
                Model = model.Id,
                Content = []
            };

            var parser = new GoogleStreamParser(initial, stream);
            parser.EmitStart();
            await foreach (var sse in SseParser.ParseAsync(responseStream, requestTimeout.Token))
            {
                if (string.IsNullOrEmpty(sse.Data))
                {
                    continue;
                }

                if (parser.ParseChunk(sse.Data))
                {
                    return;
                }
            }

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
        var body = new Dictionary<string, object>
        {
            ["contents"] = GoogleMessageConverter.ConvertMessages(context.Messages)
        };

        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            body["systemInstruction"] = new Dictionary<string, object>
            {
                ["role"] = "system",
                ["parts"] = new List<object> { new Dictionary<string, object> { ["text"] = UnicodeTextSanitizer.RemoveUnpairedSurrogates(context.SystemPrompt!) } }
            };
        }

        if (context.Tools is { Count: > 0 })
        {
            body["tools"] = GoogleMessageConverter.ConvertTools(context.Tools);
        }

        if (context.Tools is { Count: > 0 } && options is GoogleVertexOptions { ToolChoice: { Length: > 0 } toolChoice })
        {
            body["toolConfig"] = BuildToolConfig(toolChoice);
        }

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
            generationConfig["thinkingConfig"] = BuildThinkingConfig(model, thinking);
        }
        else if (reasoning.HasValue && model.Reasoning)
        {
            generationConfig["thinkingConfig"] = new Dictionary<string, object>
            {
                ["includeThoughts"] = true,
                ["thinkingBudget"] = GetGoogleThinkingBudget(model, (options as SimpleStreamOptions)?.ThinkingBudgets, reasoning.Value)
            };
        }

        if (generationConfig.Count > 0)
        {
            body["generationConfig"] = generationConfig;
        }

        return body;
    }

    private static Dictionary<string, object> BuildToolConfig(string toolChoice) => new()
    {
        ["functionCallingConfig"] = new Dictionary<string, object>
        {
            ["mode"] = MapToolChoice(toolChoice)
        }
    };

    private static string MapToolChoice(string toolChoice) =>
        toolChoice.Trim().ToLowerInvariant() switch
        {
            "none" => "NONE",
            "any" => "ANY",
            _ => "AUTO"
        };

    private static Dictionary<string, object> BuildThinkingConfig(Model model, GoogleThinkingOptions thinking)
    {
        if (!thinking.Enabled)
        {
            return GetDisabledThinkingConfig(model);
        }

        var config = new Dictionary<string, object>
        {
            ["includeThoughts"] = true
        };

        if (!string.IsNullOrWhiteSpace(thinking.Level))
        {
            config["thinkingLevel"] = thinking.Level!;
        }
        else if (thinking.BudgetTokens.HasValue)
        {
            config["thinkingBudget"] = thinking.BudgetTokens.Value;
        }

        return config;
    }

    private static Dictionary<string, object> GetDisabledThinkingConfig(Model model)
    {
        if (GoogleProvider.IsGemini3ProModel(model.Id))
        {
            return new Dictionary<string, object> { ["thinkingLevel"] = "LOW" };
        }

        if (GoogleProvider.IsGemini3FlashModel(model.Id))
        {
            return new Dictionary<string, object> { ["thinkingLevel"] = "MINIMAL" };
        }

        return new Dictionary<string, object> { ["thinkingBudget"] = 0 };
    }

    private static int GetGoogleThinkingBudget(Model model, ThinkingBudgets? budgets, ThinkingLevel reasoning)
    {
        var id = model.Id;
        if (id.Contains("2.5-pro", StringComparison.OrdinalIgnoreCase))
        {
            return StreamOptionHelpers.GetThinkingBudget(
                budgets,
                reasoning,
                defaultMinimal: 128,
                defaultLow: 2_048,
                defaultMedium: 8_192,
                defaultHigh: 32_768);
        }

        if (id.Contains("2.5-flash", StringComparison.OrdinalIgnoreCase))
        {
            return StreamOptionHelpers.GetThinkingBudget(
                budgets,
                reasoning,
                defaultMinimal: 128,
                defaultLow: 2_048,
                defaultMedium: 8_192,
                defaultHigh: 24_576);
        }

        return StreamOptionHelpers.GetCustomThinkingBudget(budgets, reasoning) ?? -1;
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
