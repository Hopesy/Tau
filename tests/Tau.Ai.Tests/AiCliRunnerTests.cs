using System.Text.Json;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Cli;

namespace Tau.Ai.Tests;

public sealed class AiCliRunnerTests
{
    [Fact]
    public async Task RunAsync_HelpShowsCommandsProvidersAndAuthFileOption()
    {
        var provider = new FakeOAuthProvider("anthropic", "Anthropic (Claude Pro/Max)");
        var console = new FakeConsole();
        var runner = CreateRunner(console, provider);

        var exitCode = await runner.RunAsync(["--help"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: tau-ai <command> [provider] [options]", console.Output);
        Assert.Contains("login [provider]", console.Output);
        Assert.Contains("list", console.Output);
        Assert.Contains("--auth-file PATH", console.Output);
        Assert.Contains("anthropic", console.Output);
        Assert.Empty(console.Error);
    }

    [Fact]
    public async Task RunAsync_ListShowsRegisteredOAuthProviders()
    {
        var provider = new FakeOAuthProvider("google-gemini-cli", "Google Cloud Code Assist (Gemini CLI)");
        var console = new FakeConsole();
        var runner = CreateRunner(console, provider);

        var exitCode = await runner.RunAsync(["list"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Available OAuth providers:", console.Output);
        Assert.Contains("google-gemini-cli", console.Output);
        Assert.Contains("Google Cloud Code Assist (Gemini CLI)", console.Output);
    }

    [Fact]
    public async Task RunAsync_LoginWithAuthFileWritesOAuthCredentialsToExplicitPath()
    {
        using var temp = new TempDir();
        var authPath = Path.Combine(temp.Path, "auth.json");
        var provider = new FakeOAuthProvider("anthropic", "Anthropic")
        {
            Credentials = new OAuthCredentials
            {
                Access = "access-token",
                Refresh = "refresh-token",
                ExpiresAt = new DateTimeOffset(2026, 5, 29, 10, 0, 0, TimeSpan.Zero),
                Metadata = new Dictionary<string, string>
                {
                    ["accountId"] = "account-1",
                    ["access"] = "shadow-access"
                }
            }
        };
        var console = new FakeConsole();
        var runner = CreateRunner(console, provider);

        var exitCode = await runner.RunAsync(["--auth-file", authPath, "login", "anthropic"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, provider.LoginCalls);
        Assert.Contains($"Credentials saved to {authPath}", console.Output);

        using var document = JsonDocument.Parse(File.ReadAllText(authPath));
        var credential = document.RootElement.GetProperty("anthropic");
        Assert.Equal("oauth", credential.GetProperty("type").GetString());
        Assert.Equal("access-token", credential.GetProperty("access").GetString());
        Assert.Equal("refresh-token", credential.GetProperty("refresh").GetString());
        Assert.Equal("2026-05-29T10:00:00.0000000+00:00", credential.GetProperty("expiresAt").GetString());
        Assert.Equal("account-1", credential.GetProperty("accountId").GetString());
        Assert.Equal(5, credential.EnumerateObject().Count());
    }

    [Fact]
    public async Task RunAsync_LoginWithoutProviderSelectsProviderInteractively()
    {
        using var temp = new TempDir();
        var authPath = Path.Combine(temp.Path, "auth.json");
        var anthropic = new FakeOAuthProvider("anthropic", "Anthropic");
        var google = new FakeOAuthProvider("google-gemini-cli", "Google Cloud Code Assist");
        var console = new FakeConsole();
        console.EnqueueInput("2");
        var runner = CreateRunner(console, anthropic, google);

        var exitCode = await runner.RunAsync(["login", "--auth-file", authPath]);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, anthropic.LoginCalls);
        Assert.Equal(1, google.LoginCalls);
        Assert.Contains("Select a provider:", console.Output);
        Assert.Contains("1. Anthropic", console.Output);
        Assert.Contains("2. Google Cloud Code Assist", console.Output);

        using var document = JsonDocument.Parse(File.ReadAllText(authPath));
        Assert.True(document.RootElement.TryGetProperty("google-gemini-cli", out _));
    }

    [Fact]
    public async Task RunAsync_LoginUnknownProviderReturnsErrorAndDoesNotWriteCredentials()
    {
        using var temp = new TempDir();
        var authPath = Path.Combine(temp.Path, "auth.json");
        var provider = new FakeOAuthProvider("anthropic", "Anthropic");
        var console = new FakeConsole();
        var runner = CreateRunner(console, provider);

        var exitCode = await runner.RunAsync(["login", "missing", "--auth-file", authPath]);

        Assert.Equal(1, exitCode);
        Assert.Equal(0, provider.LoginCalls);
        Assert.False(File.Exists(authPath));
        Assert.Contains("Unknown provider: missing", console.Error);
        Assert.Contains("Use 'tau-ai list' to see available providers.", console.Error);
    }

    [Fact]
    public async Task RunAsync_LoginWithoutAuthFileUsesTauDefaultAuthStore()
    {
        using var temp = new TempDir();
        var authPath = Path.Combine(temp.Path, ".tau", "auth.json");
        var provider = new FakeOAuthProvider("anthropic", "Anthropic");
        var console = new FakeConsole();
        var runner = CreateRunner(console, [authPath], provider);

        var exitCode = await runner.RunAsync(["login", "anthropic"]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(authPath));
        Assert.Contains($"Credentials saved to {authPath}", console.Output);
    }

    [Fact]
    public async Task RunAsync_AuthFileWithoutPathReturnsUsageError()
    {
        var console = new FakeConsole();
        var runner = CreateRunner(console, new FakeOAuthProvider("anthropic", "Anthropic"));

        var exitCode = await runner.RunAsync(["--auth-file"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("--auth-file requires a path.", console.Error);
    }

    private static AiCliRunner CreateRunner(FakeConsole console, params IOAuthProvider[] providers) =>
        new(new OAuthProviderRegistry(providers), console, paths => new OAuthCredentialStoreWriter(paths));

    private static AiCliRunner CreateRunner(
        FakeConsole console,
        IReadOnlyList<string> defaultAuthPaths,
        params IOAuthProvider[] providers) =>
        new(
            new OAuthProviderRegistry(providers),
            console,
            paths => new OAuthCredentialStoreWriter(paths),
            defaultAuthSearchPathsFactory: () => defaultAuthPaths);

    private sealed class FakeConsole : IAiCliConsole
    {
        private readonly Queue<string?> _inputs = new();

        public string Output { get; private set; } = "";

        public string Error { get; private set; } = "";

        public void EnqueueInput(string? input) => _inputs.Enqueue(input);

        public void WriteLine(string message = "") => Output += message + Environment.NewLine;

        public void WriteErrorLine(string message) => Error += message + Environment.NewLine;

        public Task<string?> ReadLineAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Output += prompt;
            return Task.FromResult(_inputs.Count == 0 ? null : _inputs.Dequeue());
        }
    }

    private sealed class FakeOAuthProvider(string id, string name) : IOAuthProvider
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public int LoginCalls { get; private set; }

        public OAuthCredentials Credentials { get; init; } = new()
        {
            Access = "sample-access",
            Refresh = "sample-refresh",
            ExpiresAt = new DateTimeOffset(2026, 5, 29, 10, 0, 0, TimeSpan.Zero)
        };

        public Task<OAuthCredentials> LoginAsync(
            IOAuthLoginCallbacks callbacks,
            CancellationToken cancellationToken = default)
        {
            LoginCalls++;
            callbacks.OnAuth("https://example.test/login", "sample instructions");
            callbacks.OnProgress("sample progress");
            return Task.FromResult(Credentials);
        }

        public Task<OAuthCredentials> RefreshTokenAsync(
            OAuthCredentials credentials,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(credentials);

        public string GetApiKey(OAuthCredentials credentials) => credentials.Access;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-ai-cli-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
