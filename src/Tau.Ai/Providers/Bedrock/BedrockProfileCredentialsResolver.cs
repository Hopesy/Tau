namespace Tau.Ai.Providers.Bedrock;

internal static class BedrockProfileCredentialsResolver
{
    public static BedrockProfileSnapshot? Load(BedrockOptions options, string? profileNameOverride = null)
    {
        var profileName = profileNameOverride
            ?? FirstNonEmpty(options.Profile, ProviderEnvironment.GetValue("AWS_PROFILE", options.Env))
            ?? "default";
        var credentialsPath = ResolveCredentialsFile(options);
        var configPath = ResolveConfigFile(options);
        var credentials = ReadProfile(credentialsPath, profileName, configFile: false);
        var config = ReadProfile(configPath, profileName, configFile: true);
        if (credentials is null && config is null)
        {
            return null;
        }

        var ssoSession = FirstNonEmpty(Get(credentials, "sso_session"), Get(config, "sso_session"));
        var ssoStartUrl = FirstNonEmpty(Get(credentials, "sso_start_url"), Get(config, "sso_start_url"));
        var ssoRegion = FirstNonEmpty(Get(credentials, "sso_region"), Get(config, "sso_region"));
        var ssoRegistrationScopes = FirstNonEmpty(Get(credentials, "sso_registration_scopes"), Get(config, "sso_registration_scopes"));

        if (!string.IsNullOrWhiteSpace(ssoSession) && !string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            var ssoSection = ReadIni(configPath).GetValueOrDefault($"sso-session {ssoSession}");
            if (ssoSection is not null)
            {
                ssoStartUrl = FirstNonEmpty(ssoStartUrl, Get(ssoSection, "sso_start_url"));
                ssoRegion = FirstNonEmpty(ssoRegion, Get(ssoSection, "sso_region"));
                ssoRegistrationScopes = FirstNonEmpty(ssoRegistrationScopes, Get(ssoSection, "sso_registration_scopes"));
            }
        }

        return new BedrockProfileSnapshot
        {
            Name = profileName,
            AccessKeyId = FirstNonEmpty(Get(credentials, "aws_access_key_id"), Get(config, "aws_access_key_id")),
            SecretAccessKey = FirstNonEmpty(Get(credentials, "aws_secret_access_key"), Get(config, "aws_secret_access_key")),
            SessionToken = FirstNonEmpty(Get(credentials, "aws_session_token"), Get(config, "aws_session_token")),
            Region = FirstNonEmpty(Get(config, "region"), Get(credentials, "region")),
            RoleArn = FirstNonEmpty(Get(credentials, "role_arn"), Get(config, "role_arn")),
            SourceProfile = FirstNonEmpty(Get(credentials, "source_profile"), Get(config, "source_profile")),
            CredentialSource = FirstNonEmpty(Get(credentials, "credential_source"), Get(config, "credential_source")),
            RoleSessionName = FirstNonEmpty(Get(credentials, "role_session_name"), Get(config, "role_session_name")),
            ExternalId = FirstNonEmpty(Get(credentials, "external_id"), Get(config, "external_id")),
            WebIdentityTokenFile = FirstNonEmpty(Get(credentials, "web_identity_token_file"), Get(config, "web_identity_token_file")),
            CredentialProcess = FirstNonEmpty(Get(credentials, "credential_process"), Get(config, "credential_process")),
            MfaSerial = FirstNonEmpty(Get(credentials, "mfa_serial"), Get(config, "mfa_serial")),
            SsoSession = ssoSession,
            SsoStartUrl = ssoStartUrl,
            SsoRegion = ssoRegion,
            SsoAccountId = FirstNonEmpty(Get(credentials, "sso_account_id"), Get(config, "sso_account_id")),
            SsoRoleName = FirstNonEmpty(Get(credentials, "sso_role_name"), Get(config, "sso_role_name")),
            SsoRegistrationScopes = ssoRegistrationScopes
        };
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
        ProviderEnvironment.GetValue("AWS_SHARED_CREDENTIALS_FILE", options.Env),
        CombineUserProfile(".aws", "credentials")) ?? string.Empty;

    private static string ResolveConfigFile(BedrockOptions options) => FirstNonEmpty(
        options.ConfigFile,
        ProviderEnvironment.GetValue("AWS_CONFIG_FILE", options.Env),
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
