namespace Tau.Ai.Registry;

/// <summary>可持久化 provider 动态模型目录的条目。</summary>
public sealed record ModelsStoreEntry(
    IReadOnlyList<Model> Models,
    long? LastModified = null,
    long? CheckedAt = null,
    string? Etag = null);

/// <summary>模型目录存储操作选项。</summary>
public sealed record ModelsStoreOperationOptions(CancellationToken CancellationToken = default);

/// <summary>按 provider id 读写动态模型目录。</summary>
public interface ModelsStore
{
    /// <summary>读取 provider 目录。</summary>
    /// <param name="providerId">provider id。</param>
    /// <param name="options">取消选项。</param>
    /// <returns>目录条目，不存在时为 null。</returns>
    Task<ModelsStoreEntry?> ReadAsync(string providerId, ModelsStoreOperationOptions? options = null);
    /// <summary>写入 provider 目录。</summary>
    /// <param name="providerId">provider id。</param>
    /// <param name="entry">目录条目。</param>
    /// <param name="options">取消选项。</param>
    Task WriteAsync(string providerId, ModelsStoreEntry entry, ModelsStoreOperationOptions? options = null);
    /// <summary>删除 provider 目录。</summary>
    /// <param name="providerId">provider id。</param>
    /// <param name="options">取消选项。</param>
    Task DeleteAsync(string providerId, ModelsStoreOperationOptions? options = null);
}

/// <summary>线程安全的内存模型目录存储。</summary>
public interface IModelsStore : ModelsStore { }

/// <summary>线程安全的内存模型目录存储。</summary>
public sealed class InMemoryModelsStore : IModelsStore
{
    private readonly Dictionary<string, ModelsStoreEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    /// <inheritdoc />
    public Task<ModelsStoreEntry?> ReadAsync(string providerId, ModelsStoreOperationOptions? options = null)
    {
        options?.CancellationToken.ThrowIfCancellationRequested();
        lock (_entries) return Task.FromResult(_entries.TryGetValue(providerId, out var value) ? value with { Models = value.Models.ToArray() } : null);
    }
    /// <inheritdoc />
    public Task WriteAsync(string providerId, ModelsStoreEntry entry, ModelsStoreOperationOptions? options = null)
    {
        options?.CancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);
        lock (_entries) _entries[providerId] = entry with { Models = entry.Models.ToArray() };
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public Task DeleteAsync(string providerId, ModelsStoreOperationOptions? options = null)
    {
        options?.CancellationToken.ThrowIfCancellationRequested();
        lock (_entries) _entries.Remove(providerId);
        return Task.CompletedTask;
    }
}
