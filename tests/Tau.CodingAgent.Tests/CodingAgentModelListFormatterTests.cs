using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentModelListFormatterTests
{
    [Fact]
    public void Format_RendersModelTable()
    {
        var output = CodingAgentModelListFormatter.Format(
            [
                new Model
                {
                    Provider = "google",
                    Id = "gemini-2.5-pro",
                    Name = "Gemini 2.5 Pro",
                    Api = "google-gemini",
                    ContextWindow = 1_048_576,
                    MaxOutputTokens = 65_536,
                    Reasoning = true,
                    InputModalities = ["text", "image"]
                },
                new Model
                {
                    Provider = "openai",
                    Id = "gpt-5.4",
                    Name = "GPT-5.4",
                    Api = "openai-responses",
                    ContextWindow = 128_000,
                    MaxOutputTokens = 16_384
                }
            ]);

        Assert.Contains("provider", output, StringComparison.Ordinal);
        Assert.Contains("model", output, StringComparison.Ordinal);
        Assert.Contains("context", output, StringComparison.Ordinal);
        Assert.Contains("max-out", output, StringComparison.Ordinal);
        Assert.Contains("google", output, StringComparison.Ordinal);
        Assert.Contains("gemini-2.5-pro", output, StringComparison.Ordinal);
        Assert.Contains("1M", output, StringComparison.Ordinal);
        Assert.Contains("65.5K", output, StringComparison.Ordinal);
        Assert.Contains("yes", output, StringComparison.Ordinal);
        Assert.Contains("openai", output, StringComparison.Ordinal);
        Assert.Contains("128K", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_FiltersWithFuzzySearch()
    {
        var output = CodingAgentModelListFormatter.Format(
            [
                new Model
                {
                    Provider = "google",
                    Id = "gemini-2.5-pro",
                    Name = "Gemini 2.5 Pro",
                    Api = "google-gemini"
                },
                new Model
                {
                    Provider = "openai",
                    Id = "gpt-5.4",
                    Name = "GPT-5.4",
                    Api = "openai-responses"
                }
            ],
            "gem");

        Assert.Contains("gemini-2.5-pro", output, StringComparison.Ordinal);
        Assert.DoesNotContain("gpt-5.4", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ReportsNoSearchMatches()
    {
        var output = CodingAgentModelListFormatter.Format(
            [
                new Model
                {
                    Provider = "openai",
                    Id = "gpt-5.4",
                    Name = "GPT-5.4",
                    Api = "openai-responses"
                }
            ],
            "zzz");

        Assert.Equal("No models matching \"zzz\"", output);
    }
}
