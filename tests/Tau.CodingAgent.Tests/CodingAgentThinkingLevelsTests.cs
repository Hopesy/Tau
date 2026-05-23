using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentThinkingLevelsTests
{
    [Fact]
    public void ClampForModel_ReturnsOffForNonReasoningAndHighForNonXhighReasoning()
    {
        var nonReasoning = new Model
        {
            Provider = "openai",
            Id = "gpt-4.1",
            Name = "GPT-4.1",
            Api = "openai-responses"
        };
        var reasoningWithoutXhigh = new Model
        {
            Provider = "google",
            Id = "gemini-2.5-pro",
            Name = "Gemini 2.5 Pro",
            Api = "google-gemini",
            Reasoning = true
        };
        var xhighReasoning = new Model
        {
            Provider = "openai",
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-responses",
            Reasoning = true
        };

        Assert.Null(CodingAgentThinkingLevels.ClampForModel(nonReasoning, ThinkingLevel.High));
        Assert.Equal(ThinkingLevel.High, CodingAgentThinkingLevels.ClampForModel(reasoningWithoutXhigh, ThinkingLevel.ExtraHigh));
        Assert.Equal(ThinkingLevel.ExtraHigh, CodingAgentThinkingLevels.ClampForModel(xhighReasoning, ThinkingLevel.ExtraHigh));
    }

    [Fact]
    public void CycleForModel_SkipsUnavailableLevels()
    {
        var nonReasoning = new Model
        {
            Provider = "openai",
            Id = "gpt-4.1",
            Name = "GPT-4.1",
            Api = "openai-responses"
        };
        var reasoningWithoutXhigh = new Model
        {
            Provider = "google",
            Id = "gemini-2.5-pro",
            Name = "Gemini 2.5 Pro",
            Api = "google-gemini",
            Reasoning = true
        };
        var xhighReasoning = new Model
        {
            Provider = "openai",
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-responses",
            Reasoning = true
        };

        Assert.Null(CodingAgentThinkingLevels.CycleForModel(nonReasoning, null));
        Assert.Null(CodingAgentThinkingLevels.CycleForModel(reasoningWithoutXhigh, ThinkingLevel.High));
        Assert.Equal(ThinkingLevel.ExtraHigh, CodingAgentThinkingLevels.CycleForModel(xhighReasoning, ThinkingLevel.High));
    }
}
