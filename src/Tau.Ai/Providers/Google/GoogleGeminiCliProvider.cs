using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.Google;

public sealed class GoogleGeminiCliProvider : IStreamProvider
{
    private const string DefaultEndpoint = "https://cloudcode-pa.googleapis.com";
    private const string AntigravityDailyEndpoint = "https://daily-cloudcode-pa.sandbox.googleapis.com";
    private const string AntigravityAutopushEndpoint = "https://autopush-cloudcode-pa.sandbox.googleapis.com";
    private const int MaxRetries = 3;
    private const int MaxEmptyStreamRetries = 2;
    private const string DefaultAntigravityVersion = "1.21.9";
    private const string ClaudeThinkingBetaHeader = "interleaved-thinking-2025-05-14";
    private const string AntigravitySystemInstruction =
        "You are Antigravity, a powerful agentic AI coding assistant designed by the Google Deepmind team working on Advanced Agentic Coding." +
        "You are pair programming with a USER to solve their coding task. The task may require creating a new codebase, modifying or debugging an existing codebase, or simply answering a question." +
        "**Absolute paths only**" +
        "**Proactiveness**";

    private static readonly Regex ResetAfterRegex = new("reset after (?:(\\d+)h)?(?:(\\d+)m)?(\\d+(?:\\.\\d+)?)s", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RetryInRegex = new("Please retry in ([0-9.]+)(ms|s)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RetryDelayRegex = new("\\\"retryDelay\\\"\\s*:\\s*\\\"([0-9.]+)(ms|s)\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

    public static TimeSpan? ExtractRetryDelay(string errorText, HttpResponseMessage? response = null, DateTimeOffset? now = null)
    {
        static TimeSpan? Normalize(double milliseconds)
        {
            return milliseconds > 0
                ? TimeSpan.FromMilliseconds(Math.Ceiling(milliseconds + 1000))
                : null;
        }

        if (response is not null)
        {
            if (TryGetHeader(response, "Retry-After", out var retryAfter))
            {
                if (double.TryParse(retryAfter, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                {
                    var delay = Normalize(seconds * 1000);
                    if (delay.HasValue)
                    {
                        return delay.Value;
                    }
                }

                if (DateTimeOffset.TryParse(retryAfter, out var retryAfterDate))
                {
                    var delay = Normalize((retryAfterDate - (now ?? DateTimeOffset.UtcNow)).TotalMilliseconds);
                    if (delay.HasValue)
                    {
                        return delay.Value;
                    }
                }
            }

            if (TryGetHeader(response, "x-ratelimit-reset", out var reset) &&
                long.TryParse(reset, out var resetSeconds))
            {
                var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds);
                var delay = Normalize((resetAt - (now ?? DateTimeOffset.UtcNow)).TotalMilliseconds);
                if (delay.HasValue)
                {
                    return delay.Value;
                }
            }

            if (TryGetHeader(response, "x-ratelimit-reset-after", out var resetAfter) &&
                double.TryParse(resetAfter, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var resetAfterSeconds))
            {
                var delay = Normalize(resetAfterSeconds * 1000);
                if (delay.HasValue)
                {
                    return delay.Value;
                }
            }
        }

        var resetMatch = ResetAfterRegex.Match(errorText);
        if (resetMatch.Success)
        {
            var hours = resetMatch.Groups[1].Success ? int.Parse(resetMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var minutes = resetMatch.Groups[2].Success ? int.Parse(resetMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var secs = double.Parse(resetMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            var delay = Normalize(((hours * 60 + minutes) * 60 + secs) * 1000);
            if (delay.HasValue)
            {
                return delay.Value;
            }
        }

        var retryInMatch = RetryInRegex.Match(errorText);
        if (retryInMatch.Success)
        {
            var value = double.Parse(retryInMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var ms = retryInMatch.Groups[2].Value.Equals("ms", StringComparison.OrdinalIgnoreCase) ? value : value * 1000;
            var delay = Normalize(ms);
            if (delay.HasValue)
            {
                return delay.Value;
            }
        }

        var retryDelayMatch = RetryDelayRegex.Match(errorText);
        if (retryDelayMatch.Success)
        {
            var value = double.Parse(retryDelayMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var ms = retryDelayMatch.Groups[2].Value.Equals("ms", StringComparison.OrdinalIgnoreCase) ? value : value * 1000;
            var delay = Normalize(ms);
            if (delay.HasValue)
            {
                return delay.Value;
            }
        }

        return null;
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

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            stream.Push(new ErrorEvent("Google Cloud Code Assist requires OAuth credentials. Import them into auth.json or provide an explicit apiKey payload."));
            return;
        }

        var credentials = ParseCredentials(options.ApiKey);
        var projectId = options is GoogleGeminiCliOptions { ProjectId: { Length: > 0 } configuredProjectId }
            ? configuredProjectId
            : credentials.ProjectId;
        var isAntigravity = model.Provider.Equals("google-antigravity", StringComparison.OrdinalIgnoreCase);
        var endpoints = ResolveEndpoints(model, isAntigravity);
        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(model, context, projectId, options, reasoning, isAntigravity)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, GoogleGeminiCliRequestJsonContext.Default.DictionaryStringObject);

        using var responseWithEndpoint = await SendWithRetryAsync(
            model,
            options,
            credentials.Token,
            endpoints,
            json,
            isAntigravity,
            options.Signal).ConfigureAwait(false);
        var initial = new AssistantMessage
        {
            Api = Api,
            Provider = model.Provider,
            Model = model.Id,
            Content = []
        };

        for (var emptyAttempt = 0; emptyAttempt <= MaxEmptyStreamRetries; emptyAttempt++)
        {
            var activeResponse = emptyAttempt == 0
                ? responseWithEndpoint.Response
                : await SendSingleAsync(
                    model,
                    options,
                    credentials.Token,
                    responseWithEndpoint.Endpoint,
                    json,
                    isAntigravity,
                    options.Signal).ConfigureAwait(false);

            await StreamOptionHelpers.InvokeResponseCallbackAsync(options, model, activeResponse).ConfigureAwait(false);
            var emitted = await ParseResponseAsync(activeResponse, stream, initial, options.Signal).ConfigureAwait(false);
            if (activeResponse != responseWithEndpoint.Response)
            {
                activeResponse.Dispose();
            }

            if (emitted)
            {
                return;
            }
        }

        stream.Push(new ErrorEvent("Cloud Code Assist API returned an empty response."));
    }

    private async Task<GeminiCliResponse> SendWithRetryAsync(
        Model model,
        StreamOptions options,
        string token,
        IReadOnlyList<string> endpoints,
        string json,
        bool isAntigravity,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        string? lastError = null;
        var endpointIndex = 0;
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            response = await SendSingleAsync(
                model,
                options,
                token,
                endpoints[endpointIndex],
                json,
                isAntigravity,
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new GeminiCliResponse(response, endpoints[endpointIndex]);
            }

            lastError = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if ((response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.NotFound) && endpointIndex < endpoints.Count - 1)
            {
                response.Dispose();
                endpointIndex++;
                continue;
            }

            if (attempt == MaxRetries || !IsRetryableError(response.StatusCode, lastError))
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new InvalidOperationException($"Cloud Code Assist API error ({status}): {ExtractErrorMessage(lastError)}");
            }

            if (endpointIndex < endpoints.Count - 1)
            {
                endpointIndex++;
            }

            var serverDelay = ExtractRetryDelay(lastError, response);
            response.Dispose();
            var delay = serverDelay ?? TimeSpan.FromMilliseconds(25 * Math.Pow(2, attempt));
            var maxDelay = options.MaxRetryDelay ?? TimeSpan.FromSeconds(60);
            if (serverDelay.HasValue && maxDelay > TimeSpan.Zero && serverDelay.Value > maxDelay)
            {
                throw new InvalidOperationException($"Server requested {Math.Ceiling(serverDelay.Value.TotalSeconds)}s retry delay (max: {Math.Ceiling(maxDelay.TotalSeconds)}s). {ExtractErrorMessage(lastError)}");
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(lastError ?? "Cloud Code Assist request failed.");
    }

    private async Task<HttpResponseMessage> SendSingleAsync(
        Model model,
        StreamOptions options,
        string token,
        string endpoint,
        string json,
        bool isAntigravity,
        CancellationToken cancellationToken)
    {
        var url = $"{endpoint.TrimEnd('/')}/v1internal:streamGenerateContent?alt=sse";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyDefaultHeaders(request, isAntigravity);
        if (NeedsClaudeThinkingBetaHeader(model))
        {
            request.Headers.TryAddWithoutValidation("anthropic-beta", ClaudeThinkingBetaHeader);
        }

        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);

        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ParseResponseAsync(
        HttpResponseMessage response,
        AssistantMessageStream stream,
        AssistantMessage initial,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            stream.Push(new ErrorEvent($"Google Gemini CLI API error {(int)response.StatusCode}: {errorBody}"));
            return true;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var parser = new GoogleStreamParser(initial, stream);
        var hasContent = false;

        await foreach (var sse in SseParser.ParseAsync(responseStream, cancellationToken))
        {
            if (string.IsNullOrEmpty(sse.Data))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(sse.Data);
            if (doc.RootElement.TryGetProperty("response", out var responseElement))
            {
                if (!hasContent)
                {
                    parser.EmitStart();
                    hasContent = true;
                }

                parser.ParseChunk(responseElement.GetRawText());
            }
        }

        if (!hasContent)
        {
            return false;
        }

        parser.EmitDone();
        return true;
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
        ThinkingLevel? reasoning,
        bool isAntigravity)
    {
        var request = new Dictionary<string, object>
        {
            ["contents"] = GoogleMessageConverter.ConvertMessages(context.Messages)
        };

        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            request["sessionId"] = options.SessionId!;
        }

        var systemParts = new List<object>();
        if (isAntigravity)
        {
            systemParts.Add(new Dictionary<string, object> { ["text"] = AntigravitySystemInstruction });
            systemParts.Add(new Dictionary<string, object> { ["text"] = $"Please ignore following [ignore]{AntigravitySystemInstruction}[/ignore]" });
        }

        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            systemParts.Add(new Dictionary<string, object> { ["text"] = UnicodeTextSanitizer.RemoveUnpairedSurrogates(context.SystemPrompt!) });
        }

        if (systemParts.Count > 0)
        {
            var systemInstruction = new Dictionary<string, object> { ["parts"] = systemParts };
            if (isAntigravity)
            {
                systemInstruction["role"] = "user";
            }

            request["systemInstruction"] = systemInstruction;
        }

        if (context.Tools is { Count: > 0 })
        {
            request["tools"] = GoogleMessageConverter.ConvertTools(context.Tools);
            if (options is GoogleGeminiCliOptions { ToolChoice: { Length: > 0 } toolChoice })
            {
                request["toolConfig"] = BuildToolConfig(toolChoice);
            }
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
        if (options is GoogleGeminiCliOptions { Thinking: { } thinking } && model.Reasoning)
        {
            generationConfig["thinkingConfig"] = BuildThinkingConfig(model.Id, thinking);
        }
        else if (reasoning.HasValue && model.Reasoning)
        {
            generationConfig["thinkingConfig"] = BuildThinkingConfig(
                model.Id,
                reasoning.Value,
                (options as SimpleStreamOptions)?.ThinkingBudgets);
        }
        if (generationConfig.Count > 0)
        {
            request["generationConfig"] = generationConfig;
        }

        var body = new Dictionary<string, object>
        {
            ["project"] = projectId,
            ["model"] = model.Id,
            ["request"] = request,
            ["userAgent"] = isAntigravity ? "antigravity" : "tau-coding-agent",
            ["requestId"] = $"{(isAntigravity ? "agent" : "tau")}-{Guid.NewGuid():N}"
        };

        if (isAntigravity)
        {
            body["requestType"] = "agent";
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

    private static Dictionary<string, object> BuildThinkingConfig(string modelId, GoogleThinkingOptions thinking)
    {
        if (!thinking.Enabled)
        {
            return GetDisabledThinkingConfig(modelId);
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

    private static Dictionary<string, object> GetDisabledThinkingConfig(string modelId)
    {
        if (IsGemini3ProModel(modelId))
        {
            return new Dictionary<string, object> { ["thinkingLevel"] = "LOW" };
        }

        if (IsGemini3FlashModel(modelId))
        {
            return new Dictionary<string, object> { ["thinkingLevel"] = "MINIMAL" };
        }

        return new Dictionary<string, object> { ["thinkingBudget"] = 0 };
    }

    private static Dictionary<string, object> BuildThinkingConfig(string modelId, ThinkingLevel reasoning, ThinkingBudgets? budgets = null)
    {
        if (IsGemini3Model(modelId))
        {
            return new Dictionary<string, object>
            {
            ["includeThoughts"] = true,
            ["thinkingLevel"] = GetGemini3ThinkingLevel(modelId, reasoning)
            };
        }

        return new Dictionary<string, object>
        {
            ["includeThoughts"] = true,
            ["thinkingBudget"] = StreamOptionHelpers.GetThinkingBudget(
                budgets,
                reasoning,
                defaultMinimal: 1_024,
                defaultLow: 2_048,
                defaultMedium: 8_192,
                defaultHigh: 16_384)
        };
    }

    private static string GetGemini3ThinkingLevel(string modelId, ThinkingLevel reasoning)
    {
        if (IsGemini3ProModel(modelId))
        {
            return reasoning is ThinkingLevel.Minimal or ThinkingLevel.Low ? "LOW" : "HIGH";
        }

        return reasoning switch
        {
            ThinkingLevel.Minimal => "MINIMAL",
            ThinkingLevel.Low => "LOW",
            ThinkingLevel.Medium => "MEDIUM",
            ThinkingLevel.High or ThinkingLevel.ExtraHigh => "HIGH",
            _ => "LOW"
        };
    }

    private static IReadOnlyList<string> ResolveEndpoints(Model model, bool isAntigravity)
    {
        if (!string.IsNullOrWhiteSpace(model.BaseUrl))
        {
            return [model.BaseUrl!.TrimEnd('/')];
        }

        return isAntigravity
            ? [AntigravityDailyEndpoint, AntigravityAutopushEndpoint, DefaultEndpoint]
            : [DefaultEndpoint];
    }

    private static void ApplyDefaultHeaders(HttpRequestMessage request, bool isAntigravity)
    {
        if (isAntigravity)
        {
            var version = Environment.GetEnvironmentVariable("PI_AI_ANTIGRAVITY_VERSION");
            request.Headers.TryAddWithoutValidation("User-Agent", $"antigravity/{(string.IsNullOrWhiteSpace(version) ? DefaultAntigravityVersion : version)} darwin/arm64");
            return;
        }

        request.Headers.TryAddWithoutValidation("User-Agent", "google-cloud-sdk vscode_cloudshelleditor/0.1");
        request.Headers.TryAddWithoutValidation("X-Goog-Api-Client", "gl-dotnet/tau");
        request.Headers.TryAddWithoutValidation("Client-Metadata", "{\"ideType\":\"IDE_UNSPECIFIED\",\"platform\":\"PLATFORM_UNSPECIFIED\",\"pluginType\":\"GEMINI\"}");
    }

    private static bool NeedsClaudeThinkingBetaHeader(Model model) =>
        model.Provider.Equals("google-antigravity", StringComparison.OrdinalIgnoreCase) &&
        model.Id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) &&
        model.Reasoning;

    private static bool IsGemini3Model(string modelId) => IsGemini3ProModel(modelId) || IsGemini3FlashModel(modelId);

    private static bool IsGemini3ProModel(string modelId) =>
        modelId.Contains("gemini-3", StringComparison.OrdinalIgnoreCase) &&
        modelId.Contains("pro", StringComparison.OrdinalIgnoreCase);

    private static bool IsGemini3FlashModel(string modelId) =>
        modelId.Contains("gemini-3", StringComparison.OrdinalIgnoreCase) &&
        modelId.Contains("flash", StringComparison.OrdinalIgnoreCase);

    private static bool IsRetryableError(HttpStatusCode statusCode, string errorText) =>
        statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ||
        Regex.IsMatch(errorText, "resource.?exhausted|rate.?limit|overloaded|service.?unavailable|other.?side.?closed", RegexOptions.IgnoreCase);

    private static string ExtractErrorMessage(string errorText)
    {
        try
        {
            using var document = JsonDocument.Parse(errorText);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? errorText;
            }
        }
        catch (JsonException)
        {
            // keep raw body
        }

        return errorText;
    }

    private static bool TryGetHeader(HttpResponseMessage response, string name, out string value)
    {
        if (response.Headers.TryGetValues(name, out var headerValues) || response.Content.Headers.TryGetValues(name, out headerValues))
        {
            value = headerValues.FirstOrDefault() ?? string.Empty;
            return !string.IsNullOrEmpty(value);
        }

        value = string.Empty;
        return false;
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

internal sealed class GeminiCliResponse : IDisposable
{
    public GeminiCliResponse(HttpResponseMessage response, string endpoint)
    {
        Response = response;
        Endpoint = endpoint;
    }

    public HttpResponseMessage Response { get; }
    public string Endpoint { get; }

    public void Dispose() => Response.Dispose();
}

public record GoogleGeminiCliOptions : StreamOptions
{
    public string? ToolChoice { get; init; }
    public GoogleThinkingOptions? Thinking { get; init; }
    public string? ProjectId { get; init; }
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
