// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Registry;

namespace Tau.Ai.Providers;

/// <summary>【AI】【目录发布】提供方选择的持久化变更与同步内存更新；未指定 Persist 表示不修改存储，显式空值表示删除。</summary>
public sealed record ModelsPublication
{
    private ModelsStoreEntry? _persist;
    private bool _hasPersistence;
    public ModelsStoreEntry? Persist { get => _persist; init { _persist = value; _hasPersistence = true; } }
    public bool HasPersistence => _hasPersistence;
    public Action? Update { get; init; }
}

/// <summary>【AI】【动态目录刷新】某一离线或联网阶段的凭据、独立缓存快照及受代次保护的发布入口。</summary>
/// <param name="Credential">此阶段有效凭据，离线阶段为原始保存值。</param><param name="Stored">提供方缓存的独立快照。</param>
/// <param name="AllowNetwork">是否允许网络访问。</param><param name="Force">可选强制获取选项，离线阶段为空。</param>
/// <param name="Signal">调用方及刷新代次共同控制的取消信号。</param><param name="PublishAsync">先持久化再同步更新的发布委托。</param>
public sealed record RefreshModelsContext(ProviderCredential? Credential, ModelsStoreEntry? Stored, bool AllowNetwork, bool? Force,
    CancellationToken Signal, Func<ModelsPublication, Task<bool>> PublishAsync);
