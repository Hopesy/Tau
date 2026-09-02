using Tau.AgentCore.Harness.Session;
using Tau.Ai;

namespace Tau.AgentCore.Search;

/// <summary>会话搜索选项。</summary>
public sealed record SessionSearchOptions(IReadOnlyList<string>? EntryTypes = null, int? Limit = null, CancellationToken CancellationToken = default);

/// <summary>会话搜索命中项。</summary>
public sealed record SessionSearchHit(string SessionId, string EntryId, DateTimeOffset Timestamp, string Snippet);

/// <summary>异步会话搜索接口。</summary>
public interface ISessionSearch
{
    /// <summary>搜索会话文本并按条目顺序返回命中。</summary>
    /// <param name="text">不区分大小写的查询文本。</param>
    /// <param name="options">过滤和限制选项。</param>
    /// <returns>异步命中序列。</returns>
    IAsyncEnumerable<SessionSearchHit> SearchAsync(string text, SessionSearchOptions? options = null);
}

/// <summary>基于现有 ISessionStorage 的扫描式会话搜索实现。</summary>
public sealed class ScanningSessionSearch<TMetadata> : ISessionSearch where TMetadata : SessionMetadata
{
    private readonly IReadOnlyList<AgentHarnessSession<TMetadata>> _sessions;
    /// <summary>创建扫描式搜索。</summary>
    /// <param name="sessions">要扫描的会话。</param>
    public ScanningSessionSearch(IEnumerable<AgentHarnessSession<TMetadata>> sessions) => _sessions = sessions?.ToArray() ?? throw new ArgumentNullException(nameof(sessions));

    /// <inheritdoc />
    public async IAsyncEnumerable<SessionSearchHit> SearchAsync(string text, SessionSearchOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var normalized = text.Trim();
        var limit = options?.Limit;
        var emitted = 0;
        if (limit is <= 0) yield break;
        foreach (var session in _sessions)
        {
            options?.CancellationToken.ThrowIfCancellationRequested();
            var metadata = await session.GetMetadataAsync(options?.CancellationToken ?? default).ConfigureAwait(false);
            var entries = await session.GetEntriesAsync(options?.CancellationToken ?? default).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                if (options?.EntryTypes is { Count: > 0 } types && !types.Contains(entry.Type, StringComparer.OrdinalIgnoreCase)) continue;
                var textValue = entry switch
                {
                    MessageSessionEntry message => string.Join(" ", message.Message switch { UserMessage user => user.Content.OfType<TextContent>().Select(content => content.Text), AssistantMessage assistant => assistant.Content.OfType<TextContent>().Select(content => content.Text), _ => [] }),
                    CompactionSessionEntry compaction => compaction.Summary,
                    CustomMessageSessionEntry custom => string.Join(" ", custom.Content.OfType<TextContent>().Select(content => content.Text)),
                    _ => entry.Type
                };
                if (!textValue.Contains(normalized, StringComparison.OrdinalIgnoreCase)) continue;
                yield return new SessionSearchHit(metadata.Id, entry.Id, entry.Timestamp, textValue);
                emitted++;
                if (limit is int max && emitted >= max) yield break;
            }
        }
    }
}
