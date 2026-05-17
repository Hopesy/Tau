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

        if (string.IsNullOrWhiteSpace(profile.SourceProfile))
        {
            // For now we only support source_profile pointing at a static profile.
            // credential_source-based AssumeRole (Environment / EcsContainer / Ec2InstanceMetadata)
            // is left to a follow-up slice.
            return BedrockCredentialProcessOutcome.Failure(
                $"profile '{profile.Name}' has role_arn but no source_profile; credential_source-based AssumeRole is not yet supported.");
        }

        BedrockProfileSnapshot? sourceProfile;
        try
        {
            sourceProfile = BedrockProfileCredentialsResolver.Load(options, profile.SourceProfile);
        }
        catch (Exception ex)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"failed to load source_profile '{profile.SourceProfile}' for AssumeRole: {ex.Message}");
        }

        if (sourceProfile is null ||
            string.IsNullOrWhiteSpace(sourceProfile.AccessKeyId) ||
            string.IsNullOrWhiteSpace(sourceProfile.SecretAccessKey))
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"source_profile '{profile.SourceProfile}' for AssumeRole did not provide static credentials.");
        }

        var sourceCredentials = new BedrockAwsCredentials(
            sourceProfile.AccessKeyId!,
            sourceProfile.SecretAccessKey!,
            sourceProfile.SessionToken,
            Source: $"profile:{sourceProfile.Name}");

        var sessionName = !string.IsNullOrWhiteSpace(profile.RoleSessionName)
            ? profile.RoleSessionName!
            : $"tau-bedrock-{clock().ToUnixTimeSeconds()}";

        var formContent = BuildAssumeRoleFormContent(profile.RoleArn!, sessionName, profile.ExternalId);
        var payload = Encoding.UTF8.GetBytes(formContent);

        var endpoint = new Uri(
            BedrockStsResponseParser.ResolveStsEndpoint(options.StsEndpoint, region),
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
