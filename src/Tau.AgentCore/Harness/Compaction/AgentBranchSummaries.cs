using System.Text.Json;
using Tau.AgentCore.Harness.Session;
using Tau.Ai;

namespace Tau.AgentCore.Harness;

public sealed record AgentBranchSummaryDetails(
    IReadOnlyList<string> ReadFiles,
    IReadOnlyList<string> ModifiedFiles);

public sealed record AgentBranchPreparation(
    IReadOnlyList<ChatMessage> Messages,
    AgentFileOperations FileOperations,
    int TotalTokens);

public sealed record AgentCollectedBranchEntries(
    IReadOnlyList<SessionTreeEntry> Entries,
    string? CommonAncestorId);

public static class AgentBranchSummaries
{
    public const string BranchSummaryPreamble = """
        The user explored a different conversation branch before returning here.
        Summary of that exploration:

        """;

    public const string BranchSummaryPrompt = """
        Create a structured summary of this conversation branch for context when returning later.

        Use this EXACT format:

        ## Goal
        [What was the user trying to accomplish in this branch?]

        ## Constraints & Preferences
        - [Any constraints, preferences, or requirements mentioned]
        - [Or "(none)" if none were mentioned]

        ## Progress
        ### Done
        - [x] [Completed tasks/changes]

        ### In Progress
        - [ ] [Work that was started but not finished]

        ### Blocked
        - [Issues preventing progress, if any]

        ## Key Decisions
        - **[Decision]**: [Brief rationale]

        ## Next Steps
        1. [What should happen next to continue this work]

        Keep each section concise. Preserve exact file paths, function names, and error messages.
        """;

    public static async Task<AgentCollectedBranchEntries> CollectEntriesForBranchSummaryAsync<TMetadata>(
        AgentHarnessSession<TMetadata> session,
        string? oldLeafId,
        string targetId,
        CancellationToken cancellationToken = default)
        where TMetadata : SessionMetadata
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        if (string.IsNullOrWhiteSpace(oldLeafId))
            return new AgentCollectedBranchEntries([], null);

        var oldPath = await session.GetBranchAsync(oldLeafId, cancellationToken).ConfigureAwait(false);
        var targetPath = await session.GetBranchAsync(targetId, cancellationToken).ConfigureAwait(false);
        var oldPathIds = oldPath.Select(static entry => entry.Id).ToHashSet(StringComparer.Ordinal);

        string? commonAncestorId = null;
        for (var i = targetPath.Count - 1; i >= 0; i--)
        {
            if (!oldPathIds.Contains(targetPath[i].Id))
                continue;

            commonAncestorId = targetPath[i].Id;
            break;
        }

        var entries = new List<SessionTreeEntry>();
        var currentId = oldLeafId;
        while (currentId is not null && currentId != commonAncestorId)
        {
            var entry = await session.GetEntryAsync(currentId, cancellationToken).ConfigureAwait(false);
            if (entry is null)
                throw new SessionException("invalid_session", $"Entry {currentId} not found");

            entries.Add(entry);
            currentId = entry.ParentId;
        }

        entries.Reverse();
        return new AgentCollectedBranchEntries(entries, commonAncestorId);
    }

    public static AgentBranchPreparation PrepareBranchEntries(
        IReadOnlyList<SessionTreeEntry> entries,
        int tokenBudget = 0)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var messages = new List<ChatMessage>();
        var fileOperations = AgentCompaction.CreateFileOperations();
        var totalTokens = 0;

        foreach (var entry in entries)
        {
            if (entry is not BranchSummarySessionEntry { FromHook: false, Details: { } detailsValue } ||
                !TryGetBranchSummaryDetails(detailsValue, out var details))
            {
                continue;
            }

            foreach (var file in details.ReadFiles)
                fileOperations.Read.Add(file);
            foreach (var file in details.ModifiedFiles)
                fileOperations.Edited.Add(file);
        }

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            var message = GetMessageFromEntry(entry);
            if (message is null)
                continue;

            AgentCompaction.ExtractFileOperationsFromMessage(message, fileOperations);

            var tokens = AgentCompaction.EstimateTokens(message);
            if (tokenBudget > 0 && totalTokens + tokens > tokenBudget)
            {
                if (entry is (CompactionSessionEntry or BranchSummarySessionEntry) &&
                    totalTokens < tokenBudget * 0.9)
                {
                    messages.Insert(0, message);
                    totalTokens += tokens;
                }

                break;
            }

            messages.Insert(0, message);
            totalTokens += tokens;
        }

        return new AgentBranchPreparation(messages.ToArray(), fileOperations, totalTokens);
    }

    public static string BuildBranchSummaryPrompt(
        AgentBranchPreparation preparation,
        string? customInstructions = null,
        bool replaceInstructions = false)
    {
        ArgumentNullException.ThrowIfNull(preparation);

        var llmMessages = AgentHarnessMessages.ConvertToLlm(preparation.Messages);
        var conversationText = AgentCompaction.SerializeConversation(llmMessages);
        var instructions = replaceInstructions && !string.IsNullOrWhiteSpace(customInstructions)
            ? customInstructions.Trim()
            : string.IsNullOrWhiteSpace(customInstructions)
                ? BranchSummaryPrompt
                : $"{BranchSummaryPrompt}\n\nAdditional focus: {customInstructions.Trim()}";

        return $"<conversation>\n{conversationText}\n</conversation>\n\n{instructions}";
    }

    public static string CompleteBranchSummaryText(string summary, AgentFileOperations fileOperations)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(fileOperations);

        var details = AgentCompaction.ComputeFileLists(fileOperations);
        return BranchSummaryPreamble +
            summary +
            AgentCompaction.FormatFileOperations(details.ReadFiles, details.ModifiedFiles);
    }

    private static ChatMessage? GetMessageFromEntry(SessionTreeEntry entry) =>
        entry switch
        {
            MessageSessionEntry { Message: ToolResultMessage } => null,
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

    private static bool TryGetBranchSummaryDetails(object value, out AgentBranchSummaryDetails details)
    {
        switch (value)
        {
            case AgentBranchSummaryDetails typed:
                details = typed;
                return true;
            case AgentCompactionDetails compactionDetails:
                details = new AgentBranchSummaryDetails(
                    compactionDetails.ReadFiles,
                    compactionDetails.ModifiedFiles);
                return true;
            case JsonElement element:
                return TryGetBranchSummaryDetails(element, out details);
            case string text when TryParseJsonObject(text, out var element):
                return TryGetBranchSummaryDetails(element, out details);
            default:
                details = new AgentBranchSummaryDetails([], []);
                return false;
        }
    }

    private static bool TryGetBranchSummaryDetails(JsonElement element, out AgentBranchSummaryDetails details)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            details = new AgentBranchSummaryDetails([], []);
            return false;
        }

        details = new AgentBranchSummaryDetails(
            ReadStringArrayProperty(element, "readFiles"),
            ReadStringArrayProperty(element, "modifiedFiles"));
        return details.ReadFiles.Count > 0 || details.ModifiedFiles.Count > 0;
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
}
