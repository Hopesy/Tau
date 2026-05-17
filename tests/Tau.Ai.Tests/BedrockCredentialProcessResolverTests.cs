using System.Globalization;
using Tau.Ai.Providers.Bedrock;

namespace Tau.Ai.Tests;

public sealed class BedrockCredentialProcessResolverTests
{
    [Fact]
    public void TryTokenize_HandlesSimpleSpaceSeparatedArguments()
    {
        Assert.True(BedrockCredentialProcessResolver.TryTokenize("helper --profile dev", out var tokens, out _));
        Assert.Equal(new[] { "helper", "--profile", "dev" }, tokens);
    }

    [Fact]
    public void TryTokenize_PreservesDoubleQuotedArgumentsWithSpaces()
    {
        Assert.True(BedrockCredentialProcessResolver.TryTokenize("\"C:\\Program Files\\helper.exe\" --arg \"value with space\"", out var tokens, out _));
        Assert.Equal(new[] { "C:\\Program Files\\helper.exe", "--arg", "value with space" }, tokens);
    }

    [Fact]
    public void TryTokenize_PreservesSingleQuotedArguments()
    {
        Assert.True(BedrockCredentialProcessResolver.TryTokenize("helper '--token=abc def' --flag", out var tokens, out _));
        Assert.Equal(new[] { "helper", "--token=abc def", "--flag" }, tokens);
    }

    [Fact]
    public void TryTokenize_FailsOnUnterminatedDoubleQuote()
    {
        Assert.False(BedrockCredentialProcessResolver.TryTokenize("helper \"unterminated", out _, out var error));
        Assert.Contains("unterminated double-quoted", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsCredentialsWhenProcessReturnsValidJson()
    {
        const string json = """
            {
              "Version": 1,
              "AccessKeyId": "AKIAEXAMPLE",
              "SecretAccessKey": "secret-example",
              "SessionToken": "session-example",
              "Expiration": "2099-01-01T00:00:00Z"
            }
            """;
        var runner = new StubProcessRunner(new BedrockProcessResult(0, json, "", TimedOut: false));

        var outcome = await BedrockCredentialProcessResolver.ResolveAsync(
            "helper --profile dev",
            runner,
            () => DateTimeOffset.UtcNow);

        Assert.True(outcome.HasCredentials);
        var credentials = outcome.Credentials!;
        Assert.Equal("AKIAEXAMPLE", credentials.AccessKeyId);
        Assert.Equal("secret-example", credentials.SecretAccessKey);
        Assert.Equal("session-example", credentials.SessionToken);
        Assert.Equal("credential_process", credentials.Source);
        Assert.NotNull(credentials.ExpiresAt);
        Assert.Equal(
            DateTimeOffset.Parse("2099-01-01T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            credentials.ExpiresAt);
        Assert.NotNull(runner.LastRequest);
        Assert.Equal("helper", runner.LastRequest!.FileName);
        Assert.Equal(new[] { "--profile", "dev" }, runner.LastRequest.Arguments);
    }

    [Fact]
    public async Task ResolveAsync_TreatsNonZeroExitAsFailureAndIncludesStderr()
    {
        var runner = new StubProcessRunner(new BedrockProcessResult(2, "", "credential helper failed\n", TimedOut: false));

        var outcome = await BedrockCredentialProcessResolver.ResolveAsync(
            "helper",
            runner,
            () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.True(outcome.HasError);
        Assert.Contains("exited with status 2", outcome.Error!, StringComparison.Ordinal);
        Assert.Contains("credential helper failed", outcome.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsErrorWhenOutputIsNotJson()
    {
        var runner = new StubProcessRunner(new BedrockProcessResult(0, "not json", "", TimedOut: false));

        var outcome = await BedrockCredentialProcessResolver.ResolveAsync(
            "helper",
            runner,
            () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.Contains("not valid JSON", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_RejectsUnsupportedVersion()
    {
        const string json = """
            { "Version": 2, "AccessKeyId": "AKIA", "SecretAccessKey": "secret" }
            """;
        var runner = new StubProcessRunner(new BedrockProcessResult(0, json, "", TimedOut: false));

        var outcome = await BedrockCredentialProcessResolver.ResolveAsync(
            "helper",
            runner,
            () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.Contains("Version=2", outcome.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_RejectsExpiredCredentials()
    {
        var clock = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        const string json = """
            {
              "Version": 1,
              "AccessKeyId": "AKIA",
              "SecretAccessKey": "secret",
              "Expiration": "2020-01-01T00:00:00Z"
            }
            """;
        var runner = new StubProcessRunner(new BedrockProcessResult(0, json, "", TimedOut: false));

        var outcome = await BedrockCredentialProcessResolver.ResolveAsync(
            "helper",
            runner,
            () => clock);

        Assert.False(outcome.HasCredentials);
        Assert.Contains("already-expired", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsErrorWhenAccessKeyMissing()
    {
        const string json = """{ "Version": 1, "SecretAccessKey": "secret" }""";
        var runner = new StubProcessRunner(new BedrockProcessResult(0, json, "", TimedOut: false));

        var outcome = await BedrockCredentialProcessResolver.ResolveAsync(
            "helper",
            runner,
            () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.Contains("missing AccessKeyId or SecretAccessKey", outcome.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_ReportsTimeoutAsFailure()
    {
        var runner = new StubProcessRunner(new BedrockProcessResult(-1, "", "", TimedOut: true));

        var outcome = await BedrockCredentialProcessResolver.ResolveAsync(
            "helper",
            runner,
            () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.Contains("timed out", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_ReportsNotConfiguredOnEmptyCommand()
    {
        var runner = new StubProcessRunner(new BedrockProcessResult(0, "", "", TimedOut: false));

        var outcome = await BedrockCredentialProcessResolver.ResolveAsync(
            "",
            runner,
            () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.True(outcome.IsNotConfigured);
        Assert.False(outcome.HasError);
    }

    private sealed class StubProcessRunner : IBedrockProcessRunner
    {
        private readonly BedrockProcessResult _result;

        public StubProcessRunner(BedrockProcessResult result)
        {
            _result = result;
        }

        public BedrockProcessRequest? LastRequest { get; private set; }

        public Task<BedrockProcessResult> RunAsync(BedrockProcessRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }
}
