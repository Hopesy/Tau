using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentRetryClassifierTests
{
    [Fact]
    public void IsContextOverflow_UsesAiLevelProviderPatternMatrix()
    {
        Assert.True(CodingAgentRetryClassifier.IsContextOverflow(
            "This endpoint's maximum context length is 8192 tokens. However, you requested about 12000 tokens"));
        Assert.True(CodingAgentRetryClassifier.IsContextOverflow(
            "prompt too long; exceeded max context length by 42 tokens"));
        Assert.False(CodingAgentRetryClassifier.IsContextOverflow(
            "Throttling error: Too many tokens, please wait before trying again."));
    }

    [Fact]
    public void IsRetryable_DoesNotRetryContextOverflowErrors()
    {
        Assert.False(CodingAgentRetryClassifier.IsRetryable(
            "prompt too long; exceeded max context length by 42 tokens",
            contextWindow: 128_000));
        Assert.True(CodingAgentRetryClassifier.IsRetryable(
            "429 rate limit",
            contextWindow: 128_000));
    }
}
