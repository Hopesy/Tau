using Tau.Ai.Auth;

namespace Tau.Ai.Tests;

public sealed class EnvironmentApiKeyResolverTests
{
    [Fact]
    public void GetApiKey_PrefersScopedEnvironmentOverAmbientEnvironment()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set("OPENAI_API_KEY", "ambient-key");

        var apiKey = EnvironmentApiKeyResolver.GetApiKey(
            "openai",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["OPENAI_API_KEY"] = "scoped-key"
            });

        Assert.Equal("scoped-key", apiKey);
        Assert.Equal("ambient-key", Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
    }

    [Theory]
    [InlineData("oauth-token", "api-key", "oauth-token")]
    [InlineData(null, "api-key", "api-key")]
    public void GetApiKey_ResolvesAnthropicCredentialWithExpectedPrecedence(string? oauthToken, string? apiKeyValue, string expected)
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set("ANTHROPIC_OAUTH_TOKEN", oauthToken);
        scope.Set("ANTHROPIC_API_KEY", apiKeyValue);

        var apiKey = EnvironmentApiKeyResolver.GetApiKey("anthropic");

        Assert.Equal(expected, apiKey);
    }

    [Theory]
    [InlineData("gemini-key", null, "gemini-key")]
    [InlineData(null, "google-key", "google-key")]
    [InlineData("gemini-key", "google-key", "gemini-key")]
    public void GetApiKey_ResolvesGoogleCredentialWithExpectedPrecedence(string? geminiKey, string? googleApiKey, string expected)
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set("GEMINI_API_KEY", geminiKey);
        scope.Set("GOOGLE_API_KEY", googleApiKey);

        var apiKey = EnvironmentApiKeyResolver.GetApiKey("google");

        Assert.Equal(expected, apiKey);
    }

    [Fact]
    public void GetApiKey_PrefersVertexExplicitApiKeyBeforeAmbientCredentials()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-vertex-explicit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var credentialsPath = Path.Combine(tempDir, "adc.json");
            File.WriteAllText(credentialsPath, "{}");

            scope.Set("GOOGLE_CLOUD_API_KEY", "vertex-explicit-key");
            scope.Set("GOOGLE_APPLICATION_CREDENTIALS", credentialsPath);
            scope.Set("GOOGLE_CLOUD_PROJECT", "tau-project");
            scope.Set("GOOGLE_CLOUD_LOCATION", "us-central1");

            var apiKey = EnvironmentApiKeyResolver.GetApiKey("google-vertex");

            Assert.Equal("vertex-explicit-key", apiKey);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
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

    [Fact]
    public void GetApiKey_ReturnsAuthenticatedMarker_ForScopedVertexCredentials()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-vertex-scoped-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var credentialsPath = Path.Combine(tempDir, "adc.json");
            File.WriteAllText(credentialsPath, "{}");

            scope.Set("GOOGLE_CLOUD_API_KEY", null);
            scope.Set("GOOGLE_APPLICATION_CREDENTIALS", null);
            scope.Set("GOOGLE_CLOUD_PROJECT", null);
            scope.Set("GOOGLE_CLOUD_LOCATION", null);

            var apiKey = EnvironmentApiKeyResolver.GetApiKey(
                "google-vertex",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GOOGLE_APPLICATION_CREDENTIALS"] = credentialsPath,
                    ["GOOGLE_CLOUD_PROJECT"] = "scoped-project",
                    ["GOOGLE_CLOUD_LOCATION"] = "us-central1"
                });

            Assert.Equal(EnvironmentApiKeyResolver.AuthenticatedMarker, apiKey);
            Assert.Null(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetApiKey_ReturnsAuthenticatedMarker_ForVertexCredentialsInWindowsAppDataPath()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"tau-vertex-appdata-{Guid.NewGuid():N}");
        var previousAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Directory.CreateDirectory(tempRoot);

        try
        {
            var gcloudDir = Path.Combine(tempRoot, "gcloud");
            Directory.CreateDirectory(gcloudDir);
            var credentialsPath = Path.Combine(gcloudDir, "application_default_credentials.json");
            File.WriteAllText(credentialsPath, "{}");

            scope.Set("APPDATA", tempRoot);
            scope.Set("GOOGLE_APPLICATION_CREDENTIALS", null);
            scope.Set("GOOGLE_CLOUD_API_KEY", null);
            scope.Set("GOOGLE_CLOUD_PROJECT", "tau-project");
            scope.Set("GOOGLE_CLOUD_LOCATION", "us-central1");

            var apiKey = EnvironmentApiKeyResolver.GetApiKey("google-vertex");

            Assert.Equal(EnvironmentApiKeyResolver.AuthenticatedMarker, apiKey);
        }
        finally
        {
            scope.Set("APPDATA", previousAppData);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetApiKey_ReturnsNull_ForVertexWhenProjectOrLocationMissing()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-vertex-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var credentialsPath = Path.Combine(tempDir, "adc.json");
            File.WriteAllText(credentialsPath, "{}");

            scope.Set("GOOGLE_CLOUD_API_KEY", null);
            scope.Set("GOOGLE_APPLICATION_CREDENTIALS", credentialsPath);
            scope.Set("GOOGLE_CLOUD_PROJECT", "tau-project");
            scope.Set("GOOGLE_CLOUD_LOCATION", null);

            var apiKey = EnvironmentApiKeyResolver.GetApiKey("google-vertex");

            Assert.Null(apiKey);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("copilot-token", "gh-token", "github-token", "copilot-token")]
    [InlineData(null, "gh-token", "github-token", "gh-token")]
    [InlineData(null, null, "github-token", "github-token")]
    public void GetApiKey_ResolvesCopilotCredentialWithExpectedFallbackOrder(
        string? copilotToken,
        string? ghToken,
        string? githubToken,
        string expected)
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set("COPILOT_GITHUB_TOKEN", copilotToken);
        scope.Set("GH_TOKEN", ghToken);
        scope.Set("GITHUB_TOKEN", githubToken);

        var apiKey = EnvironmentApiKeyResolver.GetApiKey("github-copilot");

        Assert.Equal(expected, apiKey);
    }

    [Theory]
    [InlineData("AWS_PROFILE", "tau-bedrock")]
    [InlineData("AWS_BEARER_TOKEN_BEDROCK", "bedrock-bearer-token")]
    [InlineData("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", "/v2/credentials/123")]
    [InlineData("AWS_CONTAINER_CREDENTIALS_FULL_URI", "http://169.254.170.2/v2/credentials/123")]
    [InlineData("AWS_WEB_IDENTITY_TOKEN_FILE", "/tmp/token-file")]
    public void GetApiKey_ReturnsAuthenticatedMarker_ForBedrockSingleAmbientCredentialSource(string variableName, string value)
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set("AWS_PROFILE", null);
        scope.Set("AWS_ACCESS_KEY_ID", null);
        scope.Set("AWS_SECRET_ACCESS_KEY", null);
        scope.Set("AWS_BEARER_TOKEN_BEDROCK", null);
        scope.Set("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", null);
        scope.Set("AWS_CONTAINER_CREDENTIALS_FULL_URI", null);
        scope.Set("AWS_WEB_IDENTITY_TOKEN_FILE", null);
        scope.Set(variableName, value);

        var apiKey = EnvironmentApiKeyResolver.GetApiKey("amazon-bedrock");

        Assert.Equal(EnvironmentApiKeyResolver.AuthenticatedMarker, apiKey);
    }

    [Theory]
    [InlineData("openai", "OPENAI_API_KEY")]
    [InlineData("azure-openai-responses", "AZURE_OPENAI_API_KEY")]
    [InlineData("groq", "GROQ_API_KEY")]
    [InlineData("cerebras", "CEREBRAS_API_KEY")]
    [InlineData("xai", "XAI_API_KEY")]
    [InlineData("openrouter", "OPENROUTER_API_KEY")]
    [InlineData("vercel-ai-gateway", "AI_GATEWAY_API_KEY")]
    [InlineData("zai", "ZAI_API_KEY")]
    [InlineData("mistral", "MISTRAL_API_KEY")]
    [InlineData("minimax", "MINIMAX_API_KEY")]
    [InlineData("minimax-cn", "MINIMAX_CN_API_KEY")]
    [InlineData("huggingface", "HF_TOKEN")]
    [InlineData("opencode", "OPENCODE_API_KEY")]
    [InlineData("opencode-go", "OPENCODE_API_KEY")]
    [InlineData("kimi-coding", "KIMI_API_KEY")]
    public void GetApiKey_MapsProviderToExpectedEnvironmentVariable(string provider, string variableName)
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set(variableName, $"value-for-{provider}");

        var apiKey = EnvironmentApiKeyResolver.GetApiKey(provider);

        Assert.Equal($"value-for-{provider}", apiKey);
    }

    [Fact]
    public void GetApiKey_ReturnsAuthenticatedMarker_ForBedrockAccessKeyPair()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set("AWS_PROFILE", null);
        scope.Set("AWS_ACCESS_KEY_ID", "access-key");
        scope.Set("AWS_SECRET_ACCESS_KEY", "secret-key");
        scope.Set("AWS_BEARER_TOKEN_BEDROCK", null);
        scope.Set("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", null);
        scope.Set("AWS_CONTAINER_CREDENTIALS_FULL_URI", null);
        scope.Set("AWS_WEB_IDENTITY_TOKEN_FILE", null);

        var apiKey = EnvironmentApiKeyResolver.GetApiKey("amazon-bedrock");

        Assert.Equal(EnvironmentApiKeyResolver.AuthenticatedMarker, apiKey);
    }

    [Fact]
    public void GetApiKey_ReturnsNull_ForBedrockWhenAccessKeyPairIsIncomplete()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set("AWS_PROFILE", null);
        scope.Set("AWS_ACCESS_KEY_ID", "access-key");
        scope.Set("AWS_SECRET_ACCESS_KEY", null);
        scope.Set("AWS_BEARER_TOKEN_BEDROCK", null);
        scope.Set("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", null);
        scope.Set("AWS_CONTAINER_CREDENTIALS_FULL_URI", null);
        scope.Set("AWS_WEB_IDENTITY_TOKEN_FILE", null);

        var apiKey = EnvironmentApiKeyResolver.GetApiKey("amazon-bedrock");

        Assert.Null(apiKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown-provider")]
    [InlineData("openai-codex")]
    public void GetApiKey_ReturnsNull_ForUnknownOrUnsupportedProvider(string? provider)
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set("OPENAI_API_KEY", null);
        scope.Set("GITHUB_TOKEN", null);

        var apiKey = EnvironmentApiKeyResolver.GetApiKey(provider!);

        Assert.Null(apiKey);
    }
}
