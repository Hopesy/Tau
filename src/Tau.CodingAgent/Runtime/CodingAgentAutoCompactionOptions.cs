using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentAutoCompactionOptions(int ThresholdTokens, string? Instructions = null)
{
    public static CodingAgentAutoCompactionOptions Disabled { get; } = new(0);

    public bool IsEnabled => ThresholdTokens > 0;

    public CodingAgentAutoCompactionOptions WithEnabledOverride(bool? enabled) =>
        enabled == false ? Disabled : this;

    public static CodingAgentAutoCompactionOptions FromEnvironment()
    {
        var thresholdValue = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_AUTO_COMPACT_TOKENS");
        if (!int.TryParse(thresholdValue, out var threshold) || threshold <= 0)
        {
            return Disabled;
        }

        var instructions = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_AUTO_COMPACT_INSTRUCTIONS");
        return new CodingAgentAutoCompactionOptions(
            threshold,
            string.IsNullOrWhiteSpace(instructions) ? null : instructions.Trim());
    }
}

public static class CodingAgentTokenEstimator
{
    public static int Estimate(IReadOnlyList<ChatMessage> messages, string? pendingInput = null)
    {
        var characters = messages.Sum(EstimateCharacters);
        if (!string.IsNullOrEmpty(pendingInput))
        {
            characters += pendingInput.Length;
        }

        return characters <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(characters / 4.0));
    }

    private static int EstimateCharacters(ChatMessage message)
    {
        return message switch
        {
            UserMessage user => user.Content.Sum(EstimateCharacters),
            AssistantMessage assistant => assistant.Content.Sum(EstimateCharacters),
            ToolResultMessage toolResult => toolResult.Content.Sum(EstimateCharacters),
            _ => message.Role.Length
        };
    }

    private static int EstimateCharacters(ContentBlock block)
    {
        return block switch
        {
            TextContent text => text.Text.Length,
            ThinkingContent thinking => thinking.Thinking.Length,
            ImageContent image => image.Data.Length,
            ToolCallContent toolCall => toolCall.Id.Length + toolCall.Name.Length + toolCall.Arguments.Length,
            _ => block.Type.Length
        };
    }
}
