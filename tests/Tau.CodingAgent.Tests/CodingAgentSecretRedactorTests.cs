using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentSecretRedactorTests
{
    [Fact]
    public void Disabled_LeavesInputUnchanged()
    {
        var redactor = new CodingAgentSecretRedactor(enabled: false);
        const string input = "AKIAIOSFODNN7EXAMPLE Bearer abcdefghijklmnopqrstuvwxyz";

        Assert.Equal(input, redactor.Redact(input));
    }

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE", "AWS access key")]
    [InlineData("ASIAY34FZKBOKMUTVV7A", "STS session")]
    [InlineData("AROA1234567890ABCDEF", "AssumeRole id")]
    public void Redact_ReplacesAwsAccessKeyPatterns(string key, string label)
    {
        var redactor = new CodingAgentSecretRedactor(enabled: true);

        var output = redactor.Redact($"key=[{key}] note");

        Assert.Equal($"key=[{CodingAgentSecretRedactor.Placeholder}] note", output);
        _ = label;
    }

    [Fact]
    public void Redact_ReplacesGitHubTokens()
    {
        var redactor = new CodingAgentSecretRedactor(enabled: true);

        var output = redactor.Redact("token=ghp_AAAA1111BBBB2222CCCC3333DDDD4444EEEE done");

        Assert.Equal($"token={CodingAgentSecretRedactor.Placeholder} done", output);
    }

    [Fact]
    public void Redact_ReplacesAnthropicAndOpenAiKeys()
    {
        var redactor = new CodingAgentSecretRedactor(enabled: true);

        var anthropic = redactor.Redact("api=sk-ant-api03-EXAMPLE-SECRET-TOKEN-9999 ok");
        Assert.Equal($"api={CodingAgentSecretRedactor.Placeholder} ok", anthropic);

        var openai = redactor.Redact("api=sk-EXAMPLE_BASE_KEY_99999999 ok");
        Assert.Equal($"api={CodingAgentSecretRedactor.Placeholder} ok", openai);
    }

    [Fact]
    public void Redact_ReplacesSlackTokens()
    {
        var redactor = new CodingAgentSecretRedactor(enabled: true);
        var output = redactor.Redact("token=xoxb-1234567890-098765-ABCDEFabcdefGH stop");
        Assert.Equal($"token={CodingAgentSecretRedactor.Placeholder} stop", output);
    }

    [Fact]
    public void Redact_ReplacesBearerTokens()
    {
        var redactor = new CodingAgentSecretRedactor(enabled: true);
        var output = redactor.Redact("Authorization: Bearer abcdef1234567890abcdef1234567890");
        Assert.Equal($"Authorization: {CodingAgentSecretRedactor.Placeholder}", output);
    }

    [Fact]
    public void Redact_ReplacesJwtsInText()
    {
        var redactor = new CodingAgentSecretRedactor(enabled: true);
        var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        var output = redactor.Redact($"jwt={jwt} keep");
        Assert.Equal($"jwt={CodingAgentSecretRedactor.Placeholder} keep", output);
    }

    [Fact]
    public void Redact_LeavesNonSecretsIntact()
    {
        var redactor = new CodingAgentSecretRedactor(enabled: true);
        const string input = "Hello world. command: dotnet build path=C:/repo";
        Assert.Equal(input, redactor.Redact(input));
    }

    [Fact]
    public void IsDefaultEnabled_RespectsEnvironmentValues()
    {
        Assert.False(CodingAgentSecretRedactor.IsEnabledFromEnvironment("0"));
        Assert.False(CodingAgentSecretRedactor.IsEnabledFromEnvironment("false"));
        Assert.False(CodingAgentSecretRedactor.IsEnabledFromEnvironment("FALSE"));
        Assert.True(CodingAgentSecretRedactor.IsEnabledFromEnvironment("1"));
        Assert.True(CodingAgentSecretRedactor.IsEnabledFromEnvironment("true"));
        Assert.True(CodingAgentSecretRedactor.IsEnabledFromEnvironment(null));
        Assert.True(CodingAgentSecretRedactor.IsEnabledFromEnvironment(""));
    }
}
