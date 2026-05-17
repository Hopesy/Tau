using System.Globalization;
using System.Xml.Linq;

namespace Tau.Ai.Providers.Bedrock;

internal static class BedrockStsResponseParser
{
    public static BedrockAwsCredentials? ParseCredentialsResponse(string xml, string sourceLabel, Func<DateTimeOffset> clock)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch
        {
            return null;
        }

        var credentialsElement = document.Descendants().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, "Credentials", StringComparison.Ordinal));
        if (credentialsElement is null)
        {
            return null;
        }

        var accessKeyId = GetChildValue(credentialsElement, "AccessKeyId");
        var secretAccessKey = GetChildValue(credentialsElement, "SecretAccessKey");
        var sessionToken = GetChildValue(credentialsElement, "SessionToken");
        var expirationRaw = GetChildValue(credentialsElement, "Expiration");

        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
        {
            return null;
        }

        DateTimeOffset? expiresAt = null;
        if (!string.IsNullOrWhiteSpace(expirationRaw))
        {
            if (!DateTimeOffset.TryParse(
                    expirationRaw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return null;
            }

            expiresAt = parsed;
            if (parsed <= clock())
            {
                return null;
            }
        }

        return new BedrockAwsCredentials(
            AccessKeyId: accessKeyId!,
            SecretAccessKey: secretAccessKey!,
            SessionToken: string.IsNullOrWhiteSpace(sessionToken) ? null : sessionToken,
            ExpiresAt: expiresAt,
            Source: sourceLabel);
    }

    public static string? ExtractErrorMessage(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var document = XDocument.Parse(xml);
            var errorElement = document.Descendants().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, "Error", StringComparison.Ordinal));
            if (errorElement is null)
            {
                return null;
            }

            var code = GetChildValue(errorElement, "Code");
            var message = GetChildValue(errorElement, "Message");
            if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(code)
                ? message
                : string.IsNullOrWhiteSpace(message)
                    ? code
                    : $"{code}: {message}";
        }
        catch
        {
            return null;
        }
    }

    public static string ResolveStsEndpoint(string? optionsOverride, string region)
    {
        var overrideEndpoint = FirstNonEmpty(
            optionsOverride,
            Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL_STS"),
            Environment.GetEnvironmentVariable("AWS_STS_ENDPOINT_URL"));
        if (!string.IsNullOrWhiteSpace(overrideEndpoint))
        {
            return overrideEndpoint!;
        }

        return string.IsNullOrWhiteSpace(region)
            ? "https://sts.amazonaws.com/"
            : $"https://sts.{region}.amazonaws.com/";
    }

    private static string? GetChildValue(XElement parent, string localName)
    {
        var child = parent.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));
        return child?.Value.Trim();
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
