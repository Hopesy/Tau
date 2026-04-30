namespace Tau.Ai.Providers.Bedrock;

internal sealed record BedrockProfileCredentials(
    string? AccessKeyId,
    string? SecretAccessKey,
    string? SessionToken,
    string? Region);

internal static class BedrockProfileCredentialsResolver
{
    public static BedrockProfileCredentials? Load(BedrockOptions options)
    {
        var profileName = FirstNonEmpty(options.Profile, Environment.GetEnvironmentVariable("AWS_PROFILE")) ?? "default";
        var credentials = ReadProfile(ResolveCredentialsFile(options), profileName, configFile: false);
        var config = ReadProfile(ResolveConfigFile(options), profileName, configFile: true);
        if (credentials is null && config is null)
        {
            return null;
        }

        var accessKeyId = FirstNonEmpty(
            Get(credentials, "aws_access_key_id"),
            Get(config, "aws_access_key_id"));
        var secretAccessKey = FirstNonEmpty(
            Get(credentials, "aws_secret_access_key"),
            Get(config, "aws_secret_access_key"));
        var sessionToken = FirstNonEmpty(
            Get(credentials, "aws_session_token"),
            Get(config, "aws_session_token"));
        var region = FirstNonEmpty(
            Get(config, "region"),
            Get(credentials, "region"));

        if (string.IsNullOrWhiteSpace(accessKeyId) &&
            string.IsNullOrWhiteSpace(secretAccessKey) &&
            string.IsNullOrWhiteSpace(sessionToken) &&
            string.IsNullOrWhiteSpace(region))
        {
            return null;
        }

        return new BedrockProfileCredentials(accessKeyId, secretAccessKey, sessionToken, region);
    }

    private static Dictionary<string, string>? ReadProfile(string path, string profileName, bool configFile)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var sections = ReadIni(path);
        var sectionName = GetSectionName(profileName, configFile);
        if (sections.TryGetValue(sectionName, out var section))
        {
            return section;
        }

        if (!configFile && sections.TryGetValue(profileName, out section))
        {
            return section;
        }

        return null;
    }

    private static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[name] = current;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = StripInlineComment(line[(equals + 1)..].Trim());
            current[key] = value;
        }

        return result;
    }

    private static string StripInlineComment(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if ((value[i] == '#' || value[i] == ';') && i > 0 && char.IsWhiteSpace(value[i - 1]))
            {
                return value[..i].TrimEnd();
            }
        }

        return value;
    }

    private static string GetSectionName(string profileName, bool configFile)
    {
        if (!configFile || string.Equals(profileName, "default", StringComparison.OrdinalIgnoreCase))
        {
            return profileName;
        }

        return profileName.StartsWith("profile ", StringComparison.OrdinalIgnoreCase)
            ? profileName
            : $"profile {profileName}";
    }

    private static string ResolveCredentialsFile(BedrockOptions options) => FirstNonEmpty(
        options.CredentialsFile,
        Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE"),
        CombineUserProfile(".aws", "credentials")) ?? string.Empty;

    private static string ResolveConfigFile(BedrockOptions options) => FirstNonEmpty(
        options.ConfigFile,
        Environment.GetEnvironmentVariable("AWS_CONFIG_FILE"),
        CombineUserProfile(".aws", "config")) ?? string.Empty;

    private static string? CombineUserProfile(params string[] segments)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine([home, .. segments]);
    }

    private static string? Get(Dictionary<string, string>? values, string key) =>
        values is not null && values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
