using Tau.Ai.Registry;

namespace Tau.Ai;

/// <summary>AI 公共命名空间中的动态模型目录存储接口。</summary>
public interface ModelsStore : Registry.ModelsStore { }

/// <summary>AI 公共命名空间中的内存模型目录存储实现。</summary>
public sealed class InMemoryModelsStore : ModelsStore
{
    private readonly Registry.InMemoryModelsStore _inner = new();
    /// <summary>读取 provider 目录。</summary>
    public Task<ModelsStoreEntry?> ReadAsync(string providerId, ModelsStoreOperationOptions? options = null) => _inner.ReadAsync(providerId, options);
    /// <summary>写入 provider 目录。</summary>
    public Task WriteAsync(string providerId, ModelsStoreEntry entry, ModelsStoreOperationOptions? options = null) => _inner.WriteAsync(providerId, entry, options);
    /// <summary>删除 provider 目录。</summary>
    public Task DeleteAsync(string providerId, ModelsStoreOperationOptions? options = null) => _inner.DeleteAsync(providerId, options);
}
