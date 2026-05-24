using System.Text.Json;
using Tau.Ai;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

public static class CodingAgentJsonlSessionPreviewer
{
    private const int PreviewTextLimit = 160;

    public static CodingAgentJsonlSessionPreviewDto ParseFile(string path, TauSecretRedactor? redactor = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("CodingAgent JSONL path is required.", nameof(path));
        }

        return Parse(File.ReadAllText(path), path, redactor);
    }

    public static CodingAgentJsonlSessionPreviewDto ParseFile(
        string path,
        CodingAgentJsonlPreviewOptions options,
        TauSecretRedactor? redactor = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("CodingAgent JSONL path is required.", nameof(path));
        }

        return Parse(File.ReadAllText(path), path, options, redactor);
    }

    public static CodingAgentJsonlSessionPreviewDto Parse(
        string jsonl,
        string? filePath = null,
        TauSecretRedactor? redactor = null) =>
        Parse(jsonl, filePath, CodingAgentJsonlPreviewOptions.Default, redactor);

    public static CodingAgentJsonlSessionPreviewDto Parse(
        string jsonl,
        string? filePath,
        CodingAgentJsonlPreviewOptions options,
        TauSecretRedactor? redactor = null)
    {
        if (string.IsNullOrWhiteSpace(jsonl))
        {
            throw new CodingAgentJsonlPreviewException(
                "missing_session_header",
                "CodingAgent JSONL session header is required.");
        }

        using var reader = new StringReader(jsonl);
        CodingAgentJsonlSessionHeader? header = null;
        var entries = new List<CodingAgentJsonlSessionEntry>();
        var messages = new List<CodingAgentJsonlTimelineMessageDto>();
        var seenEntryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryCount = 0;
        var lineNumber = 0;
        redactor ??= TauSecretRedactor.ForEnvironmentVariable(TauSecretRedactor.WebUiEnvironmentVariable);

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (lineNumber == 1)
            {
                line = line.TrimStart('\uFEFF');
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                throw new CodingAgentJsonlPreviewException(
                    "empty_line",
                    $"CodingAgent JSONL line {lineNumber} is empty.",
                    lineNumber);
            }

            var redactedLine = JsonlSecretRedactor.RedactLine(line, redactor);
            var type = ReadType(redactedLine, lineNumber);
            if (lineNumber == 1)
            {
                if (!string.Equals(type, "session", StringComparison.Ordinal))
                {
                    throw new CodingAgentJsonlPreviewException(
                        "missing_session_header",
                        "First CodingAgent JSONL line must be a session header.",
                        lineNumber);
                }

                header = Deserialize(redactedLine, WebUiCodingAgentJsonlContext.Default.CodingAgentJsonlSessionHeader, lineNumber);
                ValidateHeader(header, lineNumber);
                continue;
            }

            var entry = Deserialize(redactedLine, WebUiCodingAgentJsonlContext.Default.CodingAgentJsonlSessionEntry, lineNumber);
            ValidateEntry(entry, seenEntryIds, lineNumber);
            entries.Add(entry);
            entryCount++;

            if (string.Equals(entry.Type, "message", StringComparison.Ordinal))
            {
                if (entry.Message is null)
                {
                    throw new CodingAgentJsonlPreviewException(
                        "missing_message",
                        $"CodingAgent JSONL line {lineNumber} is a message entry without a message payload.",
                        lineNumber);
                }

                messages.Add(CreateMessagePreview(entry, lineNumber));
            }
        }

        if (header is null)
        {
            throw new CodingAgentJsonlPreviewException(
                "missing_session_header",
                "CodingAgent JSONL session header is required.");
        }

        var tree = CreateTreeMetadata(entries);
        var audit = CreateImportAudit(entries, messages, tree);
        var filteredMessages = FilterMessages(messages, tree, options);
        var filter = CreateFilterMetadata(messages, filteredMessages, options);
        return new CodingAgentJsonlSessionPreviewDto(
            header.Id,
            header.Version,
            header.Timestamp,
            header.Cwd,
            header.ParentSession,
            filePath,
            entryCount,
            messages.Count,
            filter,
            tree,
            audit,
            filteredMessages);
    }

    private static string ReadType(string line, int lineNumber)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(typeElement.GetString()))
            {
                throw new CodingAgentJsonlPreviewException(
                    "missing_type",
                    $"CodingAgent JSONL line {lineNumber} is missing a type field.",
                    lineNumber);
            }

            return typeElement.GetString()!;
        }
        catch (JsonException ex)
        {
            throw new CodingAgentJsonlPreviewException(
                "invalid_json",
                $"CodingAgent JSONL line {lineNumber} is not valid JSON.",
                lineNumber,
                ex);
        }
    }

    private static T Deserialize<T>(string line, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo, int lineNumber)
    {
        try
        {
            return JsonSerializer.Deserialize(line, jsonTypeInfo) ??
                throw new CodingAgentJsonlPreviewException(
                    "invalid_coding_agent_jsonl",
                    $"CodingAgent JSONL line {lineNumber} could not be deserialized.",
                    lineNumber);
        }
        catch (JsonException ex)
        {
            throw new CodingAgentJsonlPreviewException(
                "invalid_coding_agent_jsonl",
                $"CodingAgent JSONL line {lineNumber} is not valid CodingAgent JSONL.",
                lineNumber,
                ex);
        }
    }

    private static void ValidateHeader(CodingAgentJsonlSessionHeader header, int lineNumber)
    {
        if (!string.Equals(header.Type, "session", StringComparison.Ordinal))
        {
            throw new CodingAgentJsonlPreviewException(
                "invalid_session_header",
                "CodingAgent JSONL header type must be 'session'.",
                lineNumber);
        }

        if (header.Version <= 0)
        {
            throw new CodingAgentJsonlPreviewException(
                "unsupported_version",
                $"Unsupported CodingAgent JSONL version '{header.Version}'.",
                lineNumber);
        }

        RequireText(header.Id, "session id", lineNumber);
        RequireText(header.Cwd, "session cwd", lineNumber);
    }

    private static void ValidateEntry(
        CodingAgentJsonlSessionEntry entry,
        HashSet<string> seenEntryIds,
        int lineNumber)
    {
        RequireText(entry.Type, $"entry type at line {lineNumber}", lineNumber);
        RequireText(entry.Id, $"entry id at line {lineNumber}", lineNumber);

        if (!seenEntryIds.Add(entry.Id))
        {
            throw new CodingAgentJsonlPreviewException(
                "duplicate_entry_id",
                $"CodingAgent JSONL line {lineNumber} has duplicate entry id '{entry.Id}'.",
                lineNumber);
        }
    }

    private static CodingAgentJsonlTimelineMessageDto CreateMessagePreview(
        CodingAgentJsonlSessionEntry entry,
        int lineNumber)
    {
        var message = entry.Message!;
        RequireText(message.Role, $"message role at line {lineNumber}", lineNumber);

        IReadOnlyList<CodingAgentJsonlSessionContent> content = message.Content ?? [];
        var text = BuildText(content);
        return new CodingAgentJsonlTimelineMessageDto(
            entry.Id,
            entry.ParentId,
            entry.Timestamp,
            message.Role,
            PreviewText(text),
            text.Length,
            content.Count,
            content.Any(static item => string.Equals(item.Type, "thinking", StringComparison.Ordinal)),
            content.Count(static item => string.Equals(item.Type, "toolCall", StringComparison.Ordinal)),
            content.Count(static item => string.Equals(item.Type, "image", StringComparison.Ordinal)),
            message.ToolCallId,
            string.Equals(message.Role, "toolResult", StringComparison.Ordinal) ? message.IsError : null);
    }

    private static CodingAgentJsonlTreeMetadataDto CreateTreeMetadata(IReadOnlyList<CodingAgentJsonlSessionEntry> entries)
    {
        var byId = entries.ToDictionary(static entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        var childCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.ParentId) &&
                !string.Equals(entry.ParentId, entry.Id, StringComparison.OrdinalIgnoreCase) &&
                byId.ContainsKey(entry.ParentId))
            {
                childCounts[entry.ParentId] = childCounts.TryGetValue(entry.ParentId, out var count) ? count + 1 : 1;
            }
        }

        var labels = BuildLabels(entries);
        var leafId = entries.Count == 0 ? null : entries[^1].Id;
        var branchEntryIds = GetBranchEntryIds(leafId, byId);
        var branchSet = branchEntryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entryTypes = entries
            .GroupBy(static entry => entry.Type, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

        var metadata = entries
            .Select(entry =>
            {
                labels.TryGetValue(entry.Id, out var label);
                return new CodingAgentJsonlEntryMetadataDto(
                    entry.Id,
                    entry.Type,
                    entry.ParentId,
                    entry.Timestamp,
                    GetDepth(entry, byId),
                    childCounts.TryGetValue(entry.Id, out var childCount) ? childCount : 0,
                    string.Equals(entry.Id, leafId, StringComparison.Ordinal),
                    branchSet.Contains(entry.Id),
                    label?.Label,
                    label?.Timestamp);
            })
            .ToArray();

        return new CodingAgentJsonlTreeMetadataDto(
            leafId,
            entries.Count(entry => IsRootEntry(entry, byId)),
            childCounts.Count(static item => item.Value > 1),
            branchEntryIds.Count,
            branchEntryIds.Count(entryId =>
                byId.TryGetValue(entryId, out var entry) &&
                string.Equals(entry.Type, "message", StringComparison.Ordinal)),
            labels.Count,
            entryTypes,
            branchEntryIds,
            metadata);
    }

    private static CodingAgentJsonlImportAuditDto CreateImportAudit(
        IReadOnlyList<CodingAgentJsonlSessionEntry> entries,
        IReadOnlyList<CodingAgentJsonlTimelineMessageDto> messages,
        CodingAgentJsonlTreeMetadataDto tree)
    {
        var byId = entries.ToDictionary(static entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        var messageEntryIds = messages
            .Select(static message => message.EntryId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var messagesByEntryId = messages.ToDictionary(static message => message.EntryId, StringComparer.OrdinalIgnoreCase);
        var currentBranchSet = tree.CurrentBranchEntryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var treeMetadataByEntryId = tree.Entries.ToDictionary(static entry => entry.EntryId, StringComparer.OrdinalIgnoreCase);

        var currentBranchTimeline = tree.CurrentBranchEntryIds
            .Where(byId.ContainsKey)
            .Select(entryId =>
            {
                var entry = byId[entryId];
                treeMetadataByEntryId.TryGetValue(entryId, out var metadata);
                messagesByEntryId.TryGetValue(entryId, out var message);
                return CreateBranchTimelineEntry(entry, metadata, message);
            })
            .ToArray();

        var branchLabels = tree.Entries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Label))
            .Select(static entry => new CodingAgentJsonlBranchLabelDto(
                entry.EntryId,
                entry.Label!,
                entry.LabelTimestamp ?? entry.Timestamp,
                entry.IsOnCurrentBranch))
            .ToArray();

        var nonImportedEntryCount = entries.Count(entry => !messageEntryIds.Contains(entry.Id));
        var offBranchMessageCount = messages.Count(message => !currentBranchSet.Contains(message.EntryId));
        var isBranched = tree.BranchPointCount > 0 || tree.RootEntryCount > 1 || offBranchMessageCount > 0;
        var warnings = CreateAuditWarnings(entries, isBranched, offBranchMessageCount, nonImportedEntryCount);

        return new CodingAgentJsonlImportAuditDto(
            isBranched,
            WillImportTimelineMessagesOnly: true,
            WillImportCurrentBranchOnly: false,
            messages.Count,
            nonImportedEntryCount,
            tree.BranchMessageCount,
            offBranchMessageCount,
            currentBranchTimeline,
            branchLabels,
            warnings);
    }

    private static CodingAgentJsonlBranchTimelineEntryDto CreateBranchTimelineEntry(
        CodingAgentJsonlSessionEntry entry,
        CodingAgentJsonlEntryMetadataDto? metadata,
        CodingAgentJsonlTimelineMessageDto? message)
    {
        return new CodingAgentJsonlBranchTimelineEntryDto(
            entry.Id,
            entry.Type,
            entry.Timestamp,
            message?.Role,
            message?.TextPreview ?? CreateEntryAuditText(entry),
            metadata?.Label,
            metadata?.IsCurrentLeaf ?? false,
            message is not null);
    }

    private static IReadOnlyList<CodingAgentJsonlAuditWarningDto> CreateAuditWarnings(
        IReadOnlyList<CodingAgentJsonlSessionEntry> entries,
        bool isBranched,
        int offBranchMessageCount,
        int nonImportedEntryCount)
    {
        var warnings = new List<CodingAgentJsonlAuditWarningDto>();
        if (isBranched)
        {
            warnings.Add(new CodingAgentJsonlAuditWarningDto(
                "branch_tree_not_persisted",
                "Source CodingAgent JSONL contains branch/tree structure; WebUi keeps it only in preview/import audit metadata."));
        }

        if (offBranchMessageCount > 0)
        {
            warnings.Add(new CodingAgentJsonlAuditWarningDto(
                "off_branch_messages_in_timeline",
                "Conservative import currently imports all timeline messages, including messages outside the current branch."));
        }

        if (nonImportedEntryCount > 0)
        {
            var firstNonMessageEntryId = entries
                .FirstOrDefault(static entry => !string.Equals(entry.Type, "message", StringComparison.Ordinal))
                ?.Id;
            warnings.Add(new CodingAgentJsonlAuditWarningDto(
                "non_message_entries_not_imported_as_messages",
                "Non-message CodingAgent entries are not imported as WebChat messages.",
                firstNonMessageEntryId));
        }

        warnings.Add(new CodingAgentJsonlAuditWarningDto(
            "webchat_import_is_linearized",
            "CodingAgent JSONL import is linearized into WebChat session messages; source tree metadata is returned as audit data only."));

        return warnings;
    }

    private static IReadOnlyList<CodingAgentJsonlTimelineMessageDto> FilterMessages(
        IReadOnlyList<CodingAgentJsonlTimelineMessageDto> messages,
        CodingAgentJsonlTreeMetadataDto tree,
        CodingAgentJsonlPreviewOptions options)
    {
        if (!options.CurrentBranchOnly && string.IsNullOrWhiteSpace(options.Search))
        {
            return messages;
        }

        var currentBranchSet = tree.CurrentBranchEntryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var query = options.Search?.Trim();
        return messages
            .Where(message => !options.CurrentBranchOnly || currentBranchSet.Contains(message.EntryId))
            .Where(message => string.IsNullOrWhiteSpace(query) || MatchesSearch(message, query))
            .ToArray();
    }

    private static CodingAgentJsonlPreviewFilterDto CreateFilterMetadata(
        IReadOnlyList<CodingAgentJsonlTimelineMessageDto> messages,
        IReadOnlyList<CodingAgentJsonlTimelineMessageDto> filteredMessages,
        CodingAgentJsonlPreviewOptions options)
    {
        var search = string.IsNullOrWhiteSpace(options.Search) ? null : options.Search.Trim();
        return new CodingAgentJsonlPreviewFilterDto(
            search,
            options.CurrentBranchOnly,
            messages.Count,
            filteredMessages.Count,
            filteredMessages.Select(static message => message.EntryId).ToArray());
    }

    private static bool MatchesSearch(CodingAgentJsonlTimelineMessageDto message, string query) =>
        message.EntryId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        message.Role.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        message.TextPreview.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(message.ToolCallId) &&
            message.ToolCallId.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static string? CreateEntryAuditText(CodingAgentJsonlSessionEntry entry)
    {
        return entry.Type switch
        {
            "branch_summary" => PreviewOptional(entry.Summary),
            "compaction" => PreviewOptional(CreateCompactionAuditText(entry)),
            "label" => PreviewOptional(CreateLabelAuditText(entry)),
            "model_change" => PreviewOptional(CreateModelChangeAuditText(entry)),
            "thinking_level_change" => PreviewOptional(entry.ThinkingLevel),
            "session_info" => PreviewOptional(entry.Name),
            _ => null
        };
    }

    private static string? CreateCompactionAuditText(CodingAgentJsonlSessionEntry entry)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Summary))
        {
            parts.Add(entry.Summary.Trim());
        }

        if (!string.IsNullOrWhiteSpace(entry.FirstKeptEntryId))
        {
            parts.Add($"first kept: {entry.FirstKeptEntryId.Trim()}");
        }

        if (entry.TokensBefore is not null)
        {
            parts.Add($"tokens before: {entry.TokensBefore.Value}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string? CreateLabelAuditText(CodingAgentJsonlSessionEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.TargetId))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(entry.Label)
            ? $"label cleared: {entry.TargetId.Trim()}"
            : $"label {entry.TargetId.Trim()}: {entry.Label.Trim()}";
    }

    private static string? CreateModelChangeAuditText(CodingAgentJsonlSessionEntry entry)
    {
        var model = string.IsNullOrWhiteSpace(entry.ModelId) ? entry.Model : entry.ModelId;
        if (string.IsNullOrWhiteSpace(entry.Provider) && string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(entry.Provider))
        {
            return model!.Trim();
        }

        return string.IsNullOrWhiteSpace(model)
            ? entry.Provider.Trim()
            : $"{entry.Provider.Trim()}/{model.Trim()}";
    }

    private static Dictionary<string, CodingAgentJsonlLabelState> BuildLabels(IReadOnlyList<CodingAgentJsonlSessionEntry> entries)
    {
        var labels = new Dictionary<string, CodingAgentJsonlLabelState>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Type, "label", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(entry.TargetId))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Label))
            {
                labels.Remove(entry.TargetId);
                continue;
            }

            labels[entry.TargetId] = new CodingAgentJsonlLabelState(entry.Label.Trim(), entry.Timestamp);
        }

        return labels;
    }

    private static IReadOnlyList<string> GetBranchEntryIds(
        string? leafId,
        IReadOnlyDictionary<string, CodingAgentJsonlSessionEntry> byId)
    {
        if (string.IsNullOrWhiteSpace(leafId) || !byId.TryGetValue(leafId, out var current))
        {
            return [];
        }

        var path = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (current is not null)
        {
            if (!seen.Add(current.Id))
            {
                break;
            }

            path.Add(current.Id);
            current = !string.IsNullOrWhiteSpace(current.ParentId) && byId.TryGetValue(current.ParentId, out var parent)
                ? parent
                : null;
        }

        path.Reverse();
        return path;
    }

    private static int GetDepth(
        CodingAgentJsonlSessionEntry entry,
        IReadOnlyDictionary<string, CodingAgentJsonlSessionEntry> byId)
    {
        var depth = 0;
        var current = entry;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry.Id };
        while (!string.IsNullOrWhiteSpace(current.ParentId) &&
            byId.TryGetValue(current.ParentId, out var parent) &&
            seen.Add(parent.Id))
        {
            depth++;
            current = parent;
        }

        return depth;
    }

    private static bool IsRootEntry(
        CodingAgentJsonlSessionEntry entry,
        IReadOnlyDictionary<string, CodingAgentJsonlSessionEntry> byId) =>
        string.IsNullOrWhiteSpace(entry.ParentId) ||
        string.Equals(entry.ParentId, entry.Id, StringComparison.OrdinalIgnoreCase) ||
        !byId.ContainsKey(entry.ParentId);

    private static string BuildText(IReadOnlyList<CodingAgentJsonlSessionContent> content)
    {
        var parts = content
            .Where(static item => string.Equals(item.Type, "text", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.Text))
            .Select(static item => item.Text!.Trim())
            .ToArray();
        return parts.Length == 0 ? string.Empty : string.Join("\n\n", parts);
    }

    private static string PreviewText(string text)
    {
        if (text.Length <= PreviewTextLimit)
        {
            return text;
        }

        return text[..PreviewTextLimit] + "...";
    }

    private static string? PreviewOptional(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return PreviewText(text.Trim());
    }

    private static void RequireText(string? value, string fieldName, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CodingAgentJsonlPreviewException(
                "missing_field",
                $"CodingAgent JSONL is missing {fieldName}.",
                lineNumber);
        }
    }
}

public sealed class CodingAgentJsonlPreviewException : Exception
{
    public CodingAgentJsonlPreviewException(
        string code,
        string message,
        int? lineNumber = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        LineNumber = lineNumber;
    }

    public string Code { get; }

    public int? LineNumber { get; }
}

internal sealed class CodingAgentJsonlSessionHeader
{
    public string Type { get; init; } = string.Empty;
    public int Version { get; init; }
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public string Cwd { get; init; } = string.Empty;
    public string? ParentSession { get; init; }
}

internal sealed class CodingAgentJsonlSessionEntry
{
    public string Type { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string? ParentId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public CodingAgentJsonlSessionMessage? Message { get; init; }
    public string? TargetId { get; init; }
    public string? Label { get; init; }
    public string? Summary { get; init; }
    public string? FromId { get; init; }
    public string? FirstKeptEntryId { get; init; }
    public long? TokensBefore { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? ModelId { get; init; }
    public string? ThinkingLevel { get; init; }
    public string? Name { get; init; }
}

internal sealed record CodingAgentJsonlLabelState(string Label, DateTimeOffset Timestamp);

public sealed record CodingAgentJsonlPreviewOptions(string? Search = null, bool CurrentBranchOnly = false)
{
    public static CodingAgentJsonlPreviewOptions Default { get; } = new();
}

internal sealed class CodingAgentJsonlSessionMessage
{
    public string Role { get; init; } = string.Empty;
    public string? ToolCallId { get; init; }
    public bool IsError { get; init; }
    public List<CodingAgentJsonlSessionContent>? Content { get; init; } = [];
}

internal sealed class CodingAgentJsonlSessionContent
{
    public string Type { get; init; } = string.Empty;
    public string? Text { get; init; }
    public string? Thinking { get; init; }
    public string? Data { get; init; }
    public string? MimeType { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Arguments { get; init; }
}
