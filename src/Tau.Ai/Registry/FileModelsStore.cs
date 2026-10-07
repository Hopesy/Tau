// 作者：xxx
using System.Text;
using System.Text.Json;
using Tau.Ai.Serialization;

namespace Tau.Ai.Registry;

/// <summary>【AI】【目录存储】使用独占文件锁和同目录原子替换保存动态模型目录。</summary>
public sealed class FileModelsStore : IModelsStore
{
    private readonly string _path;
    private readonly string _lockPath;

    /// <summary>创建 JSON 文件存储；多个实例可安全共享同一路径。</summary>
    /// <param name="path">目录存储文件路径，与用户编辑的 models.json 分开。</param>
    public FileModelsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _lockPath = _path + ".lock";
    }

    /// <summary>读取 provider 的完整独立快照。</summary>
    /// <param name="providerId">provider 标识。</param>
    /// <param name="options">可选取消信号。</param>
    /// <returns>存储条目；文件或条目不存在时返回 null。</returns>
    public async Task<ModelsStoreEntry?> ReadAsync(string providerId, ModelsStoreOperationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var signal = options?.CancellationToken ?? default;
        await using var lease = await AcquireLockAsync(signal).ConfigureAwait(false);
        var entries = await ReadEntriesAsync(signal).ConfigureAwait(false);
        signal.ThrowIfCancellationRequested();
        return entries.GetValueOrDefault(providerId);
    }

    /// <summary>在独占锁内读取最新文件并替换指定 provider，保留其他条目。</summary>
    /// <param name="providerId">provider 标识。</param>
    /// <param name="entry">完整模型快照。</param>
    /// <param name="options">可选取消信号。</param>
    /// <returns>写入完成任务。</returns>
    public async Task WriteAsync(string providerId, ModelsStoreEntry entry, ModelsStoreOperationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Models);
        if (entry.Models.Any(model => model is null)) throw new ArgumentException("Models cannot contain null entries.", nameof(entry));
        var signal = options?.CancellationToken ?? default;
        signal.ThrowIfCancellationRequested();
        // 1. 【AI】【目录存储】先建立独立快照，等待文件锁时不再持有调用方可变数据
        var snapshot = JsonSerializer.SerializeToUtf8Bytes(entry, TauAiJsonContext.Default.ModelsStoreEntry);
        await using var lease = await AcquireLockAsync(signal).ConfigureAwait(false);
        var entries = await ReadEntriesAsync(signal).ConfigureAwait(false);
        entries[providerId] = JsonSerializer.Deserialize(snapshot, TauAiJsonContext.Default.ModelsStoreEntry)!;
        await SaveEntriesAsync(entries, signal).ConfigureAwait(false);
    }

    /// <summary>删除指定 provider；不存在的条目无需改写文件。</summary>
    /// <param name="providerId">provider 标识。</param>
    /// <param name="options">可选取消信号。</param>
    /// <returns>删除完成任务。</returns>
    public async Task DeleteAsync(string providerId, ModelsStoreOperationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var signal = options?.CancellationToken ?? default;
        await using var lease = await AcquireLockAsync(signal).ConfigureAwait(false);
        var entries = await ReadEntriesAsync(signal).ConfigureAwait(false);
        if (entries.Remove(providerId)) await SaveEntriesAsync(entries, signal).ConfigureAwait(false);
    }

    /// <summary>获得跨实例、跨进程独占锁；等待共享冲突时响应取消。</summary>
    /// <param name="signal">取消信号。</param>
    /// <returns>持有期间阻止其他访问的文件句柄，释放句柄即释放锁。</returns>
    private async Task<FileStream> AcquireLockAsync(CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        for (;;)
        {
            signal.ThrowIfCancellationRequested();
            try
            {
                // 1. 【AI】【目录存储】锁文件保持存在，以免删除后出现两个不同锁对象
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11)
            {
                await Task.Delay(25, signal).ConfigureAwait(false);
            }
        }
    }

    /// <summary>在持有文件锁时读取最新目录，兼容 UTF-8 BOM，损坏内容直接报错。</summary>
    /// <param name="signal">取消信号。</param>
    /// <returns>按 provider 标识组织的目录。</returns>
    private async Task<Dictionary<string, ModelsStoreEntry>> ReadEntriesAsync(CancellationToken signal)
    {
        try
        {
            var content = await File.ReadAllTextAsync(_path, Encoding.UTF8, signal).ConfigureAwait(false);
            var entries = JsonSerializer.Deserialize(content, TauAiJsonContext.Default.DictionaryStringModelsStoreEntry)
                ?? throw new JsonException("Models store must contain a JSON object.");
            if (entries.Values.Any(entry => entry is null || entry.Models is null || entry.Models.Any(model => model is null)))
                throw new JsonException("Each models store entry must contain a models array of objects.");
            return new Dictionary<string, ModelsStoreEntry>(entries, StringComparer.OrdinalIgnoreCase);
        }
        catch (FileNotFoundException)
        {
            return new Dictionary<string, ModelsStoreEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>完整写入同目录临时文件后替换目标；失败或取消时保留原文件。</summary>
    /// <param name="entries">待保存的完整目录。</param>
    /// <param name="signal">取消信号。</param>
    /// <returns>原子替换完成任务。</returns>
    private async Task SaveEntriesAsync(Dictionary<string, ModelsStoreEntry> entries, CancellationToken signal)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entries, TauAiJsonContext.Default.DictionaryStringModelsStoreEntry);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // 1. 【AI】【目录存储】写入完整内容并刷入磁盘后才发布新版本
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, signal).ConfigureAwait(false);
                await stream.FlushAsync(signal).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            // 2. 【AI】【目录存储】取消检查位于提交点之前，提交成功后正常返回
            signal.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
