using System.Globalization;
using System.Text;
using System.Text.Json;
using Tau.AgentCore.Harness.Session;
using Tau.Ai;

namespace Tau.AgentCore.Harness;

public sealed record AgentCompactionDetails(
    IReadOnlyList<string> ReadFiles,
    IReadOnlyList<string> ModifiedFiles);

public sealed record AgentCompactionSettings(
    bool Enabled = true,
    int ReserveTokens = 16_384,
    int KeepRecentTokens = 20_000)
{
    public static AgentCompactionSettings Default { get; } = new();
}

public sealed record AgentContextUsageEstimate(
    int Tokens,
    int UsageTokens,
    int TrailingTokens,
    int? LastUsageIndex);

public sealed record AgentCutPointResult(
    int FirstKeptEntryIndex,
    int TurnStartIndex,
    bool IsSplitTurn);

public sealed record AgentCompactionPreparation(
    string FirstKeptEntryId,
    IReadOnlyList<ChatMessage> MessagesToSummarize,
    IReadOnlyList<ChatMessage> TurnPrefixMessages,
    bool IsSplitTurn,
    int TokensBefore,
    string? PreviousSummary,
    AgentFileOperations FileOperations,
    AgentCompactionSettings Settings);

public sealed class AgentFileOperations
{
    internal AgentFileOperations()
    {
    }

    public ISet<string> Read { get; } = new HashSet<string>(StringComparer.Ordinal);
    public ISet<string> Written { get; } = new HashSet<string>(StringComparer.Ordinal);
    public ISet<string> Edited { get; } = new HashSet<string>(StringComparer.Ordinal);
}

public static class AgentCompaction
{
    private const int EstimatedImageCharacters = 4_800;
    private const int ToolResultMaxCharacters = 2_000;

    public static AgentFileOperations CreateFileOperations() => new();

    public static void ExtractFileOperationsFromMessage(ChatMessage message, AgentFileOperations fileOperations)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(fileOperations);

        if (message is not AssistantMessage assistant)
            return;

        foreach (var toolCall in assistant.Content.OfType<ToolCallContent>())
        {
            if (!TryGetFileOperation(toolCall.Name, out var operation) ||
                !TryGetToolCallPath(toolCall.Arguments, out var path))
            {
                continue;
            }

            switch (operation)
            {
                case FileOperation.Read:
                    fileOperations.Read.Add(path);
                    break;
                case FileOperation.Write:
                    fileOperations.Written.Add(path);
                    break;
                case FileOperation.Edit:
                    fileOperations.Edited.Add(path);
                    break;
            }
        }
    }

    public static AgentCompactionDetails ComputeFileLists(AgentFileOperations fileOperations)
    {
        ArgumentNullException.ThrowIfNull(fileOperations);

        var modified = fileOperations.Edited
            .Concat(fileOperations.Written)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var modifiedSet = modified.ToHashSet(StringComparer.Ordinal);
        var readOnly = fileOperations.Read
            .Where(file => !modifiedSet.Contains(file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new AgentCompactionDetails(readOnly, modified);
    }

    public static string FormatFileOperations(IReadOnlyList<string> readFiles, IReadOnlyList<string> modifiedFiles)
    {
        ArgumentNullException.ThrowIfNull(readFiles);
        ArgumentNullException.ThrowIfNull(modifiedFiles);

        var sections = new List<string>();
        if (readFiles.Count > 0)
            sections.Add($"<read-files>\n{string.Join("\n", readFiles)}\n</read-files>");
        if (modifiedFiles.Count > 0)
            sections.Add($"<modified-files>\n{string.Join("\n", modifiedFiles)}\n</modified-files>");

        return sections.Count == 0
            ? string.Empty
            : "\n\n" + string.Join("\n\n", sections);
    }

    public static string SerializeConversation(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var parts = new List<string>();
        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                    {
                        var content = JoinText(user.Content);
                        if (content.Length > 0)
                            parts.Add($"[User]: {content}");
                        break;
                    }
                case AssistantMessage assistant:
                    {
                        var textParts = new List<string>();
                        var thinkingParts = new List<string>();
                        var toolCalls = new List<string>();
                        foreach (var block in assistant.Content)
                        {
                            switch (block)
                            {
                                case TextContent text:
                                    textParts.Add(text.Text);
                                    break;
                                case ThinkingContent thinking:
                                    thinkingParts.Add(thinking.Thinking);
                                    break;
                                case ToolCallContent toolCall:
                                    toolCalls.Add(FormatToolCall(toolCall));
                                    break;
                            }
                        }

                        if (thinkingParts.Count > 0)
                            parts.Add($"[Assistant thinking]: {string.Join("\n", thinkingParts)}");
                        if (textParts.Count > 0)
                            parts.Add($"[Assistant]: {string.Join("\n", textParts)}");
                        if (toolCalls.Count > 0)
                            parts.Add($"[Assistant tool calls]: {string.Join("; ", toolCalls)}");
                        break;
                    }
                case ToolResultMessage toolResult:
                    {
                        var content = JoinText(toolResult.Content);
                        if (content.Length > 0)
                            parts.Add($"[Tool result]: {TruncateForSummary(content, ToolResultMaxCharacters)}");
                        break;
                    }
            }
        }

        return string.Join("\n\n", parts);
    }

    public static int CalculateContextTokens(Usage usage) =>
        usage.InputTokens +
        usage.OutputTokens +
        (usage.CacheReadTokens ?? 0) +
        (usage.CacheWriteTokens ?? 0);

    public static Usage? GetLastAssistantUsage(IReadOnlyList<SessionTreeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is MessageSessionEntry { Message: { } message } &&
                GetAssistantUsage(message) is { } usage)
            {
                return usage;
            }
        }

        return null;
    }

    public static AgentContextUsageEstimate EstimateContextTokens(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var usageInfo = GetLastAssistantUsageInfo(messages);
        if (usageInfo is null)
        {
            var estimated = messages.Sum(EstimateTokens);
            return new AgentContextUsageEstimate(estimated, 0, estimated, null);
        }

        var usageTokens = CalculateContextTokens(usageInfo.Value.Usage);
        var trailingTokens = 0;
        for (var i = usageInfo.Value.Index + 1; i < messages.Count; i++)
            trailingTokens += EstimateTokens(messages[i]);

        return new AgentContextUsageEstimate(
            usageTokens + trailingTokens,
            usageTokens,
            trailingTokens,
            usageInfo.Value.Index);
    }

    public static bool ShouldCompact(
        int contextTokens,
        int contextWindow,
        AgentCompactionSettings? settings = null)
    {
        var resolved = settings ?? AgentCompactionSettings.Default;
        return resolved.Enabled && contextTokens > contextWindow - resolved.ReserveTokens;
    }

    public static int EstimateTokens(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var characters = message switch
        {
            UserMessage user => EstimateTextAndImageContentCharacters(user.Content),
            AssistantMessage assistant => EstimateAssistantCharacters(assistant),
            ToolResultMessage toolResult => EstimateTextAndImageContentCharacters(toolResult.Content),
            AgentCustomMessage custom => EstimateTextAndImageContentCharacters(custom.Content),
            AgentBashExecutionMessage bash => bash.Command.Length + bash.Output.Length,
            AgentBranchSummaryMessage summary => summary.Summary.Length,
            AgentCompactionSummaryMessage summary => summary.Summary.Length,
            _ => 0
        };

        return characters <= 0
            ? 0
            : (int)Math.Ceiling(characters / 4.0);
    }

    public static int FindTurnStartIndex(
        IReadOnlyList<SessionTreeEntry> entries,
        int entryIndex,
        int startIndex)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var start = Math.Clamp(startIndex, 0, entries.Count);
        var index = Math.Clamp(entryIndex, -1, entries.Count - 1);
        for (var i = index; i >= start; i--)
        {
            switch (entries[i])
            {
                case BranchSummarySessionEntry:
                case CustomMessageSessionEntry:
                    return i;
                case MessageSessionEntry { Message: UserMessage or AgentBashExecutionMessage }:
                    return i;
            }
        }

        return -1;
    }

    public static AgentCutPointResult FindCutPoint(
        IReadOnlyList<SessionTreeEntry> entries,
        int startIndex,
        int endIndex,
        int keepRecentTokens)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var start = Math.Clamp(startIndex, 0, entries.Count);
        var end = Math.Clamp(endIndex, start, entries.Count);
        var cutPoints = FindValidCutPoints(entries, start, end);
        if (cutPoints.Count == 0)
            return new AgentCutPointResult(start, -1, IsSplitTurn: false);

        var accumulatedTokens = 0;
        var cutIndex = cutPoints[0];
        for (var i = end - 1; i >= start; i--)
        {
            if (entries[i] is not MessageSessionEntry messageEntry)
                continue;

            accumulatedTokens += EstimateTokens(messageEntry.Message);
            if (accumulatedTokens < keepRecentTokens)
                continue;

            foreach (var cutPoint in cutPoints)
            {
                if (cutPoint < i)
                    continue;

                cutIndex = cutPoint;
                break;
            }

            break;
        }

        while (cutIndex > start)
        {
            var previousEntry = entries[cutIndex - 1];
            if (previousEntry is CompactionSessionEntry or MessageSessionEntry)
                break;

            cutIndex--;
        }

        var cutEntry = entries[cutIndex];
        var isUserMessage = cutEntry is MessageSessionEntry { Message: UserMessage };
        var turnStartIndex = isUserMessage ? -1 : FindTurnStartIndex(entries, cutIndex, start);

        return new AgentCutPointResult(
            cutIndex,
            turnStartIndex,
            !isUserMessage && turnStartIndex != -1);
    }

    public static AgentCompactionPreparation? PrepareCompaction(
        IReadOnlyList<SessionTreeEntry> pathEntries,
        AgentCompactionSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(pathEntries);

        if (pathEntries.Count == 0 || pathEntries[^1] is CompactionSessionEntry)
            return null;

        var resolvedSettings = settings ?? AgentCompactionSettings.Default;
        var previousCompactionIndex = -1;
        for (var i = pathEntries.Count - 1; i >= 0; i--)
        {
            if (pathEntries[i] is CompactionSessionEntry)
            {
                previousCompactionIndex = i;
                break;
            }
        }

        string? previousSummary = null;
        var boundaryStart = 0;
        if (previousCompactionIndex >= 0)
        {
            var previousCompaction = (CompactionSessionEntry)pathEntries[previousCompactionIndex];
            previousSummary = previousCompaction.Summary;
            var firstKeptEntryIndex = IndexOfEntry(pathEntries, previousCompaction.FirstKeptEntryId);
            boundaryStart = firstKeptEntryIndex >= 0
                ? firstKeptEntryIndex
                : previousCompactionIndex + 1;
        }

        var context = AgentHarnessSession<SessionMetadata>.BuildSessionContext(pathEntries);
        var tokensBefore = EstimateContextTokens(context.Messages).Tokens;
        var cutPoint = FindCutPoint(pathEntries, boundaryStart, pathEntries.Count, resolvedSettings.KeepRecentTokens);
        if (cutPoint.FirstKeptEntryIndex < 0 || cutPoint.FirstKeptEntryIndex >= pathEntries.Count)
        {
            throw new SessionException(
                "invalid_session",
                "First kept entry has no UUID - session may need migration");
        }

        var firstKeptEntry = pathEntries[cutPoint.FirstKeptEntryIndex];
        if (string.IsNullOrWhiteSpace(firstKeptEntry.Id))
        {
            throw new SessionException(
                "invalid_session",
                "First kept entry has no UUID - session may need migration");
        }

        var historyEnd = cutPoint.IsSplitTurn
            ? cutPoint.TurnStartIndex
            : cutPoint.FirstKeptEntryIndex;
        var messagesToSummarize = new List<ChatMessage>();
        for (var i = boundaryStart; i < historyEnd; i++)
        {
            if (GetMessageFromEntryForCompaction(pathEntries[i]) is { } message)
                messagesToSummarize.Add(message);
        }

        var turnPrefixMessages = new List<ChatMessage>();
        if (cutPoint.IsSplitTurn)
        {
            for (var i = cutPoint.TurnStartIndex; i < cutPoint.FirstKeptEntryIndex; i++)
            {
                if (GetMessageFromEntryForCompaction(pathEntries[i]) is { } message)
                    turnPrefixMessages.Add(message);
            }
        }

        var fileOperations = ExtractFileOperations(
            messagesToSummarize,
            pathEntries,
            previousCompactionIndex);
        if (cutPoint.IsSplitTurn)
        {
            foreach (var message in turnPrefixMessages)
                ExtractFileOperationsFromMessage(message, fileOperations);
        }

        return new AgentCompactionPreparation(
            firstKeptEntry.Id,
            messagesToSummarize.ToArray(),
            turnPrefixMessages.ToArray(),
            cutPoint.IsSplitTurn,
            tokensBefore,
            previousSummary,
            fileOperations,
            resolvedSettings);
    }

    private static AgentFileOperations ExtractFileOperations(
        IEnumerable<ChatMessage> messages,
        IReadOnlyList<SessionTreeEntry> entries,
        int previousCompactionIndex)
    {
        var fileOperations = CreateFileOperations();
        if (previousCompactionIndex >= 0 &&
            entries[previousCompactionIndex] is CompactionSessionEntry { FromHook: false } previousCompaction &&
            TryGetCompactionDetails(previousCompaction.Details, out var details))
        {
            foreach (var file in details.ReadFiles)
                fileOperations.Read.Add(file);
            foreach (var file in details.ModifiedFiles)
                fileOperations.Edited.Add(file);
        }

        foreach (var message in messages)
            ExtractFileOperationsFromMessage(message, fileOperations);

        return fileOperations;
    }

    private static ChatMessage? GetMessageFromEntryForCompaction(SessionTreeEntry entry) =>
        entry is CompactionSessionEntry
            ? null
            : GetMessageFromEntry(entry);

    private static ChatMessage? GetMessageFromEntry(SessionTreeEntry entry) =>
        entry switch
        {
            MessageSessionEntry message => message.Message,
            CustomMessageSessionEntry custom => AgentHarnessMessages.CreateCustomMessage(
                custom.CustomType,
                custom.Content,
                custom.Display,
                custom.Details,
                custom.Timestamp),
            BranchSummarySessionEntry summary => AgentHarnessMessages.CreateBranchSummaryMessage(
                summary.Summary,
                summary.FromId,
                summary.Timestamp),
            CompactionSessionEntry compaction => AgentHarnessMessages.CreateCompactionSummaryMessage(
                compaction.Summary,
                compaction.TokensBefore,
                compaction.Timestamp),
            _ => null
        };

    private static List<int> FindValidCutPoints(
        IReadOnlyList<SessionTreeEntry> entries,
        int startIndex,
        int endIndex)
    {
        var cutPoints = new List<int>();
        for (var i = startIndex; i < endIndex; i++)
        {
            switch (entries[i])
            {
                case MessageSessionEntry { Message: UserMessage or AssistantMessage or AgentBashExecutionMessage or AgentCustomMessage or AgentBranchSummaryMessage or AgentCompactionSummaryMessage }:
                case BranchSummarySessionEntry:
                case CustomMessageSessionEntry:
                    cutPoints.Add(i);
                    break;
            }
        }

        return cutPoints;
    }

    private static int EstimateTextAndImageContentCharacters(IReadOnlyList<ContentBlock> content)
    {
        var characters = 0;
        foreach (var block in content)
        {
            characters += block switch
            {
                TextContent text => text.Text.Length,
                ImageContent => EstimatedImageCharacters,
                _ => 0
            };
        }

        return characters;
    }

    private static int EstimateAssistantCharacters(AssistantMessage assistant)
    {
        var characters = 0;
        foreach (var block in assistant.Content)
        {
            characters += block switch
            {
                TextContent text => text.Text.Length,
                ThinkingContent thinking => thinking.Thinking.Length,
                ToolCallContent toolCall => toolCall.Name.Length + toolCall.Arguments.Length,
                _ => 0
            };
        }

        return characters;
    }

    private static Usage? GetAssistantUsage(ChatMessage message)
    {
        if (message is not AssistantMessage assistant ||
            assistant.StopReason is StopReason.Aborted or StopReason.Error ||
            assistant.Usage is not { } usage ||
            CalculateContextTokens(usage) <= 0)
        {
            return null;
        }

        return usage;
    }

    private static (Usage Usage, int Index)? GetLastAssistantUsageInfo(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (GetAssistantUsage(messages[i]) is { } usage)
                return (usage, i);
        }

        return null;
    }

    private static bool TryGetFileOperation(string toolName, out FileOperation operation)
    {
        operation = toolName switch
        {
            "read" or "read_file" => FileOperation.Read,
            "write" or "write_file" => FileOperation.Write,
            "edit" or "edit_file" => FileOperation.Edit,
            _ => (FileOperation)(-1)
        };

        return operation != (FileOperation)(-1);
    }

    private static bool TryGetToolCallPath(string arguments, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(arguments))
            return false;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetStringProperty(document.RootElement, out path, "path", "file_path"))
            {
                return false;
            }

            path = path.Trim();
            return path.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetStringProperty(JsonElement root, out string value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString() ?? string.Empty;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetCompactionDetails(object? value, out AgentCompactionDetails details)
    {
        switch (value)
        {
            case AgentCompactionDetails typed:
                details = typed;
                return true;
            case JsonElement element:
                return TryGetCompactionDetails(element, out details);
            case string text when TryParseJsonObject(text, out var element):
                return TryGetCompactionDetails(element, out details);
            default:
                details = new AgentCompactionDetails([], []);
                return false;
        }
    }

    private static bool TryParseJsonObject(string text, out JsonElement element)
    {
        element = default;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetCompactionDetails(JsonElement element, out AgentCompactionDetails details)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            details = new AgentCompactionDetails([], []);
            return false;
        }

        details = new AgentCompactionDetails(
            ReadStringArrayProperty(element, "readFiles"),
            ReadStringArrayProperty(element, "modifiedFiles"));
        return details.ReadFiles.Count > 0 || details.ModifiedFiles.Count > 0;
    }

    private static IReadOnlyList<string> ReadStringArrayProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var value = item.GetString()?.Trim();
            if (!string.IsNullOrEmpty(value))
                values.Add(value);
        }

        return values.ToArray();
    }

    private static string JoinText(IEnumerable<ContentBlock> content) =>
        string.Concat(content.OfType<TextContent>().Select(static text => text.Text));

    private static string FormatToolCall(ToolCallContent toolCall)
    {
        var arguments = FormatToolCallArguments(toolCall.Arguments);
        return arguments.Length == 0
            ? $"{toolCall.Name}()"
            : $"{toolCall.Name}({arguments})";
    }

    private static string FormatToolCallArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return "arguments=" + QuoteJsonString(arguments);

            var pairs = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
                pairs.Add($"{property.Name}={property.Value.GetRawText()}");

            return string.Join(", ", pairs);
        }
        catch (JsonException)
        {
            return "arguments=" + QuoteJsonString(arguments);
        }
    }

    private static string TruncateForSummary(string text, int maxCharacters)
    {
        if (text.Length <= maxCharacters)
            return text;

        var truncatedCharacters = text.Length - maxCharacters;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{text[..maxCharacters]}\n\n[... {truncatedCharacters} more characters truncated]");
    }

    private static int IndexOfEntry(IReadOnlyList<SessionTreeEntry> entries, string id)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Id == id)
                return i;
        }

        return -1;
    }

    private static string QuoteJsonString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (ch < ' ')
                    {
                        builder.Append("\\u");
                        builder.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private enum FileOperation
    {
        Read,
        Write,
        Edit
    }
}
