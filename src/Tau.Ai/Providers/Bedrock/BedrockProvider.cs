using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai.Auth;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.Bedrock;

public sealed class BedrockProvider : IStreamProvider
{
    private readonly HttpClient _httpClient;
    private readonly Func<DateTimeOffset> _clock;

    public BedrockProvider(HttpClient? httpClient = null, Func<DateTimeOffset>? clock = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Api => "bedrock-converse-stream";

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();
        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, ToBedrockOptions(options), stream).ConfigureAwait(false);
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
        var bedrockOptions = new BedrockOptions
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
            Reasoning = model.Reasoning ? options.Reasoning : null
        };
        return Stream(model, context, bedrockOptions);
    }

    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        BedrockOptions options,
        AssistantMessageStream stream)
    {
        var profile = BedrockProfileCredentialsResolver.Load(options);
        var region = ResolveRegion(options, profile);
        var requestUri = BuildRequestUri(model, region);
        var body = BedrockMessageConverter.BuildRequestBody(model, context, options);
        var json = JsonSerializer.Serialize(body, BedrockJsonContext.Default.DictionaryStringObject);
        var payload = Encoding.UTF8.GetBytes(json);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.amazon.eventstream"));
        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);

        var skipAuth = string.Equals(Environment.GetEnvironmentVariable("AWS_BEDROCK_SKIP_AUTH"), "1", StringComparison.Ordinal);
        if (!skipAuth)
        {
            var bearerToken = ResolveBearerToken(options);
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }
            else
            {
                var credentials = ResolveCredentials(options, profile);
                if (credentials is null)
                {
                    stream.Push(new ErrorEvent(BuildMissingCredentialsMessage()));
                    return;
                }

                BedrockSigV4Signer.Sign(request, payload, credentials, region, _clock());
            }
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            stream.Push(new ErrorEvent($"Amazon Bedrock error {(int)response.StatusCode}: {errorBody}"));
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
        var parser = new BedrockStreamParser(initial, stream);

        await foreach (var message in BedrockEventStreamParser.ParseAsync(responseStream))
        {
            parser.ParseMessage(message);
            if (parser.Completed)
            {
                return;
            }
        }

        parser.EmitDoneIfNeeded();
    }

    private static BedrockOptions ToBedrockOptions(StreamOptions options)
    {
        if (options is BedrockOptions bedrockOptions)
        {
            return bedrockOptions;
        }

        return new BedrockOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            MaxRetryDelay = options.MaxRetryDelay,
            Metadata = options.Metadata
        };
    }

    private static Uri BuildRequestUri(Model model, string region)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(model.BaseUrl)
            ? model.BaseUrl.TrimEnd('/')
            : $"https://bedrock-runtime.{region}.amazonaws.com";
        var encodedModelId = Uri.EscapeDataString(model.Id);
        return new Uri($"{baseUrl}/model/{encodedModelId}/converse-stream", UriKind.Absolute);
    }

    private static string ResolveRegion(BedrockOptions options, BedrockProfileCredentials? profile) => FirstNonEmpty(
        options.Region,
        Environment.GetEnvironmentVariable("AWS_REGION"),
        Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION"),
        profile?.Region) ?? "us-east-1";

    private static string? ResolveBearerToken(BedrockOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.BearerToken))
        {
            return options.BearerToken;
        }

        if (!string.IsNullOrWhiteSpace(options.ApiKey) && !EnvironmentApiKeyResolver.IsAuthenticatedMarker(options.ApiKey))
        {
            return options.ApiKey;
        }

        return Environment.GetEnvironmentVariable("AWS_BEARER_TOKEN_BEDROCK");
    }

    private static BedrockAwsCredentials? ResolveCredentials(BedrockOptions options, BedrockProfileCredentials? profile)
    {
        var accessKeyId = FirstNonEmpty(options.AccessKeyId, Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"));
        var secretAccessKey = FirstNonEmpty(options.SecretAccessKey, Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"));
        var sessionToken = FirstNonEmpty(options.SessionToken, Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN"));
        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
        {
            if (profile is null ||
                string.IsNullOrWhiteSpace(profile.AccessKeyId) ||
                string.IsNullOrWhiteSpace(profile.SecretAccessKey))
            {
                return null;
            }

            return new BedrockAwsCredentials(profile.AccessKeyId, profile.SecretAccessKey, profile.SessionToken);
        }

        return new BedrockAwsCredentials(accessKeyId, secretAccessKey, sessionToken);
    }

    private static string BuildMissingCredentialsMessage()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_PROFILE")))
        {
            return "Amazon Bedrock requires AWS_BEARER_TOKEN_BEDROCK or AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY in Tau. AWS_PROFILE/shared credential loading is not implemented yet.";
        }

        return "Amazon Bedrock requires AWS_BEARER_TOKEN_BEDROCK or AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY.";
    }

    private static void ApplyHeaders(HttpRequestMessage request, IDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                request.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                continue;
            }

            request.Headers.Remove(key);
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public record BedrockOptions : StreamOptions
{
    public string? Region { get; init; }
    public string? BearerToken { get; init; }
    public string? AccessKeyId { get; init; }
    public string? SecretAccessKey { get; init; }
    public string? SessionToken { get; init; }
    public string? Profile { get; init; }
    public string? CredentialsFile { get; init; }
    public string? ConfigFile { get; init; }
    public string? ToolChoice { get; init; }
    public string? ToolName { get; init; }
    public ThinkingLevel? Reasoning { get; init; }
    public int? ThinkingBudgetTokens { get; init; }
    public string? ThinkingDisplay { get; init; }
    public IDictionary<string, string>? RequestMetadata { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(float?))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(decimal?))]
internal partial class BedrockJsonContext : JsonSerializerContext;
