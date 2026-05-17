using Tau.Ai;

namespace Tau.Ai.Tests;

public sealed class TauSecretRedactorTests
{
    [Fact]
    public void Disabled_LeavesInputUnchanged()
    {
        var redactor = new TauSecretRedactor(enabled: false);
        const string input = "AKIAIOSFODNN7EXAMPLE Bearer abcdefghijklmnopqrstuvwxyz";

        Assert.Equal(input, redactor.Redact(input));
    }

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ASIAY34FZKBOKMUTVV7A")]
    [InlineData("AROA1234567890ABCDEF")]
    public void Redact_ReplacesAwsAccessKeyPatterns(string key)
    {
        var redactor = new TauSecretRedactor(enabled: true);
        var output = redactor.Redact($"key=[{key}] note");
        Assert.Equal($"key=[{TauSecretRedactor.Placeholder}] note", output);
    }

    [Fact]
    public void Redact_ReplacesGitHubTokens()
    {
        var redactor = new TauSecretRedactor(enabled: true);
        var output = redactor.Redact("token=ghp_AAAA1111BBBB2222CCCC3333DDDD4444EEEE done");
        Assert.Equal($"token={TauSecretRedactor.Placeholder} done", output);
    }

    [Fact]
    public void Redact_ReplacesAnthropicAndOpenAiKeys()
    {
        var redactor = new TauSecretRedactor(enabled: true);
        var anthropic = redactor.Redact("api=sk-ant-api03-EXAMPLE-SECRET-TOKEN-9999 ok");
        Assert.Equal($"api={TauSecretRedactor.Placeholder} ok", anthropic);

        var openai = redactor.Redact("api=sk-EXAMPLE_BASE_KEY_99999999 ok");
        Assert.Equal($"api={TauSecretRedactor.Placeholder} ok", openai);
    }

    [Fact]
    public void Redact_ReplacesSlackTokens()
    {
        var redactor = new TauSecretRedactor(enabled: true);
        var output = redactor.Redact("token=xoxb-1234567890-098765-ABCDEFabcdefGH stop");
        Assert.Equal($"token={TauSecretRedactor.Placeholder} stop", output);
    }

    [Fact]
    public void Redact_ReplacesBearerTokens()
    {
        var redactor = new TauSecretRedactor(enabled: true);
        var output = redactor.Redact("Authorization: Bearer abcdef1234567890abcdef1234567890");
        Assert.Equal($"Authorization: {TauSecretRedactor.Placeholder}", output);
    }

    [Fact]
    public void Redact_ReplacesJwtsInText()
    {
        var redactor = new TauSecretRedactor(enabled: true);
        var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        var output = redactor.Redact($"jwt={jwt} keep");
        Assert.Equal($"jwt={TauSecretRedactor.Placeholder} keep", output);
    }

    [Fact]
    public void Redact_LeavesNonSecretsIntact()
    {
        var redactor = new TauSecretRedactor(enabled: true);
        const string input = "Hello world. command: dotnet build path=C:/repo";
        Assert.Equal(input, redactor.Redact(input));
    }

    [Fact]
    public void IsEnabledFromEnvironment_RespectsValues()
    {
        Assert.False(TauSecretRedactor.IsEnabledFromEnvironment("0"));
        Assert.False(TauSecretRedactor.IsEnabledFromEnvironment("false"));
        Assert.False(TauSecretRedactor.IsEnabledFromEnvironment("FALSE"));
        Assert.True(TauSecretRedactor.IsEnabledFromEnvironment("1"));
        Assert.True(TauSecretRedactor.IsEnabledFromEnvironment("true"));
        Assert.True(TauSecretRedactor.IsEnabledFromEnvironment(null));
        Assert.True(TauSecretRedactor.IsEnabledFromEnvironment(""));
    }
}
