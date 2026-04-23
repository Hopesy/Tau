namespace Tau.Ai.Auth.OAuth;

public static class BuiltInOAuthProviders
{
    public static IReadOnlyList<IOAuthProvider> GetAll() =>
    [
        new UnsupportedLoginOAuthProvider("anthropic", "Anthropic (Claude Pro/Max)", credentials => credentials.Access),
        new UnsupportedLoginOAuthProvider("github-copilot", "GitHub Copilot", credentials => credentials.Access, ModifyGitHubCopilotModel),
        new UnsupportedLoginOAuthProvider("google-gemini-cli", "Google Cloud Code Assist (Gemini CLI)", BuildGoogleJsonApiKey),
        new UnsupportedLoginOAuthProvider("google-antigravity", "Google Antigravity", BuildGoogleJsonApiKey),
        new UnsupportedLoginOAuthProvider("openai-codex", "ChatGPT Plus/Pro (Codex Subscription)", credentials => credentials.Access)
    ];

    private static string BuildGoogleJsonApiKey(OAuthCredentials credentials)
    {
        if (!credentials.Metadata.TryGetValue("projectId", out var projectId) || string.IsNullOrWhiteSpace(projectId))
        {
            throw new InvalidOperationException("Google OAuth credentials are missing projectId metadata.");
        }

        return $$"""{"token":"{{credentials.Access}}","projectId":"{{projectId}}"}""";
    }

    private static Model ModifyGitHubCopilotModel(Model model, OAuthCredentials credentials)
    {
        if (!credentials.Metadata.TryGetValue("enterpriseUrl", out var enterpriseUrl) || string.IsNullOrWhiteSpace(enterpriseUrl))
        {
            return model;
        }

        var normalized = enterpriseUrl
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        return model with { BaseUrl = $"https://copilot-api.{normalized}" };
    }

    private sealed class UnsupportedLoginOAuthProvider : IOAuthProvider
    {
        private readonly Func<OAuthCredentials, string> _getApiKey;
        private readonly Func<Model, OAuthCredentials, Model> _modifyModel;

        public UnsupportedLoginOAuthProvider(
            string id,
            string name,
            Func<OAuthCredentials, string> getApiKey,
            Func<Model, OAuthCredentials, Model>? modifyModel = null)
        {
            Id = id;
            Name = name;
            _getApiKey = getApiKey;
            _modifyModel = modifyModel ?? ((model, _) => model);
        }

        public string Id { get; }
        public string Name { get; }

        public Task<OAuthCredentials> LoginAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException($"OAuth login flow for '{Id}' is not yet ported to Tau. Import credentials into auth.json instead.");

        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException($"OAuth token refresh for '{Id}' is not yet ported to Tau.");

        public string GetApiKey(OAuthCredentials credentials) => _getApiKey(credentials);

        public Model ModifyModel(Model model, OAuthCredentials credentials) => _modifyModel(model, credentials);
    }
}
