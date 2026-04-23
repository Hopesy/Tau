using Tau.Ai.Auth;

namespace Tau.Ai.Tests;

public sealed class EnvironmentApiKeyResolverTests
{
    [Fact]
    public void GetApiKey_PrefersAnthropicOAuthToken()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set("ANTHROPIC_OAUTH_TOKEN", "oauth-token");
        scope.Set("ANTHROPIC_API_KEY", "api-key");

        var apiKey = EnvironmentApiKeyResolver.GetApiKey("anthropic");

        Assert.Equal("oauth-token", apiKey);
    }

    [Fact]
    public void GetApiKey_ReturnsAuthenticatedMarker_ForVertexCredentials()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-vertex-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var credentialsPath = Path.Combine(tempDir, "adc.json");
            File.WriteAllText(credentialsPath, "{}");

            scope.Set("GOOGLE_CLOUD_API_KEY", null);
            scope.Set("GOOGLE_APPLICATION_CREDENTIALS", credentialsPath);
            scope.Set("GOOGLE_CLOUD_PROJECT", "tau-project");
            scope.Set("GOOGLE_CLOUD_LOCATION", "us-central1");

            var apiKey = EnvironmentApiKeyResolver.GetApiKey("google-vertex");

            Assert.Equal(EnvironmentApiKeyResolver.AuthenticatedMarker, apiKey);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("github-copilot", "COPILOT_GITHUB_TOKEN")]
    [InlineData("mistral", "MISTRAL_API_KEY")]
    [InlineData("google", "GEMINI_API_KEY")]
    [InlineData("huggingface", "HF_TOKEN")]
    public void GetApiKey_MapsProviderToExpectedEnvironmentVariable(string provider, string variableName)
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set(variableName, $"value-for-{provider}");

        var apiKey = EnvironmentApiKeyResolver.GetApiKey(provider);

        Assert.Equal($"value-for-{provider}", apiKey);
    }
}
