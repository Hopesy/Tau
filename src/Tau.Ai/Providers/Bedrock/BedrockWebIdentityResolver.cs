using System.Net.Http.Headers;
using System.Text;

namespace Tau.Ai.Providers.Bedrock;

internal static class BedrockWebIdentityResolver
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
        var tokenFile = FirstNonEmpty(
            options.WebIdentityTokenFile,
            Environment.GetEnvironmentVariable("AWS_WEB_IDENTITY_TOKEN_FILE"),
            profile?.WebIdentityTokenFile);
        var roleArn = FirstNonEmpty(
            options.WebIdentityRoleArn,
            Environment.GetEnvironmentVariable("AWS_ROLE_ARN"),
            profile?.RoleArn);

        if (string.IsNullOrWhiteSpace(tokenFile) || string.IsNullOrWhiteSpace(roleArn))
        {
            return BedrockCredentialProcessOutcome.NotConfigured();
        }

        if (!File.Exists(tokenFile))
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"web identity token file not found: {tokenFile}");
        }

        string token;
        try
        {
            token = (await File.ReadAllTextAsync(tokenFile, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (Exception ex)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"failed to read web identity token file ({tokenFile}): {ex.Message}");
        }

        if (string.IsNullOrEmpty(token))
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"web identity token file is empty: {tokenFile}");
        }

        var sessionName = FirstNonEmpty(
            options.WebIdentityRoleSessionName,
            Environment.GetEnvironmentVariable("AWS_ROLE_SESSION_NAME"),
            profile?.RoleSessionName)
            ?? $"tau-bedrock-{clock().ToUnixTimeSeconds()}";

        var endpoint = new Uri(BedrockStsResponseParser.ResolveStsEndpoint(options.StsEndpoint, region), UriKind.Absolute);
        var formContent = BuildAssumeRoleFormContent(roleArn!, sessionName, token);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(formContent, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"STS AssumeRoleWithWebIdentity request failed: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = ExtractStsError(body) ?? body.Trim();
                if (string.IsNullOrWhiteSpace(errorMessage))
                {
                    errorMessage = $"HTTP {(int)response.StatusCode}";
                }

                return BedrockCredentialProcessOutcome.Failure(
                    $"STS AssumeRoleWithWebIdentity rejected the request: {errorMessage}");
            }

            try
            {
                var credentials = ParseAssumeRoleResponse(body, clock);
                return credentials is null
                    ? BedrockCredentialProcessOutcome.Failure(
                        "STS AssumeRoleWithWebIdentity response did not contain credentials.")
                    : BedrockCredentialProcessOutcome.Success(credentials);
            }
            catch (Exception ex)
            {
                return BedrockCredentialProcessOutcome.Failure(
                    $"failed to parse STS AssumeRoleWithWebIdentity response: {ex.Message}");
            }
        }
    }

    private static string BuildAssumeRoleFormContent(string roleArn, string sessionName, string token)
    {
        var parameters = new[]
        {
            ("Action", "AssumeRoleWithWebIdentity"),
            ("Version", StsApiVersion),
            ("RoleArn", roleArn),
            ("RoleSessionName", sessionName),
            ("WebIdentityToken", token)
        };

        var builder = new StringBuilder();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(parameters[i].Item1));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(parameters[i].Item2));
        }

        return builder.ToString();
    }

    internal static BedrockAwsCredentials? ParseAssumeRoleResponse(string xml, Func<DateTimeOffset> clock)
        => BedrockStsResponseParser.ParseCredentialsResponse(xml, "web_identity", clock);

    internal static string? ExtractStsError(string xml)
        => BedrockStsResponseParser.ExtractErrorMessage(xml);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
