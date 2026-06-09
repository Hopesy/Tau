using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentSessionStats(
    string Provider,
    string Model,
    int TotalMessages,
    int UserMessages,
    int AssistantMessages,
    int ToolResultMessages,
    int ToolCalls,
    int EstimatedTokens,
    int? ContextWindowTokens,
    string? SessionName,
    string? SessionFile)
{
    public CodingAgentSessionUsageTotals Tokens { get; init; } = CodingAgentSessionUsageTotals.Empty;
    public decimal Cost { get; init; }
    public int CostRecords { get; init; }

    public CodingAgentSessionStats WithUsage(CodingAgentSessionUsageSummary usage) =>
        this with
        {
            Tokens = usage.Tokens,
            Cost = usage.Cost,
            CostRecords = usage.CostRecords
        };
}

public sealed record CodingAgentSessionUsageTotals(
    int Input,
    int Output,
    int CacheRead,
    int CacheWrite)
{
    public static CodingAgentSessionUsageTotals Empty { get; } = new(0, 0, 0, 0);

    public int Total => Input + Output + CacheRead + CacheWrite;
}

public sealed record CodingAgentSessionUsageSummary(
    CodingAgentSessionUsageTotals Tokens,
    decimal Cost,
    int CostRecords)
{
    public static CodingAgentSessionUsageSummary Empty { get; } = new(CodingAgentSessionUsageTotals.Empty, 0m, 0);

    public static CodingAgentSessionUsageSummary FromMessages(IReadOnlyList<ChatMessage> messages)
    {
        var input = 0;
        var output = 0;
        var cacheRead = 0;
        var cacheWrite = 0;
        var cost = 0m;
        var costRecords = 0;

        foreach (var usage in messages.OfType<AssistantMessage>().Select(static message => message.Usage))
        {
            if (usage is not { } value)
            {
                continue;
            }

            input += value.InputTokens;
            output += value.OutputTokens;
            cacheRead += value.CacheReadTokens.GetValueOrDefault();
            cacheWrite += value.CacheWriteTokens.GetValueOrDefault();

            if (value.Cost is { } usageCost)
            {
                cost += usageCost.Total;
                costRecords++;
            }
        }

        return new CodingAgentSessionUsageSummary(
            new CodingAgentSessionUsageTotals(input, output, cacheRead, cacheWrite),
            cost,
            costRecords);
    }
}
