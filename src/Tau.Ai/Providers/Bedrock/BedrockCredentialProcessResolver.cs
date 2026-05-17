using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tau.Ai.Providers.Bedrock;

internal static class BedrockCredentialProcessResolver
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static async Task<BedrockCredentialProcessOutcome> ResolveAsync(
        string command,
        IBedrockProcessRunner runner,
        Func<DateTimeOffset> clock,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return BedrockCredentialProcessOutcome.NotConfigured();
        }

        if (!TryTokenize(command, out var tokens, out var tokenizeError))
        {
            return BedrockCredentialProcessOutcome.Failure(tokenizeError);
        }

        if (tokens.Count == 0)
        {
            return BedrockCredentialProcessOutcome.Failure("credential_process command is empty.");
        }

        var fileName = tokens[0];
        var arguments = tokens.Count > 1 ? tokens.GetRange(1, tokens.Count - 1) : new List<string>();

        BedrockProcessResult result;
        try
        {
            result = await runner.RunAsync(
                new BedrockProcessRequest(fileName, arguments, timeout ?? DefaultTimeout),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return BedrockCredentialProcessOutcome.Failure($"credential_process failed to start: {ex.Message}");
        }

        if (result.TimedOut)
        {
            return BedrockCredentialProcessOutcome.Failure("credential_process timed out.");
        }

        if (result.ExitCode != 0)
        {
            var stderr = result.StandardError.Trim();
            var detail = string.IsNullOrEmpty(stderr) ? "" : $": {stderr}";
            return BedrockCredentialProcessOutcome.Failure(
                $"credential_process exited with status {result.ExitCode}{detail}");
        }

        var stdout = result.StandardOutput.Trim();
        if (string.IsNullOrEmpty(stdout))
        {
            return BedrockCredentialProcessOutcome.Failure("credential_process produced no output.");
        }

        return ParseCredentialJson(stdout, clock);
    }

    private static BedrockCredentialProcessOutcome ParseCredentialJson(string json, Func<DateTimeOffset> clock)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return BedrockCredentialProcessOutcome.Failure($"credential_process output is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return BedrockCredentialProcessOutcome.Failure("credential_process output must be a JSON object.");
            }

            if (document.RootElement.TryGetProperty("Version", out var versionProperty))
            {
                if (versionProperty.ValueKind == JsonValueKind.Number &&
                    versionProperty.TryGetInt32(out var version) &&
                    version != 1)
                {
                    return BedrockCredentialProcessOutcome.Failure(
                        $"credential_process returned unsupported Version={version}; expected 1.");
                }
            }

            var accessKeyId = GetString(document.RootElement, "AccessKeyId");
            var secretAccessKey = GetString(document.RootElement, "SecretAccessKey");
            if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
            {
                return BedrockCredentialProcessOutcome.Failure(
                    "credential_process output is missing AccessKeyId or SecretAccessKey.");
            }

            var sessionToken = GetString(document.RootElement, "SessionToken");
            DateTimeOffset? expiresAt = null;
            if (document.RootElement.TryGetProperty("Expiration", out var expirationProperty) &&
                expirationProperty.ValueKind == JsonValueKind.String)
            {
                var raw = expirationProperty.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    if (!DateTimeOffset.TryParse(
                            raw,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var parsed))
                    {
                        return BedrockCredentialProcessOutcome.Failure(
                            $"credential_process returned unparseable Expiration value: {raw}");
                    }

                    expiresAt = parsed;
                    if (parsed <= clock())
                    {
                        return BedrockCredentialProcessOutcome.Failure(
                            $"credential_process returned already-expired credentials (Expiration={raw}).");
                    }
                }
            }

            return BedrockCredentialProcessOutcome.Success(new BedrockAwsCredentials(
                AccessKeyId: accessKeyId!,
                SecretAccessKey: secretAccessKey!,
                SessionToken: string.IsNullOrWhiteSpace(sessionToken) ? null : sessionToken,
                ExpiresAt: expiresAt,
                Source: "credential_process"));
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    internal static bool TryTokenize(string command, out List<string> tokens, out string error)
    {
        tokens = new List<string>();
        error = string.Empty;

        var buffer = new StringBuilder();
        var inDouble = false;
        var inSingle = false;
        var hasToken = false;

        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (inDouble)
            {
                if (ch == '\\' && i + 1 < command.Length)
                {
                    var next = command[i + 1];
                    if (next is '"' or '\\')
                    {
                        buffer.Append(next);
                        i++;
                        hasToken = true;
                        continue;
                    }
                }

                if (ch == '"')
                {
                    inDouble = false;
                    continue;
                }

                buffer.Append(ch);
                hasToken = true;
                continue;
            }

            if (inSingle)
            {
                if (ch == '\'')
                {
                    inSingle = false;
                    continue;
                }

                buffer.Append(ch);
                hasToken = true;
                continue;
            }

            switch (ch)
            {
                case '"':
                    inDouble = true;
                    hasToken = true;
                    continue;
                case '\'':
                    inSingle = true;
                    hasToken = true;
                    continue;
                case ' ':
                case '\t':
                    if (hasToken)
                    {
                        tokens.Add(buffer.ToString());
                        buffer.Clear();
                        hasToken = false;
                    }
                    continue;
                default:
                    buffer.Append(ch);
                    hasToken = true;
                    continue;
            }
        }

        if (inDouble)
        {
            error = "credential_process command has an unterminated double-quoted argument.";
            return false;
        }

        if (inSingle)
        {
            error = "credential_process command has an unterminated single-quoted argument.";
            return false;
        }

        if (hasToken)
        {
            tokens.Add(buffer.ToString());
        }

        return true;
    }
}

internal sealed record BedrockCredentialProcessOutcome
{
    private BedrockCredentialProcessOutcome(BedrockAwsCredentials? credentials, string? error, bool notConfigured)
    {
        Credentials = credentials;
        Error = error;
        IsNotConfigured = notConfigured;
    }

    public BedrockAwsCredentials? Credentials { get; }
    public string? Error { get; }
    public bool IsNotConfigured { get; }
    public bool HasCredentials => Credentials is not null;
    public bool HasError => !string.IsNullOrEmpty(Error);

    public static BedrockCredentialProcessOutcome NotConfigured() => new(null, null, notConfigured: true);
    public static BedrockCredentialProcessOutcome Success(BedrockAwsCredentials credentials) => new(credentials, null, notConfigured: false);
    public static BedrockCredentialProcessOutcome Failure(string error) => new(null, error, notConfigured: false);
}
