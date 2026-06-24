using System.Net.Http.Headers;
using System.Text;

namespace Tau.Ai.Providers.Bedrock;

internal static class BedrockAssumeRoleResolver
{
    private const string StsApiVersion = "2011-06-15";

    public static async Task<BedrockCredentialProcessOutcome> ResolveAsync(
        BedrockOptions options,
        BedrockProfileSnapshot? profile,
        string region,
        HttpClient httpClient,
        Func<DateTimeOffset> clock,
        CancellationToken cancellationToken = default)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.RoleArn))
        {
            return BedrockCredentialProcessOutcome.NotConfigured();
        }

        BedrockAwsCredentials? sourceCredentials;
        if (!string.IsNullOrWhiteSpace(profile.SourceProfile))
        {
            var resolved = await LoadSourceProfileAsync(options, profile.SourceProfile!).ConfigureAwait(false);
            if (resolved.Error is not null)
            {
                return BedrockCredentialProcessOutcome.Failure(resolved.Error);
            }

            sourceCredentials = resolved.Credentials;
        }
        else if (!string.IsNullOrWhiteSpace(profile.CredentialSource))
        {
            var resolved = await LoadCredentialSourceAsync(options, profile.CredentialSource!, region, httpClient, clock, cancellationToken).ConfigureAwait(false);
            if (resolved.Error is not null)
            {
                return BedrockCredentialProcessOutcome.Failure(resolved.Error);
            }

            sourceCredentials = resolved.Credentials;
        }
        else
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"profile '{profile.Name}' has role_arn but neither source_profile nor credential_source is set.");
        }

        if (sourceCredentials is null)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"AssumeRole source for profile '{profile.Name}' did not yield credentials.");
        }

        var sessionName = !string.IsNullOrWhiteSpace(profile.RoleSessionName)
            ? profile.RoleSessionName!
            : $"tau-bedrock-{clock().ToUnixTimeSeconds()}";

        var formContent = BuildAssumeRoleFormContent(profile.RoleArn!, sessionName, profile.ExternalId);
        var payload = Encoding.UTF8.GetBytes(formContent);

        var endpoint = new Uri(
            BedrockStsResponseParser.ResolveStsEndpoint(options.StsEndpoint, region, options.Env),
            UriKind.Absolute);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));

        BedrockSigV4Signer.Sign(request, payload, sourceCredentials, region, "sts", clock());

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"STS AssumeRole request failed: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = BedrockStsResponseParser.ExtractErrorMessage(body) ?? body.Trim();
                if (string.IsNullOrWhiteSpace(errorMessage))
                {
                    errorMessage = $"HTTP {(int)response.StatusCode}";
                }

                return BedrockCredentialProcessOutcome.Failure(
                    $"STS AssumeRole rejected the request: {errorMessage}");
            }

            var credentials = BedrockStsResponseParser.ParseCredentialsResponse(body, "assume_role", clock);
            return credentials is null
                ? BedrockCredentialProcessOutcome.Failure(
                    "STS AssumeRole response did not contain credentials.")
                : BedrockCredentialProcessOutcome.Success(credentials);
        }
    }

    private static Task<(BedrockAwsCredentials? Credentials, string? Error)> LoadSourceProfileAsync(
        BedrockOptions options,
        string sourceProfileName)
    {
        BedrockProfileSnapshot? sourceProfile;
        try
        {
            sourceProfile = BedrockProfileCredentialsResolver.Load(options, sourceProfileName);
        }
        catch (Exception ex)
        {
            return Task.FromResult<(BedrockAwsCredentials?, string?)>(
                (null, $"failed to load source_profile '{sourceProfileName}' for AssumeRole: {ex.Message}"));
        }

        if (sourceProfile is null ||
            string.IsNullOrWhiteSpace(sourceProfile.AccessKeyId) ||
            string.IsNullOrWhiteSpace(sourceProfile.SecretAccessKey))
        {
            return Task.FromResult<(BedrockAwsCredentials?, string?)>(
                (null, $"source_profile '{sourceProfileName}' for AssumeRole did not provide static credentials."));
        }

        return Task.FromResult<(BedrockAwsCredentials?, string?)>((
            new BedrockAwsCredentials(
                sourceProfile.AccessKeyId!,
                sourceProfile.SecretAccessKey!,
                sourceProfile.SessionToken,
                Source: $"profile:{sourceProfile.Name}"),
            null));
    }

    private static async Task<(BedrockAwsCredentials? Credentials, string? Error)> LoadCredentialSourceAsync(
        BedrockOptions options,
        string credentialSource,
        string region,
        HttpClient httpClient,
        Func<DateTimeOffset> clock,
        CancellationToken cancellationToken)
    {
        if (string.Equals(credentialSource, "Environment", StringComparison.OrdinalIgnoreCase))
        {
            var accessKey = ProviderEnvironment.GetValue("AWS_ACCESS_KEY_ID", options.Env);
            var secretKey = ProviderEnvironment.GetValue("AWS_SECRET_ACCESS_KEY", options.Env);
            var session = ProviderEnvironment.GetValue("AWS_SESSION_TOKEN", options.Env);
            if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            {
                return (null, "credential_source=Environment requires AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY.");
            }

            return (new BedrockAwsCredentials(accessKey, secretKey, session, Source: "static"), null);
        }

        if (string.Equals(credentialSource, "EcsContainer", StringComparison.OrdinalIgnoreCase))
        {
            var outcome = await BedrockEcsContainerResolver.ResolveAsync(options, httpClient, clock, cancellationToken).ConfigureAwait(false);
            if (outcome.HasCredentials)
            {
                return (outcome.Credentials, null);
            }

            return (null, outcome.Error ?? "credential_source=EcsContainer requires AWS_CONTAINER_CREDENTIALS_RELATIVE_URI or _FULL_URI.");
        }

        if (string.Equals(credentialSource, "Ec2InstanceMetadata", StringComparison.OrdinalIgnoreCase))
        {
            // For credential_source AssumeRole, IMDS is implicitly opt-in regardless of
            // BedrockOptions.Ec2MetadataDisabled, because the profile explicitly asked for it.
            var optionsForImds = options with { Ec2MetadataDisabled = false };
            var outcome = await BedrockInstanceMetadataResolver.ResolveAsync(optionsForImds, httpClient, clock, cancellationToken).ConfigureAwait(false);
            if (outcome.HasCredentials)
            {
                return (outcome.Credentials, null);
            }

            return (null, outcome.Error ?? "credential_source=Ec2InstanceMetadata could not load credentials from IMDS.");
        }

        return (null, $"credential_source '{credentialSource}' is not supported; use Environment, EcsContainer, or Ec2InstanceMetadata.");
    }

    private static string BuildAssumeRoleFormContent(string roleArn, string sessionName, string? externalId)
    {
        var parameters = new List<(string Name, string Value)>
        {
            ("Action", "AssumeRole"),
            ("Version", StsApiVersion),
            ("RoleArn", roleArn),
            ("RoleSessionName", sessionName)
        };

        if (!string.IsNullOrWhiteSpace(externalId))
        {
            parameters.Add(("ExternalId", externalId));
        }

        var builder = new StringBuilder();
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(parameters[i].Name));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(parameters[i].Value));
        }

        return builder.ToString();
    }
}
