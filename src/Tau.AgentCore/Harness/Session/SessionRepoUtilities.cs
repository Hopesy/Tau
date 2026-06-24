namespace Tau.AgentCore.Harness.Session;

internal static class SessionRepoUtilities
{
    public static string CreateSessionId() => UuidV7.Create();

    public static string CreateTimestamp() =>
        DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    public static async Task<IReadOnlyList<SessionTreeEntry>> GetEntriesToForkAsync<TMetadata>(
        ISessionStorage<TMetadata> storage,
        SessionForkOptions options,
        CancellationToken cancellationToken)
        where TMetadata : SessionMetadata
    {
        if (string.IsNullOrWhiteSpace(options.EntryId))
            return await storage.GetEntriesAsync(cancellationToken).ConfigureAwait(false);

        var target = await storage.GetEntryAsync(options.EntryId, cancellationToken).ConfigureAwait(false);
        if (target is null)
            throw new SessionException("invalid_fork_target", $"Entry {options.EntryId} not found");

        string? effectiveLeafId;
        if (string.Equals(options.Position, "at", StringComparison.Ordinal))
        {
            effectiveLeafId = target.Id;
        }
        else
        {
            if (target is not MessageSessionEntry { Message: Tau.Ai.UserMessage })
                throw new SessionException("invalid_fork_target", $"Entry {options.EntryId} is not a user message");

            effectiveLeafId = target.ParentId;
        }

        return await storage.GetPathToRootAsync(effectiveLeafId, cancellationToken).ConfigureAwait(false);
    }
}
