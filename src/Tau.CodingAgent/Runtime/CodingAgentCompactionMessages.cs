using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public static class CodingAgentCompactionMessages
{
    public const string SummaryPrefix = """
        The conversation history before this point was compacted into the following summary:

        <summary>
        """;

    public const string SummarySuffix = """
        </summary>
        """;

    public const string SystemPrompt = """
        You are compacting an ongoing coding-agent session.
        Produce a concise markdown summary that preserves:
        - the user's current goal
        - implementation decisions and constraints
        - relevant files, commands, and observed outcomes
        - unresolved issues and immediate next steps
        Do not invent facts. Keep the summary dense and operational.
        """;

    public const string Prompt = """
        Compact this coding-agent session for future continuation.
        Summarize only facts already present in the conversation.
        Focus on task state, code changes, runtime findings, risks, and next steps.
        """;

    public static UserMessage CreateSummaryMessage(string summary) =>
        new(CreateSummaryText(summary));

    public static string CreateSummaryText(string summary) =>
        $"{SummaryPrefix}\n{summary}\n{SummarySuffix}";

    public static bool IsSummaryMessage(ChatMessage message)
    {
        return message is UserMessage user &&
               user.Content.Count == 1 &&
               user.Content[0] is TextContent text &&
               text.Text.StartsWith(SummaryPrefix, StringComparison.Ordinal);
    }
}
