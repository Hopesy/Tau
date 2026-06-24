using System.Text;
using Tau.Ai;

namespace Tau.AgentCore.Harness;

public sealed record AgentBashExecutionMessage(
    string Command,
    string Output,
    int? ExitCode,
    bool Cancelled,
    bool Truncated,
    string? FullOutputPath = null,
    DateTimeOffset? Timestamp = null,
    bool ExcludeFromContext = false) : ChatMessage("bashExecution");

public sealed record AgentCustomMessage(
    string CustomType,
    IReadOnlyList<ContentBlock> Content,
    bool Display,
    object? Details = null,
    DateTimeOffset? Timestamp = null) : ChatMessage("custom")
{
    public AgentCustomMessage(
        string customType,
        string content,
        bool display,
        object? details = null,
        DateTimeOffset? timestamp = null)
        : this(customType, [new TextContent(content)], display, details, timestamp)
    {
    }
}

public sealed record AgentBranchSummaryMessage(
    string Summary,
    string FromId,
    DateTimeOffset Timestamp) : ChatMessage("branchSummary");

public sealed record AgentCompactionSummaryMessage(
    string Summary,
    int TokensBefore,
    DateTimeOffset Timestamp) : ChatMessage("compactionSummary");

public static class AgentHarnessMessages
{
    public const string CompactionSummaryPrefix = """
        The conversation history before this point was compacted into the following summary:

        <summary>
        """;

    public const string CompactionSummarySuffix = """

        </summary>
        """;

    public const string BranchSummaryPrefix = """
        The following is a summary of a branch that this conversation came back from:

        <summary>
        """;

    public const string BranchSummarySuffix = """
        </summary>
        """;

    public static string BashExecutionToText(AgentBashExecutionMessage message)
    {
        var builder = new StringBuilder();
        builder.Append("Ran `").Append(message.Command).AppendLine("`");
        if (!string.IsNullOrEmpty(message.Output))
        {
            builder.Append("```").AppendLine();
            builder.Append(message.Output).AppendLine();
            builder.Append("```");
        }
        else
        {
            builder.Append("(no output)");
        }

        if (message.Cancelled)
        {
            builder.AppendLine().AppendLine().Append("(command cancelled)");
        }
        else if (message.ExitCode is not null and not 0)
        {
            builder.AppendLine().AppendLine().Append("Command exited with code ").Append(message.ExitCode.Value);
        }

        if (message.Truncated && !string.IsNullOrWhiteSpace(message.FullOutputPath))
        {
            builder.AppendLine().AppendLine().Append("[Output truncated. Full output: ")
                .Append(message.FullOutputPath)
                .Append(']');
        }

        return builder.ToString();
    }

    public static AgentBranchSummaryMessage CreateBranchSummaryMessage(
        string summary,
        string fromId,
        DateTimeOffset timestamp) =>
        new(summary, fromId, timestamp);

    public static AgentCompactionSummaryMessage CreateCompactionSummaryMessage(
        string summary,
        int tokensBefore,
        DateTimeOffset timestamp) =>
        new(summary, tokensBefore, timestamp);

    public static AgentCustomMessage CreateCustomMessage(
        string customType,
        string content,
        bool display,
        object? details,
        DateTimeOffset timestamp) =>
        new(customType, content, display, details, timestamp);

    public static AgentCustomMessage CreateCustomMessage(
        string customType,
        IReadOnlyList<ContentBlock> content,
        bool display,
        object? details,
        DateTimeOffset timestamp) =>
        new(customType, content.ToArray(), display, details, timestamp);

    public static IReadOnlyList<ChatMessage> ConvertToLlm(IEnumerable<ChatMessage> messages)
    {
        var converted = new List<ChatMessage>();
        foreach (var message in messages)
        {
            switch (message)
            {
                case AgentBashExecutionMessage { ExcludeFromContext: true }:
                    break;
                case AgentBashExecutionMessage bash:
                    converted.Add(new UserMessage(BashExecutionToText(bash)));
                    break;
                case AgentCustomMessage custom:
                    converted.Add(new UserMessage(custom.Content));
                    break;
                case AgentBranchSummaryMessage summary:
                    converted.Add(new UserMessage(BranchSummaryPrefix + summary.Summary + BranchSummarySuffix));
                    break;
                case AgentCompactionSummaryMessage summary:
                    converted.Add(new UserMessage(CompactionSummaryPrefix + summary.Summary + CompactionSummarySuffix));
                    break;
                case UserMessage:
                case AssistantMessage:
                case ToolResultMessage:
                    converted.Add(message);
                    break;
            }
        }

        return converted;
    }
}
