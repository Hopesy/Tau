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

        var baseUrl = model.BaseUrl?.TrimEnd('/') ?? "https://generativelanguage.googleapis.com";
        var url = $"{baseUrl}/v1beta/models/{model.Id}:streamGenerateContent?alt=sse";

        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(context, options, model, reasoning)).ConfigureAwait(false);
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

        using var requestTimeout = StreamOptionHelpers.CreateRequestTimeout(options);
        try
        {
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token).ConfigureAwait(false);
            await StreamOptionHelpers.InvokeResponseCallbackAsync(options, model, response).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
                stream.Push(new ErrorEvent($"Google API error {(int)response.StatusCode}: {errorBody}"));
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
                    continue;
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
                    new Dictionary<string, object> { ["text"] = UnicodeTextSanitizer.RemoveUnpairedSurrogates(context.SystemPrompt!) }
                }
            };
        }

        if (context.Tools is { Count: > 0 })
        {
            body["tools"] = GoogleMessageConverter.ConvertTools(context.Tools);
        }

        if (context.Tools is { Count: > 0 } && options is GoogleOptions { ToolChoice: { Length: > 0 } toolChoice })
        {
            body["toolConfig"] = BuildToolConfig(toolChoice);
        }

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
            generationConfig["thinkingConfig"] = BuildThinkingConfig(model, thinking);
        }
        else if (reasoning.HasValue && model.Reasoning)
        {
            var budget = GetGoogleThinkingBudget(model, (options as SimpleStreamOptions)?.ThinkingBudgets, reasoning.Value);
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
        if (IsGemini3ProModel(model.Id))
        {
            return new Dictionary<string, object> { ["thinkingLevel"] = "LOW" };
        }

        if (IsGemini3FlashModel(model.Id) || IsGemma4Model(model.Id))
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

        if (id.Contains("2.5-flash-lite", StringComparison.OrdinalIgnoreCase))
        {
            return StreamOptionHelpers.GetThinkingBudget(
                budgets,
                reasoning,
                defaultMinimal: 512,
                defaultLow: 2_048,
                defaultMedium: 8_192,
                defaultHigh: 24_576);
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

    internal static bool IsGemini3ProModel(string modelId) =>
        modelId.Contains("gemini-3", StringComparison.OrdinalIgnoreCase) &&
        modelId.Contains("pro", StringComparison.OrdinalIgnoreCase);

    internal static bool IsGemini3FlashModel(string modelId) =>
        modelId.Contains("gemini-3", StringComparison.OrdinalIgnoreCase) &&
        modelId.Contains("flash", StringComparison.OrdinalIgnoreCase);

    internal static bool IsGemma4Model(string modelId) =>
        modelId.Contains("gemma-4", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("gemma4", StringComparison.OrdinalIgnoreCase);
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
