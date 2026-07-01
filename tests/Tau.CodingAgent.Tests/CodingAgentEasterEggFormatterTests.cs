using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentEasterEggFormatterTests
{
    [Fact]
    public void FormatArminSaysHi_RendersXbmFinalFrameAndLabel()
    {
        var message = CodingAgentEasterEggFormatter.FormatArminSaysHi(width: 40);

        Assert.Equal(CodingAgentMessageDisplayFormatter.CustomKind, message.Kind);
        Assert.Contains("ARMIN SAYS HI", message.Text, StringComparison.Ordinal);
        Assert.Contains('█', message.Text);
        Assert.All(
            message.Text.Split(Environment.NewLine),
            line => Assert.True(line.Length <= 40, $"Line exceeded requested width: {line}"));
    }

    [Fact]
    public void FormatEarendilAnnouncement_RendersBlogLink()
    {
        var message = CodingAgentEasterEggFormatter.FormatEarendilAnnouncement(width: 72);

        Assert.Equal(CodingAgentMessageDisplayFormatter.CustomKind, message.Kind);
        Assert.Contains("pi has joined Earendil", message.Text, StringComparison.Ordinal);
        Assert.Contains("https://mariozechner.at/posts/2026-04-08-ive-sold-out/", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDaxnuts_RendersOpenCodeKimiText()
    {
        var message = CodingAgentEasterEggFormatter.FormatDaxnuts(width: 72);

        Assert.Equal(CodingAgentMessageDisplayFormatter.CustomKind, message.Kind);
        Assert.Contains("Free Kimi K2.5 via OpenCode Zen", message.Text, StringComparison.Ordinal);
        Assert.Contains("\"Powered by daxnuts\"", message.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("opencode", "kimi-k2.5-preview", true)]
    [InlineData("opencode", "KIMI-K2.5", true)]
    [InlineData("opencode", "qwen3-coder", false)]
    [InlineData("openai", "kimi-k2.5-preview", false)]
    public void TryFormatDaxnuts_MatchesUpstreamProviderAndModelRule(
        string provider,
        string modelId,
        bool expected)
    {
        var model = new Model
        {
            Provider = provider,
            Id = modelId,
            Name = modelId,
            Api = "openai-chat-completions",
            ContextWindow = 128_000
        };

        var actual = CodingAgentEasterEggFormatter.TryFormatDaxnuts(model, out var message);

        Assert.Equal(expected, actual);
        if (expected)
        {
            Assert.Contains("Powered by daxnuts", message.Text, StringComparison.Ordinal);
        }
    }
}
