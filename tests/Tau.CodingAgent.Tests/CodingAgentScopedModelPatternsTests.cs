using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentScopedModelPatternsTests
{
    [Fact]
    public void TryResolve_PrefersExactModelReferenceBeforeThinkingSuffix()
    {
        var model = new Model
        {
            Provider = "openrouter",
            Id = "openai/gpt:exacto",
            Name = "OpenRouter Exacto",
            Api = "openai-compatible"
        };

        var resolved = CodingAgentScopedModelPatterns.TryResolve(
            "openrouter/openai/gpt:exacto",
            [model],
            out var entry,
            out var error);

        Assert.True(resolved);
        Assert.Null(error);
        Assert.Equal("openrouter/openai/gpt:exacto", entry.ModelId);
        Assert.Null(entry.ThinkingLevel);
    }

    [Fact]
    public void TryResolve_ParsesLastColonAsThinkingLevelWhenModelPrefixMatches()
    {
        var model = new Model
        {
            Provider = "openrouter",
            Id = "openai/gpt:exacto",
            Name = "OpenRouter Exacto",
            Api = "openai-compatible"
        };

        var resolved = CodingAgentScopedModelPatterns.TryResolve(
            "openrouter/openai/gpt:exacto:xhigh",
            [model],
            out var entry,
            out var error);

        Assert.True(resolved);
        Assert.Null(error);
        Assert.Equal("openrouter/openai/gpt:exacto", entry.ModelId);
        Assert.Equal("xhigh", entry.ThinkingLevel);
        Assert.Equal("openrouter/openai/gpt:exacto:xhigh", entry.Pattern);
    }
}
