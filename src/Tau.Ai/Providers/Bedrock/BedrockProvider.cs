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
    private readonly IBedrockProcessRunner _processRunner;

    public BedrockProvider(HttpClient? httpClient = null, Func<DateTimeOffset>? clock = null)
        : this(httpClient, clock, processRunner: null)
    {
    }

    internal BedrockProvider(
        HttpClient? httpClient,
        Func<DateTimeOffset>? clock,
        IBedrockProcessRunner? processRunner)
    {
        _httpClient = httpClient ?? new HttpClient();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _processRunner = processRunner ?? DefaultBedrockProcessRunner.Instance;
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
        var bedrockOptions = new BedrockOptions
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
            MaxRetryDelay = options.MaxRetryDelay,
            MaxRetries = options.MaxRetries,
            Metadata = options.Metadata,
            Reasoning = model.Reasoning ? options.Reasoning : null,
            ThinkingBudgets = options.ThinkingBudgets
        };
        return Stream(model, context, bedrockOptions);
    }

    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        BedrockOptions options,
        AssistantMessageStream stream)
    {
        if (StreamOptionHelpers.PushAbortedIfCanceled(options, stream, model, Api))
        {
            return;
        }

        var profile = BedrockProfileCredentialsResolver.Load(options);
        var region = ResolveRegion(options, profile);
        var requestUri = BuildRequestUri(model, region);
        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BedrockMessageConverter.BuildRequestBody(model, context, options)).ConfigureAwait(false);
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
                var credentialsOutcome = await ResolveCredentialsAsync(options, profile, region).ConfigureAwait(false);
                if (credentialsOutcome.Credentials is null)
                {
                    stream.Push(new ErrorEvent(credentialsOutcome.Error ?? BuildMissingCredentialsMessage()));
                    return;
                }

                BedrockSigV4Signer.Sign(request, payload, credentialsOutcome.Credentials, region, _clock());
            }
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, options.Signal).ConfigureAwait(false);
        await StreamOptionHelpers.InvokeResponseCallbackAsync(options, model, response).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(options.Signal).ConfigureAwait(false);
            stream.Push(new ErrorEvent($"Amazon Bedrock error {(int)response.StatusCode}: {errorBody}"));
            return;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(options.Signal).ConfigureAwait(false);
        var initial = new AssistantMessage
        {
            Api = Api,
            Provider = model.Provider,
            Model = model.Id,
            Content = []
        };
        var parser = new BedrockStreamParser(initial, stream);

        await foreach (var message in BedrockEventStreamParser.ParseAsync(responseStream, options.Signal))
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
            Signal = options.Signal,
            OnResponse = options.OnResponse,
            OnPayload = options.OnPayload,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            MaxRetryDelay = options.MaxRetryDelay,
            MaxRetries = options.MaxRetries,
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

    private static string ResolveRegion(BedrockOptions options, BedrockProfileSnapshot? profile) => FirstNonEmpty(
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

    private async Task<BedrockCredentialResolution> ResolveCredentialsAsync(BedrockOptions options, BedrockProfileSnapshot? profile, string region)
    {
        var staticCredentials = ResolveStaticCredentials(options, profile);
        if (staticCredentials is not null)
        {
            return BedrockCredentialResolution.Resolved(staticCredentials);
        }

        var webIdentityOutcome = await BedrockWebIdentityResolver.ResolveAsync(
            options,
            profile,
            region,
            _httpClient,
            _clock).ConfigureAwait(false);
        if (webIdentityOutcome.HasCredentials)
        {
            return BedrockCredentialResolution.Resolved(webIdentityOutcome.Credentials!);
        }

        if (webIdentityOutcome.HasError)
        {
            return BedrockCredentialResolution.Failed(webIdentityOutcome.Error!);
        }

        var assumeRoleOutcome = await BedrockAssumeRoleResolver.ResolveAsync(
            options,
            profile,
            region,
            _httpClient,
            _clock).ConfigureAwait(false);
        if (assumeRoleOutcome.HasCredentials)
        {
            return BedrockCredentialResolution.Resolved(assumeRoleOutcome.Credentials!);
        }

        if (assumeRoleOutcome.HasError)
        {
            return BedrockCredentialResolution.Failed(assumeRoleOutcome.Error!);
        }

        var ssoOutcome = await BedrockSsoResolver.ResolveAsync(
            options,
            profile,
            _httpClient,
            _clock).ConfigureAwait(false);
        if (ssoOutcome.HasCredentials)
        {
            return BedrockCredentialResolution.Resolved(ssoOutcome.Credentials!);
        }

        if (ssoOutcome.HasError)
        {
            return BedrockCredentialResolution.Failed(ssoOutcome.Error!);
        }

        var processCommand = ResolveCredentialProcessCommand(options, profile);
        if (!string.IsNullOrWhiteSpace(processCommand))
        {
            var outcome = await BedrockCredentialProcessResolver.ResolveAsync(
                processCommand!,
                _processRunner,
                _clock).ConfigureAwait(false);
            if (outcome.HasCredentials)
            {
                return BedrockCredentialResolution.Resolved(outcome.Credentials!);
            }

            if (outcome.HasError)
            {
                return BedrockCredentialResolution.Failed(outcome.Error!);
            }
        }

        var ecsOutcome = await BedrockEcsContainerResolver.ResolveAsync(
            options,
            _httpClient,
            _clock).ConfigureAwait(false);
        if (ecsOutcome.HasCredentials)
        {
            return BedrockCredentialResolution.Resolved(ecsOutcome.Credentials!);
        }

        if (ecsOutcome.HasError)
        {
            return BedrockCredentialResolution.Failed(ecsOutcome.Error!);
        }

        var imdsOutcome = await BedrockInstanceMetadataResolver.ResolveAsync(
            options,
            _httpClient,
            _clock).ConfigureAwait(false);
        if (imdsOutcome.HasCredentials)
        {
            return BedrockCredentialResolution.Resolved(imdsOutcome.Credentials!);
        }

        if (imdsOutcome.HasError)
        {
            return BedrockCredentialResolution.Failed(imdsOutcome.Error!);
        }

        return BedrockCredentialResolution.Failed(BuildMissingCredentialsMessage());
    }

    private static string? ResolveCredentialProcessCommand(BedrockOptions options, BedrockProfileSnapshot? profile)
    {
        if (!string.IsNullOrWhiteSpace(options.CredentialProcess))
        {
            return options.CredentialProcess;
        }

        return string.IsNullOrWhiteSpace(profile?.CredentialProcess) ? null : profile!.CredentialProcess;
    }

    private static BedrockAwsCredentials? ResolveStaticCredentials(BedrockOptions options, BedrockProfileSnapshot? profile)
    {
        var accessKeyId = FirstNonEmpty(options.AccessKeyId, Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"));
        var secretAccessKey = FirstNonEmpty(options.SecretAccessKey, Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"));
        var sessionToken = FirstNonEmpty(options.SessionToken, Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN"));
        if (!string.IsNullOrWhiteSpace(accessKeyId) && !string.IsNullOrWhiteSpace(secretAccessKey))
        {
            return new BedrockAwsCredentials(accessKeyId!, secretAccessKey!, sessionToken, Source: "static");
        }

        // When the active profile defines role_arn, its static credentials are the
        // source for AssumeRole and must not be returned as the profile's own
        // credentials.
        if (profile is not null &&
            string.IsNullOrWhiteSpace(profile.RoleArn) &&
            !string.IsNullOrWhiteSpace(profile.AccessKeyId) &&
            !string.IsNullOrWhiteSpace(profile.SecretAccessKey))
        {
            return new BedrockAwsCredentials(
                profile.AccessKeyId!,
                profile.SecretAccessKey!,
                profile.SessionToken,
                Source: $"profile:{profile.Name}");
        }

        return null;
    }

    private static string BuildMissingCredentialsMessage()
    {
        return "Amazon Bedrock requires AWS_BEARER_TOKEN_BEDROCK, AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY, a shared AWS profile with static credentials, AWS_WEB_IDENTITY_TOKEN_FILE + AWS_ROLE_ARN, a profile-configured credential_process, AWS_CONTAINER_CREDENTIALS_RELATIVE_URI/AWS_CONTAINER_CREDENTIALS_FULL_URI, or AWS_EC2_METADATA_DISABLED=false on an EC2 instance with an IAM role.";
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
    public string? CredentialProcess { get; init; }
    public string? WebIdentityTokenFile { get; init; }
    public string? WebIdentityRoleArn { get; init; }
    public string? WebIdentityRoleSessionName { get; init; }
    public string? StsEndpoint { get; init; }
    public string? SsoTokenCacheFile { get; init; }
    public string? SsoTokenCacheDirectory { get; init; }
    public string? SsoPortalEndpoint { get; init; }
    public string? SsoOidcEndpoint { get; init; }
    public string? ContainerCredentialsRelativeUri { get; init; }
    public string? ContainerCredentialsFullUri { get; init; }
    public string? ContainerAuthorizationToken { get; init; }
    public string? ContainerAuthorizationTokenFile { get; init; }
    public bool? Ec2MetadataDisabled { get; init; }
    public bool? Ec2MetadataV1Disabled { get; init; }
    public string? Ec2MetadataServiceEndpoint { get; init; }
    public TimeSpan? Ec2MetadataServiceTimeout { get; init; }
    public string? ToolChoice { get; init; }
    public string? ToolName { get; init; }
    public ThinkingLevel? Reasoning { get; init; }
    public int? ThinkingBudgetTokens { get; init; }
    public ThinkingBudgets? ThinkingBudgets { get; init; }
    public string? ThinkingDisplay { get; init; }
    public bool? InterleavedThinking { get; init; }
    public IDictionary<string, string>? RequestMetadata { get; init; }
}

internal readonly record struct BedrockCredentialResolution(BedrockAwsCredentials? Credentials, string? Error)
{
    public static BedrockCredentialResolution Resolved(BedrockAwsCredentials credentials) => new(credentials, null);
    public static BedrockCredentialResolution Failed(string error) => new(null, error);
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
