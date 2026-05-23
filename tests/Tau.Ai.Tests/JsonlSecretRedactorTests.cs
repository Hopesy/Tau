using System.Text.Json;
using Tau.Ai;

namespace Tau.Ai.Tests;

public sealed class JsonlSecretRedactorTests
{
    [Fact]
    public void RedactLine_RedactsNestedStringValuesOnly()
    {
        var line = """
            {"sk-EXAMPLE_BASE_KEY_99999999":"key name stays","nested":{"token":"ghp_AAAA1111BBBB2222CCCC3333DDDD4444EEEE","items":["Authorization: Bearer abcdef1234567890abcdef1234567890",42,true,null]}}
            """;

        var redacted = JsonlSecretRedactor.RedactLine(line, new TauSecretRedactor(enabled: true));

        using var doc = JsonDocument.Parse(redacted);
        Assert.True(doc.RootElement.TryGetProperty("sk-EXAMPLE_BASE_KEY_99999999", out var keyProperty));
        Assert.Equal("key name stays", keyProperty.GetString());
        Assert.Equal(
            TauSecretRedactor.Placeholder,
            doc.RootElement.GetProperty("nested").GetProperty("token").GetString());
        Assert.Equal(
            $"Authorization: {TauSecretRedactor.Placeholder}",
            doc.RootElement.GetProperty("nested").GetProperty("items")[0].GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("nested").GetProperty("items")[1].GetInt32());
        Assert.True(doc.RootElement.GetProperty("nested").GetProperty("items")[2].GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("nested").GetProperty("items")[3].ValueKind);
    }

    [Fact]
    public void RedactLine_InvalidJsonFallsBackToLineRedaction()
    {
        var line = "not-json sk-EXAMPLE_BASE_KEY_99999999";

        var redacted = JsonlSecretRedactor.RedactLine(line, new TauSecretRedactor(enabled: true));

        Assert.Equal($"not-json {TauSecretRedactor.Placeholder}", redacted);
    }

    [Fact]
    public void RedactLine_DisabledRedactorLeavesLineUnchanged()
    {
        var line = """{"token":"sk-EXAMPLE_BASE_KEY_99999999"}""";

        var redacted = JsonlSecretRedactor.RedactLine(line, new TauSecretRedactor(enabled: false));

        Assert.Equal(line, redacted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RedactLine_BlankLineIsPreserved(string line)
    {
        Assert.Equal(line, JsonlSecretRedactor.RedactLine(line, new TauSecretRedactor(enabled: true)));
    }

    [Fact]
    public void RedactLines_UsesSameRedactorForEveryLine()
    {
        var lines = new[]
        {
            """{"token":"sk-EXAMPLE_BASE_KEY_99999999"}""",
            "invalid Bearer abcdef1234567890abcdef1234567890"
        };

        var redacted = JsonlSecretRedactor.RedactLines(lines, new TauSecretRedactor(enabled: true));

        Assert.Equal(2, redacted.Count);
        Assert.DoesNotContain("sk-EXAMPLE_BASE_KEY_99999999", redacted[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer abcdef1234567890abcdef1234567890", redacted[1], StringComparison.Ordinal);
    }
}
