using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tau.Ai.Streaming;
using Tau.Ai.Utilities;

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
        _httpClient = httpClient ?? TauHttpClientFactory.Create();
    }

    public string Api => "google-gemini-cli";

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
        GoogleStreamParser parser,
        ThinkingLevel? reasoning)
    {
        options.Signal.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            parser.PushError("Google Cloud Code Assist requires OAuth credentials. Import them into auth.json or provide an explicit apiKey payload.");
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

        using var requestTimeout = StreamOptionHelpers.CreateRequestTimeout(options);
        try
        {
            using var responseWithEndpoint = await SendWithRetryAsync(
                model,
                options,
                credentials.Token,
                endpoints,
                json,
                isAntigravity,
                requestTimeout.Token).ConfigureAwait(false);
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
                        requestTimeout.Token).ConfigureAwait(false);

                await StreamOptionHelpers.InvokeResponseCallbackAsync(options, model, activeResponse).ConfigureAwait(false);
                var emitted = await ParseResponseAsync(activeResponse, parser, requestTimeout.Token, options, model).ConfigureAwait(false);
                if (activeResponse != responseWithEndpoint.Response)
                {
                    activeResponse.Dispose();
                }

                if (emitted)
                {
                    return;
                }
            }

            parser.PushError("Cloud Code Assist API returned an empty response.");
        }
        catch (OperationCanceledException ex) when (requestTimeout.IsTimeoutCancellation)
        {
            throw requestTimeout.CreateTimeoutException(ex);
        }
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
        ApplyDefaultHeaders(request, isAntigravity, options.Env);
        if (NeedsClaudeThinkingBetaHeader(model))
        {
            request.Headers.TryAddWithoutValidation("anthropic-beta", ClaudeThinkingBetaHeader);
        }

        ProviderHttpHeaders.Apply(request, model.Headers);
        ProviderHttpHeaders.Apply(request, options.Headers);

        await StreamOptionHelpers.ApplyHeadersCallbackAsync(options, model, request).ConfigureAwait(false);
        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ParseResponseAsync(
        HttpResponseMessage response,
        GoogleStreamParser parser,
        CancellationToken cancellationToken, StreamOptions options, Model model)
    {
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            parser.PushError($"Google Gemini CLI API error {(int)response.StatusCode}: {errorBody}");
            return true;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var hasContent = false;

        await foreach (var sse in SseParser.ParseAsync(responseStream, cancellationToken))
        {
            if (string.IsNullOrEmpty(sse.Data))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(sse.Data);
            if (options.OnProviderStreamEvent is not null) await options.OnProviderStreamEvent(doc.RootElement.Clone(), model).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (doc.RootElement.TryGetProperty("response", out var responseElement))
            {
                if (!hasContent)
                {
                    parser.EmitStart();
                    hasContent = true;
                }

                if (parser.ParseChunk(responseElement.GetRawText()))
                {
                    return true;
                }
            }
        }

        if (!hasContent)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
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
        context = Transcript.ResolveContext(context);
        context = MessageTransformer.DowngradeUnsupportedImages(context, model);
        var request = new Dictionary<string, object>
        {
            ["contents"] = GoogleMessageConverter.ConvertMessages(model, context.Messages)
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

        GoogleMessageConverter.ApplyTools(request, model, context.Tools, options,
            useParameters: model.Id.StartsWith("claude-", StringComparison.Ordinal));

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
            generationConfig["thinkingConfig"] = GoogleThinking.BuildConfig(model, thinking);
        }
        else if (options is SimpleStreamOptions simple && model.Reasoning)
        {
            generationConfig["thinkingConfig"] = GoogleThinking.BuildConfig(model, GoogleThinking.ResolveSimple(model, reasoning, simple.ThinkingBudgets));
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

    private static void ApplyDefaultHeaders(
        HttpRequestMessage request,
        bool isAntigravity,
        IReadOnlyDictionary<string, string>? env)
    {
        if (isAntigravity)
        {
            var version = ProviderEnvironment.GetValue("PI_AI_ANTIGRAVITY_VERSION", env);
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
