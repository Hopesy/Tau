using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.AgentCore.Harness.Session;

public sealed class AgentHarnessSession<TMetadata>
    where TMetadata : SessionMetadata
{
    private readonly ISessionStorage<TMetadata> _storage;

    public AgentHarnessSession(ISessionStorage<TMetadata> storage)
    {
        _storage = storage;
    }

    public Task<TMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) =>
        _storage.GetMetadataAsync(cancellationToken);

    public ISessionStorage<TMetadata> GetStorage() => _storage;

    public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default) =>
        _storage.GetLeafIdAsync(cancellationToken);

    public Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default) =>
        _storage.GetEntryAsync(id, cancellationToken);

    public Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) =>
        _storage.GetEntriesAsync(cancellationToken);

    public async Task<IReadOnlyList<SessionTreeEntry>> GetBranchAsync(
        string? fromId = null,
        CancellationToken cancellationToken = default)
    {
        var leafId = fromId ?? await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false);
        return await _storage.GetPathToRootAsync(leafId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionContext> BuildContextAsync(CancellationToken cancellationToken = default) =>
        BuildSessionContext(await GetBranchAsync(cancellationToken: cancellationToken).ConfigureAwait(false));

    public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default) =>
        _storage.GetLabelAsync(id, cancellationToken);

    public async Task<string?> GetSessionNameAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _storage.FindEntriesAsync("session_info", cancellationToken).ConfigureAwait(false);
        return entries
            .OfType<SessionInfoEntry>()
            .LastOrDefault()
            ?.Name
            ?.Trim() is { Length: > 0 } name
                ? name
                : null;
    }

    public async Task<string> AppendMessageAsync(
        ChatMessage message,
        CancellationToken cancellationToken = default) =>
        await AppendTypedEntryAsync(
            new MessageSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                message),
            cancellationToken).ConfigureAwait(false);

    public async Task<string> AppendThinkingLevelChangeAsync(
        string thinkingLevel,
        CancellationToken cancellationToken = default) =>
        await AppendTypedEntryAsync(
            new ThinkingLevelChangeSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                thinkingLevel),
            cancellationToken).ConfigureAwait(false);

    public async Task<string> AppendModelChangeAsync(
        string provider,
        string modelId,
        CancellationToken cancellationToken = default) =>
        await AppendTypedEntryAsync(
            new ModelChangeSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                provider,
                modelId),
            cancellationToken).ConfigureAwait(false);

    public async Task<string> AppendActiveToolsChangeAsync(
        IReadOnlyList<string> activeToolNames,
        CancellationToken cancellationToken = default) =>
        await AppendTypedEntryAsync(
            new ActiveToolsChangeSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                activeToolNames.ToArray()),
            cancellationToken).ConfigureAwait(false);

    public async Task<string> AppendCompactionAsync(
        string summary,
        string firstKeptEntryId,
        int tokensBefore,
        object? details = null,
        bool fromHook = false,
        CancellationToken cancellationToken = default) =>
        await AppendTypedEntryAsync(
            new CompactionSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                summary,
                firstKeptEntryId,
                tokensBefore,
                details,
                fromHook),
            cancellationToken).ConfigureAwait(false);

    public async Task<string> AppendCustomEntryAsync(
        string customType,
        object? data = null,
        CancellationToken cancellationToken = default) =>
        await AppendTypedEntryAsync(
            new CustomSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                customType,
                data),
            cancellationToken).ConfigureAwait(false);

    public async Task<string> AppendCustomMessageEntryAsync(
        string customType,
        string content,
        bool display,
        object? details = null,
        CancellationToken cancellationToken = default) =>
        await AppendCustomMessageEntryAsync(
            customType,
            [new TextContent(content)],
            display,
            details,
            cancellationToken).ConfigureAwait(false);

    public async Task<string> AppendCustomMessageEntryAsync(
        string customType,
        IReadOnlyList<ContentBlock> content,
        bool display,
        object? details = null,
        CancellationToken cancellationToken = default) =>
        await AppendTypedEntryAsync(
            new CustomMessageSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                customType,
                content.ToArray(),
                display,
                details),
            cancellationToken).ConfigureAwait(false);

    public async Task<string> AppendLabelAsync(
        string targetId,
        string? label,
        CancellationToken cancellationToken = default)
    {
        if (await _storage.GetEntryAsync(targetId, cancellationToken).ConfigureAwait(false) is null)
            throw new SessionException("not_found", $"Entry {targetId} not found");

        return await AppendTypedEntryAsync(
            new LabelSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                targetId,
                label),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> AppendSessionNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var sanitized = name
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        return await AppendTypedEntryAsync(
            new SessionInfoEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                sanitized),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> MoveToAsync(
        string? entryId,
        SessionBranchSummary? summary = null,
        CancellationToken cancellationToken = default)
    {
        if (entryId is not null && await _storage.GetEntryAsync(entryId, cancellationToken).ConfigureAwait(false) is null)
            throw new SessionException("not_found", $"Entry {entryId} not found");

        await _storage.SetLeafIdAsync(entryId, cancellationToken).ConfigureAwait(false);
        if (summary is null)
            return null;

        return await AppendTypedEntryAsync(
            new BranchSummarySessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                entryId,
                DateTimeOffset.UtcNow,
                entryId ?? "root",
                summary.Summary,
                summary.Details,
                summary.FromHook),
            cancellationToken).ConfigureAwait(false);
    }

    public static SessionContext BuildSessionContext(IReadOnlyList<SessionTreeEntry> pathEntries)
    {
        var thinkingLevel = "off";
        SessionModelReference? model = null;
        IReadOnlyList<string>? activeToolNames = null;
        CompactionSessionEntry? compaction = null;

        foreach (var entry in pathEntries)
        {
            switch (entry)
            {
                case ThinkingLevelChangeSessionEntry thinking:
                    thinkingLevel = thinking.ThinkingLevel;
                    break;
                case ModelChangeSessionEntry modelChange:
                    model = new SessionModelReference(modelChange.Provider, modelChange.ModelId);
                    break;
                case ActiveToolsChangeSessionEntry tools:
                    activeToolNames = tools.ActiveToolNames.ToArray();
                    break;
                case CompactionSessionEntry compactionEntry:
                    compaction = compactionEntry;
                    break;
                case MessageSessionEntry { Message: AssistantMessage { Provider: not null, Model: not null } assistant }:
                    model = new SessionModelReference(assistant.Provider, assistant.Model);
                    break;
            }
        }

        var messages = new List<ChatMessage>();
        void AppendMessage(SessionTreeEntry entry)
        {
            switch (entry)
            {
                case MessageSessionEntry message:
                    messages.Add(message.Message);
                    break;
                case CustomMessageSessionEntry custom:
                    messages.Add(AgentHarnessMessages.CreateCustomMessage(
                        custom.CustomType,
                        custom.Content,
                        custom.Display,
                        custom.Details,
                        custom.Timestamp));
                    break;
                case BranchSummarySessionEntry { Summary.Length: > 0 } summary:
                    messages.Add(AgentHarnessMessages.CreateBranchSummaryMessage(
                        summary.Summary,
                        summary.FromId,
                        summary.Timestamp));
                    break;
            }
        }

        if (compaction is not null)
        {
            messages.Add(AgentHarnessMessages.CreateCompactionSummaryMessage(
                compaction.Summary,
                compaction.TokensBefore,
                compaction.Timestamp));
            var compactionIndex = pathEntries
                .Select((entry, index) => (entry, index))
                .FirstOrDefault(pair => pair.entry is CompactionSessionEntry current && current.Id == compaction.Id)
                .index;
            var foundFirstKept = false;
            for (var i = 0; i < compactionIndex; i++)
            {
                var entry = pathEntries[i];
                if (entry.Id == compaction.FirstKeptEntryId)
                    foundFirstKept = true;
                if (foundFirstKept)
                    AppendMessage(entry);
            }

            for (var i = compactionIndex + 1; i < pathEntries.Count; i++)
            {
                AppendMessage(pathEntries[i]);
            }
        }
        else
        {
            foreach (var entry in pathEntries)
            {
                AppendMessage(entry);
            }
        }

        return new SessionContext(messages, thinkingLevel, model, activeToolNames);
    }

    private async Task<string> AppendTypedEntryAsync<TEntry>(
        TEntry entry,
        CancellationToken cancellationToken)
        where TEntry : SessionTreeEntry
    {
        await _storage.AppendEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        return entry.Id;
    }
}
