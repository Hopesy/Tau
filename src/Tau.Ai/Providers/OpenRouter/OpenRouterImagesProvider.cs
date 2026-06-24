using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai.Auth;

namespace Tau.Ai.Providers.OpenRouter;

public sealed class OpenRouterImagesProvider : IImagesProvider
{
    private readonly HttpClient _httpClient;

    public OpenRouterImagesProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public string Api => "openrouter-images";

    public async Task<AssistantImages> GenerateImagesAsync(
        ImagesModel model,
        ImagesContext context,
        ImagesOptions options)
    {
        if (options.Signal.IsCancellationRequested)
        {
            return CreateError(model, ImagesStopReason.Aborted, "Request was aborted");
        }

        var output = new AssistantImages
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Output = []
        };

        try
        {
            if (string.IsNullOrWhiteSpace(options.ApiKey) ||
                EnvironmentApiKeyResolver.IsAuthenticatedMarker(options.ApiKey))
            {
                throw new InvalidOperationException($"No API key for provider: {model.Provider}");
            }

            var payload = await ApplyPayloadCallbackAsync(
                options,
                model,
                BuildRequestBody(model, context)).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(payload, OpenRouterImagesJsonContext.Default.DictionaryStringObject);
            var baseUrl = model.BaseUrl?.TrimEnd('/') ?? "https://openrouter.ai/api/v1";
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            ApplyHeaders(request, model.Headers);
            ApplyHeaders(request, options.Headers);

            using var requestTimeout = new ImagesRequestTimeout(options);
            try
            {
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    requestTimeout.Token).ConfigureAwait(false);
                await InvokeResponseCallbackAsync(options, model, response).ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"openrouter-images error {(int)response.StatusCode}: {body}");
                }

                return ParseResponse(output, body, model);
            }
            catch (OperationCanceledException ex) when (requestTimeout.IsTimeoutCancellation)
            {
                throw requestTimeout.CreateTimeoutException(ex);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return output with
            {
                StopReason = options.Signal.IsCancellationRequested ? ImagesStopReason.Aborted : ImagesStopReason.Error,
                ErrorMessage = ex.Message,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
    }

    private static AssistantImages CreateError(ImagesModel model, ImagesStopReason reason, string message) => new()
    {
        Api = model.Api,
        Provider = model.Provider,
        Model = model.Id,
        Output = [],
        StopReason = reason,
        ErrorMessage = message,
        Timestamp = DateTimeOffset.UtcNow
    };

    private static Dictionary<string, object> BuildRequestBody(ImagesModel model, ImagesContext context)
    {
        var content = new List<object>();
        foreach (var item in context.Input)
        {
            switch (item)
            {
                case TextContent text:
                    content.Add(new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = UnicodeTextSanitizer.RemoveUnpairedSurrogates(text.Text)
                    });
                    break;
                case ImageContent image:
                    content.Add(new Dictionary<string, object>
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new Dictionary<string, object>
                        {
                            ["url"] = $"data:{image.MimeType};base64,{image.Data}"
                        }
                    });
                    break;
            }
        }

        return new Dictionary<string, object>
        {
            ["model"] = model.Id,
            ["messages"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = content
                }
            },
            ["stream"] = false,
            ["modalities"] = model.OutputModalities.Contains("text", StringComparer.OrdinalIgnoreCase)
                ? new[] { "image", "text" }
                : new[] { "image" }
        };
    }

    private static AssistantImages ParseResponse(AssistantImages output, string body, ImagesModel model)
    {
        var parsed = JsonSerializer.Deserialize(body, OpenRouterImagesJsonContext.Default.OpenRouterImagesResponse);
        if (parsed is null)
        {
            return output with
            {
                StopReason = ImagesStopReason.Error,
                ErrorMessage = "openrouter-images response was empty.",
                Timestamp = DateTimeOffset.UtcNow
            };
        }

        var content = new List<ContentBlock>();
        var choice = parsed.Choices?.FirstOrDefault();
        if (!string.IsNullOrEmpty(choice?.Message?.Content))
        {
            content.Add(new TextContent(choice.Message.Content!));
        }

        foreach (var image in choice?.Message?.Images ?? [])
        {
            var url = image.ImageUrl?.ResolveUrl();
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var comma = url.IndexOf(',');
            var prefix = comma >= 0 ? url[..comma] : string.Empty;
            const string marker = "data:";
            const string suffix = ";base64";
            if (comma < 0 ||
                !prefix.StartsWith(marker, StringComparison.OrdinalIgnoreCase) ||
                !prefix.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var mimeType = prefix[marker.Length..^suffix.Length];
            var data = url[(comma + 1)..];
            if (!string.IsNullOrWhiteSpace(mimeType) && !string.IsNullOrWhiteSpace(data))
            {
                content.Add(new ImageContent(data, mimeType));
            }
        }

        return output with
        {
            ResponseId = parsed.Id,
            Output = content,
            Usage = parsed.Usage is null ? null : ParseUsage(parsed.Usage, model),
            StopReason = ImagesStopReason.Stop,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    private static Usage ParseUsage(OpenRouterImagesUsage rawUsage, ImagesModel model)
    {
        var promptTokens = rawUsage.PromptTokens ?? 0;
        var reportedCachedTokens = rawUsage.PromptTokensDetails?.CachedTokens ?? 0;
        var cacheWriteTokens = rawUsage.PromptTokensDetails?.CacheWriteTokens ?? 0;
        var cacheReadTokens = cacheWriteTokens > 0
            ? Math.Max(0, reportedCachedTokens - cacheWriteTokens)
            : reportedCachedTokens;
        var input = Math.Max(0, promptTokens - cacheReadTokens - cacheWriteTokens);
        var output = rawUsage.CompletionTokens ?? 0;
        UsageCost? cost = null;
        if (model.Cost is { } modelCost)
        {
            cost = new UsageCost(
                (modelCost.InputPerMillion / 1_000_000m) * input,
                (modelCost.OutputPerMillion / 1_000_000m) * output,
                ((modelCost.CacheReadPerMillion ?? 0m) / 1_000_000m) * cacheReadTokens,
                ((modelCost.CacheWritePerMillion ?? 0m) / 1_000_000m) * cacheWriteTokens);
        }

        return new Usage(input, output, cacheReadTokens, cacheWriteTokens, Cost: cost);
    }

    private static async ValueTask<Dictionary<string, object>> ApplyPayloadCallbackAsync(
        ImagesOptions options,
        ImagesModel model,
        Dictionary<string, object> payload)
    {
        if (options.OnPayload is null)
        {
            return payload;
        }

        var replacement = await options.OnPayload(payload, model).ConfigureAwait(false);
        return replacement switch
        {
            null => payload,
            Dictionary<string, object> dictionary => dictionary,
            IDictionary<string, object> dictionary => new Dictionary<string, object>(dictionary, StringComparer.Ordinal),
            _ => throw new InvalidOperationException(
                "ImagesOptions.OnPayload must return null or an object dictionary compatible with provider request-body serialization.")
        };
    }

    private static async ValueTask InvokeResponseCallbackAsync(
        ImagesOptions options,
        ImagesModel model,
        HttpResponseMessage response)
    {
        if (options.OnResponse is null)
        {
            return;
        }

        await options.OnResponse(
            new ProviderResponse((int)response.StatusCode, AiHeaderUtilities.ToDictionary(response)),
            model).ConfigureAwait(false);
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

    private sealed class ImagesRequestTimeout : IDisposable
    {
        private readonly CancellationToken _signal;
        private readonly CancellationTokenSource? _timeoutSource;
        private readonly CombinedCancellationToken _combined;
        private readonly TimeSpan _timeout;

        public ImagesRequestTimeout(ImagesOptions options)
        {
            _signal = options.Signal;
            _timeout = options.Timeout ?? TimeSpan.Zero;
            if (_timeout <= TimeSpan.Zero)
            {
                _combined = CancellationTokenUtilities.Combine(_signal);
                return;
            }

            _timeoutSource = new CancellationTokenSource(_timeout);
            _combined = CancellationTokenUtilities.Combine(_signal, _timeoutSource.Token);
        }

        public CancellationToken Token => _combined.Token;

        public bool IsTimeoutCancellation =>
            _timeoutSource?.IsCancellationRequested == true && !_signal.IsCancellationRequested;

        public TimeoutException CreateTimeoutException(OperationCanceledException cause) =>
            new($"Request timed out after {(int)_timeout.TotalMilliseconds}ms", cause);

        public void Dispose()
        {
            _combined.Dispose();
            _timeoutSource?.Dispose();
        }
    }
}

internal sealed record OpenRouterImagesResponse(
    string? Id,
    IReadOnlyList<OpenRouterImagesChoice>? Choices,
    OpenRouterImagesUsage? Usage);

internal sealed record OpenRouterImagesChoice(OpenRouterImagesMessage? Message);

internal sealed record OpenRouterImagesMessage(
    string? Content,
    IReadOnlyList<OpenRouterGeneratedImage>? Images);

internal sealed record OpenRouterGeneratedImage(
    [property: JsonConverter(typeof(OpenRouterImageUrlConverter))]
    OpenRouterImageUrl? ImageUrl);

internal sealed record OpenRouterImageUrl(string? Url)
{
    public string? ResolveUrl() => Url;
}

internal sealed record OpenRouterImagesUsage(
    int? PromptTokens,
    int? CompletionTokens,
    OpenRouterImagesPromptTokensDetails? PromptTokensDetails);

internal sealed record OpenRouterImagesPromptTokensDetails(
    int? CachedTokens,
    int? CacheWriteTokens);

internal sealed class OpenRouterImageUrlConverter : JsonConverter<OpenRouterImageUrl?>
{
    public override OpenRouterImageUrl? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new OpenRouterImageUrl(reader.GetString());
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        string? url = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                reader.Skip();
                continue;
            }

            var property = reader.GetString();
            reader.Read();
            if (string.Equals(property, "url", StringComparison.OrdinalIgnoreCase) &&
                reader.TokenType == JsonTokenType.String)
            {
                url = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        return new OpenRouterImageUrl(url);
    }

    public override void Write(
        Utf8JsonWriter writer,
        OpenRouterImageUrl? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("url", value.Url);
        writer.WriteEndObject();
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(OpenRouterImagesResponse))]
internal partial class OpenRouterImagesJsonContext : JsonSerializerContext;
