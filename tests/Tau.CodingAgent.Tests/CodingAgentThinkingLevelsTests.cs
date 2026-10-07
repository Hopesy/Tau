using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentThinkingLevelsTests
{
    /// <summary>【CodingAgent】【思考选择】显式等级表只显示允许等级，支持 max、循环及禁用等级钳制。</summary>
    [Fact]
    public void ExplicitThinkingMapControlsSelectionAndMaximum()
    {
        var model = new Model { Id = "virtual", Provider = "router", Name = "Auto", Api = "pi-virtual", Reasoning = true,
            ThinkingLevelMap = new Dictionary<string, string?> { ["off"] = "off", ["minimal"] = null, ["low"] = null,
                ["medium"] = null, ["high"] = "high", ["xhigh"] = null, ["max"] = "max" } };
        Assert.Equal(["off", "high", "max"], CodingAgentThinkingLevels.AvailableForModel(model));
        Assert.True(CodingAgentThinkingLevels.TryParse("max", out var maximum));
        Assert.Equal(ThinkingLevel.Max, maximum);
        Assert.Equal("max", CodingAgentThinkingLevels.Format(maximum));
        Assert.Equal(ThinkingLevel.High, CodingAgentThinkingLevels.CycleForModel(model, null));
        Assert.Equal(ThinkingLevel.Max, CodingAgentThinkingLevels.CycleForModel(model, ThinkingLevel.High));
        Assert.Null(CodingAgentThinkingLevels.CycleForModel(model, ThinkingLevel.Max));
        Assert.Equal(ThinkingLevel.Max, CodingAgentThinkingLevels.ClampForModel(model, ThinkingLevel.ExtraHigh));
        Assert.Equal(ThinkingLevel.High, CodingAgentThinkingLevels.ClampForModel(model, ThinkingLevel.Minimal));
    }

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
