using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tau.Ai.Providers.Bedrock;

internal sealed record BedrockAwsCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken);

internal static class BedrockSigV4Signer
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string Service = "bedrock";
    private const string TerminationString = "aws4_request";

    public static void Sign(
        HttpRequestMessage request,
        byte[] payload,
        BedrockAwsCredentials credentials,
        string region,
        DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        var amzDate = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = Hex(SHA256.HashData(payload));

        request.Headers.Remove("x-amz-date");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (!string.IsNullOrWhiteSpace(credentials.SessionToken))
        {
            request.Headers.Remove("x-amz-security-token");
            request.Headers.TryAddWithoutValidation("x-amz-security-token", credentials.SessionToken);
        }

        request.Headers.Host = request.RequestUri!.IsDefaultPort
            ? request.RequestUri.Host
            : request.RequestUri.Authority;

        var headers = CollectHeaders(request);
        var canonicalHeaders = string.Concat(headers.Select(header => $"{header.Key}:{header.Value}\n"));
        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalRequest = string.Join('\n',
            request.Method.Method,
            request.RequestUri.AbsolutePath.Length == 0 ? "/" : request.RequestUri.AbsolutePath,
            BuildCanonicalQueryString(request.RequestUri),
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        var credentialScope = $"{dateStamp}/{region}/{Service}/{TerminationString}";
        var stringToSign = string.Join('\n',
            Algorithm,
            amzDate,
            credentialScope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = GetSigningKey(credentials.SecretAccessKey, dateStamp, region, Service);
        var signature = Hex(Hmac(signingKey, stringToSign));
        var authorization = $"{Algorithm} Credential={credentials.AccessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    private static SortedDictionary<string, string> CollectHeaders(HttpRequestMessage request)
    {
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        headers["host"] = request.Headers.Host ?? request.RequestUri!.Authority;

        foreach (var header in request.Headers)
        {
            AddHeader(headers, header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                AddHeader(headers, header.Key, header.Value);
            }
        }

        return headers;
    }

    private static void AddHeader(IDictionary<string, string> headers, string key, IEnumerable<string> values)
    {
        if (string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var name = key.ToLowerInvariant();
        var value = string.Join(',', values.Select(NormalizeHeaderValue));
        headers[name] = value;
    }

    private static string NormalizeHeaderValue(string value)
    {
        var builder = new StringBuilder();
        var inWhitespace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inWhitespace)
                {
                    builder.Append(' ');
                    inWhitespace = true;
                }
            }
            else
            {
                builder.Append(ch);
                inWhitespace = false;
            }
        }

        return builder.ToString();
    }

    private static string BuildCanonicalQueryString(Uri uri)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return string.Empty;
        }

        return string.Join('&', query[1..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var pieces = part.Split('=', 2);
                var name = Uri.EscapeDataString(Uri.UnescapeDataString(pieces[0]));
                var value = pieces.Length == 2 ? Uri.EscapeDataString(Uri.UnescapeDataString(pieces[1])) : string.Empty;
                return (name, value);
            })
            .OrderBy(part => part.name, StringComparer.Ordinal)
            .ThenBy(part => part.value, StringComparer.Ordinal)
            .Select(part => $"{part.name}={part.value}"));
    }

    private static byte[] GetSigningKey(string secretAccessKey, string dateStamp, string region, string service)
    {
        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        return Hmac(kService, TerminationString);
    }

    private static byte[] Hmac(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
