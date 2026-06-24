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
        var messages = new List<ChatMessage>();

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
                case MessageSessionEntry { Message: AssistantMessage { Provider: not null, Model: not null } assistant }:
                    model = new SessionModelReference(assistant.Provider, assistant.Model);
                    messages.Add(assistant);
                    break;
                case MessageSessionEntry message:
                    messages.Add(message.Message);
                    break;
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
