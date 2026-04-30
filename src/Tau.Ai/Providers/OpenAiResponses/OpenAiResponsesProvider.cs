using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.OpenAiResponses;

public sealed class OpenAiResponsesProvider : IStreamProvider
{
    private readonly HttpClient _httpClient;

    public OpenAiResponsesProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public string Api => "openai-responses";

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
        var reasoningEffort = OpenAiResponsesShared.MapReasoningEffort(options.Reasoning, model);
        var responseOptions = new OpenAiResponsesOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens ?? model.MaxOutputTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            MaxRetryDelay = options.MaxRetryDelay,
            Metadata = options.Metadata,
            ReasoningEffort = reasoningEffort
        };
        return Stream(model, context, responseOptions);
    }

    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        StreamOptions options,
        AssistantMessageStream stream)
    {
        var baseUrl = model.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
        var url = $"{baseUrl}/responses";
        var body = BuildRequestBody(model, context, options);
        var json = JsonSerializer.Serialize(body, OpenAiResponsesJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyAuthHeader(request, options.ApiKey);
        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, ResolveDynamicHeaders(model, context));
        ApplyHeaders(request, options.Headers);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            stream.Push(new ErrorEvent($"{Api} error {(int)response.StatusCode}: {errorBody}"));
            return;
        }

        var partial = new AssistantMessage
        {
            Api = Api,
            Provider = model.Provider,
            Model = model.Id,
            Content = []
        };
        stream.Push(new StartEvent(partial));

        await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await OpenAiResponsesShared.ProcessResponsesStreamAsync(
            responseStream,
            partial,
            stream,
            requestedServiceTier: (options as OpenAiResponsesOptions)?.ServiceTier).ConfigureAwait(false);
    }

    private static Dictionary<string, object> BuildRequestBody(Model model, LlmContext context, StreamOptions options)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model.Id,
            ["input"] = OpenAiResponsesShared.ConvertResponsesMessages(model, context),
            ["stream"] = true,
            ["store"] = false
        };

        var tools = OpenAiResponsesShared.ConvertResponsesTools(context.Tools);
        if (tools.Count > 0)
        {
            body["tools"] = tools;
            body["tool_choice"] = "auto";
            body["parallel_tool_calls"] = true;
        }

        OpenAiResponsesShared.AddBaseParameters(body, model, options);
        if (options is OpenAiResponsesOptions responseOptions)
        {
            AddResponsesOptions(body, responseOptions);
        }

        return body;
    }

    private static void AddResponsesOptions(Dictionary<string, object> body, OpenAiResponsesOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort) ||
            !string.IsNullOrWhiteSpace(options.ReasoningSummary))
        {
            var reasoning = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
            {
                reasoning["effort"] = options.ReasoningEffort!;
            }

            if (!string.IsNullOrWhiteSpace(options.ReasoningSummary))
            {
                reasoning["summary"] = options.ReasoningSummary!;
            }

            body["reasoning"] = reasoning;
        }

        if (!string.IsNullOrWhiteSpace(options.ServiceTier))
        {
            body["service_tier"] = options.ServiceTier!;
        }
    }

    private static void ApplyAuthHeader(HttpRequestMessage request, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey))
        {
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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

    private static IDictionary<string, string>? ResolveDynamicHeaders(Model model, LlmContext context)
    {
        return string.Equals(model.Provider, "github-copilot", StringComparison.OrdinalIgnoreCase)
            ? GitHubCopilotHeaders.BuildDynamicHeaders(context.Messages)
            : null;
    }
}
