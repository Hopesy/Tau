using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.OpenAiResponses;

public sealed class AzureOpenAiResponsesProvider : IStreamProvider
{
    private const string DefaultAzureApiVersion = "v1";
    private readonly HttpClient _httpClient;

    public AzureOpenAiResponsesProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public string Api => "azure-openai-responses";

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();
        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream).ConfigureAwait(false);
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
        var reasoningEffort = OpenAiResponsesShared.MapReasoningEffort(options.Reasoning, model);
        var azureOptions = new AzureOpenAiResponsesOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens ?? model.MaxOutputTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            Signal = options.Signal,
            OnResponse = options.OnResponse,
            OnPayload = options.OnPayload,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            Timeout = options.Timeout,
            MaxRetryDelay = options.MaxRetryDelay,
            MaxRetries = options.MaxRetries,
            WebSocketConnectTimeout = options.WebSocketConnectTimeout,
            Metadata = options.Metadata,
            Env = options.Env,
            ReasoningEffort = reasoningEffort
        };
        return Stream(model, context, azureOptions);
    }

    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        StreamOptions options,
        AssistantMessageStream stream)
    {
        if (StreamOptionHelpers.PushAbortedIfCanceled(options, stream, model, Api))
        {
            return;
        }

        var azureOptions = options as AzureOpenAiResponsesOptions;
        var deploymentName = ResolveDeploymentName(model, azureOptions);
        var config = ResolveAzureConfig(model, azureOptions);
        var url = $"{config.BaseUrl}/responses?api-version={Uri.EscapeDataString(config.ApiVersion)}";
        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(model, context, options, deploymentName)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, OpenAiResponsesJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyAuthHeader(request, ResolveApiKey(azureOptions));
        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

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

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);
            await OpenAiResponsesShared.ProcessResponsesStreamAsync(
                responseStream,
                partial,
                stream,
                cancellationToken: requestTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (requestTimeout.IsTimeoutCancellation)
        {
            throw requestTimeout.CreateTimeoutException(ex);
        }
    }

    private static Dictionary<string, object> BuildRequestBody(
        Model model,
        LlmContext context,
        StreamOptions options,
        string deploymentName)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = deploymentName,
            ["input"] = OpenAiResponsesShared.ConvertResponsesMessages(model, context),
            ["stream"] = true
        };

        if (options.MaxTokens.HasValue)
        {
            body["max_output_tokens"] = options.MaxTokens.Value;
        }

        if (options.Temperature.HasValue)
        {
            body["temperature"] = options.Temperature.Value;
        }

        if (options.TopP.HasValue)
        {
            body["top_p"] = options.TopP.Value;
        }

        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            body["prompt_cache_key"] = options.SessionId!;
        }

        var tools = OpenAiResponsesShared.ConvertResponsesTools(context.Tools);
        if (tools.Count > 0)
        {
            body["tools"] = tools;
        }

        if (model.Reasoning)
        {
            AddReasoning(body, options as AzureOpenAiResponsesOptions);
        }

        return body;
    }

    private static void AddReasoning(Dictionary<string, object> body, AzureOpenAiResponsesOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.ReasoningEffort) ||
            !string.IsNullOrWhiteSpace(options?.ReasoningSummary))
        {
            body["reasoning"] = new Dictionary<string, object>
            {
                ["effort"] = string.IsNullOrWhiteSpace(options?.ReasoningEffort) ? "medium" : options!.ReasoningEffort!,
                ["summary"] = string.IsNullOrWhiteSpace(options?.ReasoningSummary) ? "auto" : options!.ReasoningSummary!
            };
            body["include"] = new List<object> { "reasoning.encrypted_content" };
            return;
        }

        body["reasoning"] = new Dictionary<string, object> { ["effort"] = "none" };
    }

    private static AzureConfig ResolveAzureConfig(Model model, AzureOpenAiResponsesOptions? options)
    {
        var apiVersion = FirstNonEmpty(
            options?.AzureApiVersion,
            ProviderEnvironment.GetValue("AZURE_OPENAI_API_VERSION", options?.Env),
            DefaultAzureApiVersion)!;

        var baseUrl = FirstNonEmpty(
            options?.AzureBaseUrl,
            ProviderEnvironment.GetValue("AZURE_OPENAI_BASE_URL", options?.Env));
        var resourceName = FirstNonEmpty(
            options?.AzureResourceName,
            ProviderEnvironment.GetValue("AZURE_OPENAI_RESOURCE_NAME", options?.Env));

        if (string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(resourceName))
        {
            baseUrl = $"https://{resourceName}.openai.azure.com/openai/v1";
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = model.BaseUrl;
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "Azure OpenAI base URL is required. Set AZURE_OPENAI_BASE_URL or AZURE_OPENAI_RESOURCE_NAME, or pass AzureBaseUrl, AzureResourceName, or model.BaseUrl.");
        }

        return new AzureConfig(baseUrl.TrimEnd('/'), apiVersion);
    }

    private static string ResolveDeploymentName(Model model, AzureOpenAiResponsesOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.AzureDeploymentName))
        {
            return options.AzureDeploymentName!;
        }

        var map = ParseDeploymentNameMap(ProviderEnvironment.GetValue("AZURE_OPENAI_DEPLOYMENT_NAME_MAP", options?.Env));
        return map.TryGetValue(model.Id, out var deploymentName) ? deploymentName : model.Id;
    }

    private static Dictionary<string, string> ParseDeploymentNameMap(string? value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return map;
        }

        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                string.IsNullOrWhiteSpace(parts[0]) ||
                string.IsNullOrWhiteSpace(parts[1]))
            {
                continue;
            }

            map[parts[0]] = parts[1];
        }

        return map;
    }

    private static string? ResolveApiKey(AzureOpenAiResponsesOptions? options) =>
        string.IsNullOrWhiteSpace(options?.ApiKey)
            ? ProviderEnvironment.GetValue("AZURE_OPENAI_API_KEY", options?.Env)
            : options.ApiKey;

    private static void ApplyAuthHeader(HttpRequestMessage request, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey))
        {
            throw new InvalidOperationException(
                "Azure OpenAI API key is required. Set AZURE_OPENAI_API_KEY environment variable or pass it as an argument.");
        }

        request.Headers.TryAddWithoutValidation("api-key", apiKey);
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

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private sealed record AzureConfig(string BaseUrl, string ApiVersion);
}
