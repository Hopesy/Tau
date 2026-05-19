using Tau.Ai.Auth.OAuth.Providers;

namespace Tau.Ai.Auth.OAuth;

public static class BuiltInOAuthProviders
{
    public static IReadOnlyList<IOAuthProvider> GetAll() =>
    [
        new AnthropicOAuthProvider(),
        new GitHubCopilotOAuthProvider(),
        new GeminiCliOAuthProvider(),
        new AntigravityOAuthProvider(),
        new OpenAICodexOAuthProvider()
    ];
}
