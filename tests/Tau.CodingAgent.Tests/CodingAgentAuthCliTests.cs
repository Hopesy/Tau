using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentAuthCliTests
{
    [Fact]
    public async Task TryHandleAsync_IgnoresNonAuthCommands()
    {
        var exitCode = await CodingAgentAuthCli.TryHandleAsync(
            ["prompt"],
            new StringReader(string.Empty),
            TextWriter.Null,
            TextWriter.Null,
            oauthProviders: new OAuthProviderRegistry([]));

        Assert.Null(exitCode);
    }

    [Fact]
    public async Task TryHandleAsync_AuthListPrintsOAuthProviders()
    {
        var output = new StringWriter();
        var registry = new OAuthProviderRegistry(
        [
            new FakeOAuthProvider { Id = "custom-b", Name = "Custom B" },
            new FakeOAuthProvider { Id = "custom-a", Name = "Custom A" }
        ]);

        var exitCode = await CodingAgentAuthCli.TryHandleAsync(
            ["auth", "list"],
            new StringReader(string.Empty),
            output,
            TextWriter.Null,
            oauthProviders: registry);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Available OAuth providers:", text, StringComparison.Ordinal);
        Assert.Contains("custom-a", text, StringComparison.Ordinal);
        Assert.Contains("Custom A", text, StringComparison.Ordinal);
        Assert.Contains("custom-b", text, StringComparison.Ordinal);
        Assert.Contains("Custom B", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_AuthLoginWithProviderSavesCredentials()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            var provider = new FakeOAuthProvider { Id = "custom", Name = "Custom" };
            var registry = new OAuthProviderRegistry([provider]);
            var output = new StringWriter();

            var exitCode = await CodingAgentAuthCli.TryHandleAsync(
                ["auth", "login", "custom"],
                new StringReader(string.Empty),
                output,
                TextWriter.Null,
                oauthProviders: registry,
                credentialStore: new OAuthCredentialStore([authPath]),
                callbacksFactory: () => new NoopOAuthLoginCallbacks());

            Assert.Equal(0, exitCode);
            Assert.Equal(1, provider.LoginCalls);
            Assert.Contains("Logging in to custom...", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Credentials saved to auth.json.", output.ToString(), StringComparison.Ordinal);

            var saved = new OAuthCredentialStore([authPath]).Load();
            var credentials = Assert.Contains("custom", saved);
            Assert.Equal("refresh-token", credentials.Refresh);
            Assert.Equal("access-token", credentials.Access);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TryHandleAsync_LoginWithoutProviderSelectsInteractively()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-cli-select-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            var first = new FakeOAuthProvider { Id = "alpha", Name = "Alpha" };
            var second = new FakeOAuthProvider { Id = "beta", Name = "Beta" };
            var registry = new OAuthProviderRegistry([first, second]);
            var output = new StringWriter();

            var exitCode = await CodingAgentAuthCli.TryHandleAsync(
                ["login"],
                new StringReader("2" + Environment.NewLine),
                output,
                TextWriter.Null,
                oauthProviders: registry,
                credentialStore: new OAuthCredentialStore([authPath]),
                callbacksFactory: () => new NoopOAuthLoginCallbacks());

            Assert.Equal(0, exitCode);
            Assert.Equal(0, first.LoginCalls);
            Assert.Equal(1, second.LoginCalls);
            Assert.Contains("Select a provider:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Logging in to beta...", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("beta", new OAuthCredentialStore([authPath]).Load().Keys);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TryHandleAsync_UnknownProviderReturnsError()
    {
        var error = new StringWriter();
        var exitCode = await CodingAgentAuthCli.TryHandleAsync(
            ["auth", "login", "missing"],
            new StringReader(string.Empty),
            TextWriter.Null,
            error,
            oauthProviders: new OAuthProviderRegistry([]),
            callbacksFactory: () => new NoopOAuthLoginCallbacks());

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown provider: missing", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class NoopOAuthLoginCallbacks : IOAuthLoginCallbacks
    {
        public void OnAuth(string url, string? instructions = null)
        {
        }

        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) =>
            Task.FromResult(string.Empty);

        public void OnProgress(string message)
        {
        }

        public Task<string>? OnManualCodeInputAsync() => null;
    }
}
