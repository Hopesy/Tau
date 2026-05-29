using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Observability;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

public sealed class ProviderAuthResolverTests
{
    [Fact]
    public void ResolveApiKey_PrefersExplicitValue()
    {
        var resolver = new ProviderAuthResolver();

        var result = resolver.ResolveApiKey("openai", "explicit-key");

        Assert.Equal("explicit-key", result);
    }

    [Fact]
    public void GetStatus_ReportsEnvironmentCredentialsWithoutLeakingSecret()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set("OPENAI_API_KEY", "secret-openai-key");
        scope.Set("TAU_AUTH_FILE", Path.Combine(Path.GetTempPath(), $"missing-auth-{Guid.NewGuid():N}.json"));

        var resolver = new ProviderAuthResolver();

        var status = resolver.GetStatus("openai");

        Assert.True(status.IsConfigured);
        Assert.Equal("environment", status.Source);
        Assert.DoesNotContain("secret-openai-key", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStatus_LogsRuntimeEventWithoutLeakingSecret()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set("OPENAI_API_KEY", "secret-openai-key");
        scope.Set("TAU_AUTH_FILE", Path.Combine(Path.GetTempPath(), $"missing-auth-{Guid.NewGuid():N}.json"));
        var sink = new CapturingLogSink();
        var resolver = new ProviderAuthResolver(logSink: sink);

        var status = resolver.GetStatus("openai");

        Assert.True(status.IsConfigured);
        var evt = Assert.Single(sink.Events);
        Assert.Equal("auth", evt.Category);
        Assert.Equal("status.checked", evt.Event);
        Assert.Equal("openai", evt.Fields["provider"]);
        Assert.Equal("true", evt.Fields["configured"]);
        Assert.Equal("environment", evt.Fields["source"]);
        Assert.Equal("false", evt.Fields["usesOAuth"]);
        Assert.Equal("false", evt.Fields["canLogin"]);
        Assert.False(evt.Fields.ContainsKey("message"));
        var fields = string.Join(
            Environment.NewLine,
            evt.Fields.Select(field => $"{field.Key}={field.Value}"));
        Assert.DoesNotContain("secret-openai-key", fields, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStatus_ReportsModelsJsonCredentials()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "custom-provider": {
                      "apiKey": "models-key",
                      "models": [
                        { "id": "custom-model" }
                      ]
                    }
                  }
                }
                """);
            var model = new Model
            {
                Provider = "custom-provider",
                Id = "custom-model",
                Name = "Custom Model",
                Api = "openai-chat-completions"
            };
            var resolver = CreateResolver(authPath, modelsPath);

            var status = resolver.GetStatus(model);

            Assert.True(status.IsConfigured);
            Assert.Equal("models.json", status.Source);
            Assert.DoesNotContain("models-key", status.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetStatus_ReportsCommandBackedModelsJsonCredentialWithoutExecutingCommand()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var markerPath = Path.Combine(tempDir, "command-ran.txt");
            var command = CreateSecretCommand(tempDir, markerPath);
            var commandValue = EscapeJson("!" + command);
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, $$"""
                {
                  "providers": {
                    "custom-provider": {
                      "apiKey": "{{commandValue}}",
                      "models": [
                        { "id": "custom-model" }
                      ]
                    }
                  }
                }
                """);
            var model = new Model
            {
                Provider = "custom-provider",
                Id = "custom-model",
                Name = "Custom Model",
                Api = "openai-chat-completions"
            };
            var resolver = CreateResolver(authPath, modelsPath);

            var status = resolver.GetStatus(model);

            Assert.True(status.IsConfigured);
            Assert.Equal("models.json", status.Source);
            Assert.Contains("do not execute", status.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-from-command", status.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetStatus_ReportsModelsJsonCredentialHeadersWithoutLeakingSecret()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "custom-provider": {
                      "headers": {
                        "Authorization": "Bearer header-secret"
                      },
                      "models": [
                        { "id": "custom-model" }
                      ]
                    }
                  }
                }
                """);
            var model = new Model
            {
                Provider = "custom-provider",
                Id = "custom-model",
                Name = "Custom Model",
                Api = "openai-chat-completions"
            };
            var resolver = CreateResolver(authPath, modelsPath);

            var status = resolver.GetStatus(model);

            Assert.True(status.IsConfigured);
            Assert.Equal("models.json", status.Source);
            Assert.DoesNotContain("header-secret", status.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveApiKey_UsesAuthFileApiKeyEntry()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, """
                {
                  "openrouter": {
                    "type": "api_key",
                    "key": "or-key"
                  }
                }
                """);

            var resolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]));

            var result = resolver.ResolveApiKey("openrouter");

            Assert.Equal("or-key", result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveApiKey_RefreshesExpiredOAuthCredentials()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, """
                {
                  "custom-oauth": {
                    "type": "oauth",
                    "refresh": "refresh-token",
                    "access": "expired-access",
                    "expiresAt": "2000-01-01T00:00:00Z"
                  }
                }
                """);

            var oauthRegistry = new OAuthProviderRegistry([new StubOAuthProvider()]);
            var resolver = new ProviderAuthResolver(
                oauthRegistry,
                new OAuthCredentialStore([authPath]));

            var result = resolver.ResolveApiKey("custom-oauth");

            Assert.Equal("refreshed-access", result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveModel_AppliesOAuthModelMutation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, """
                {
                  "custom-oauth": {
                    "type": "oauth",
                    "refresh": "refresh-token",
                    "access": "access-token",
                    "expiresAt": "2030-01-01T00:00:00Z"
                  }
                }
                """);

            var oauthRegistry = new OAuthProviderRegistry([new StubOAuthProvider()]);
            var resolver = new ProviderAuthResolver(
                oauthRegistry,
                new OAuthCredentialStore([authPath]));

            var model = new Model
            {
                Id = "model-1",
                Name = "Model 1",
                Api = "openai-responses",
                Provider = "custom-oauth",
                BaseUrl = "https://original.example.com"
            };

            var resolved = resolver.ResolveModel(model);

            Assert.Equal("https://mutated.example.com", resolved.BaseUrl);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Logout_RemovesAuthFileCredentialForProvider()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, """
                {
                  "openrouter": {
                    "type": "api_key",
                    "key": "or-key"
                  },
                  "anthropic": {
                    "type": "api_key",
                    "key": "anthropic-key"
                  }
                }
                """);
            var credentialStore = new OAuthCredentialStore([authPath]);
            var resolver = new ProviderAuthResolver(credentialStore: credentialStore);

            var removed = resolver.Logout("openrouter");

            Assert.True(removed);
            Assert.Null(resolver.ResolveApiKey("openrouter"));
            Assert.Equal("anthropic-key", resolver.ResolveApiKey("anthropic"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class StubOAuthProvider : IOAuthProvider
    {
        public string Id => "custom-oauth";
        public string Name => "Custom OAuth";

        public Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(credentials with
            {
                Access = "refreshed-access",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });
        }

        public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

        public Model ModifyModel(Model model, OAuthCredentials credentials) =>
            model with { BaseUrl = "https://mutated.example.com" };
    }

    private sealed class CapturingLogSink : ITauLogSink
    {
        public List<TauLogEvent> Events { get; } = [];

        public void Log(TauLogEvent evt) => Events.Add(evt);
    }

    private static ProviderAuthResolver CreateResolver(string authPath, string modelsPath) =>
        new(
            credentialStore: new OAuthCredentialStore([authPath]),
            configurationStore: new ModelConfigurationStore([modelsPath]));

    private static string CreateSecretCommand(string tempDir, string markerPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(tempDir, "secret-command.cmd");
            File.WriteAllText(scriptPath, $"""
                @echo off
                echo ran>"{markerPath}"
                echo secret-from-command
                """);
            return $"\"{scriptPath}\"";
        }

        var shellPath = Path.Combine(tempDir, "secret-command.sh");
        File.WriteAllText(shellPath, $"""
            #!/usr/bin/env sh
            echo ran > "{markerPath}"
            echo secret-from-command
            """);
        File.SetUnixFileMode(
            shellPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return $"\"{shellPath}\"";
    }

    private static string EscapeJson(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
