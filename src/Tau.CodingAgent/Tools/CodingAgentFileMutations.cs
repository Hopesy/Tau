// 作者：xxx
namespace Tau.CodingAgent.Tools;

/// <summary>【CodingAgent】【文件修改】跨工具与会话按实际文件串行修改，不阻塞无关文件。</summary>
internal static class CodingAgentFileMutations
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Task> Queues = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>【CodingAgent】【修改队列】保持调用登记顺序，操作真正结束后才释放队列，包括已取消的操作。</summary>
    /// <typeparam name="T">操作返回类型。</typeparam><param name="path">待修改文件。</param><param name="action">持锁期间执行的完整操作。</param><returns>操作结果。</returns>
    internal static async Task<T> RunAsync<T>(string path, Func<Task<T>> action)
    {
        string key;
        Task previous;
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            key = ResolveKey(path);
            previous = Queues.GetValueOrDefault(key, Task.CompletedTask);
            Queues[key] = released.Task;
        }
        await previous.ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally
        {
            released.TrySetResult();
            lock (Gate) if (ReferenceEquals(Queues.GetValueOrDefault(key), released.Task)) Queues.Remove(key);
        }
    }

    /// <summary>【CodingAgent】【文件身份】现有文件按真实链接目标登记，不存在的路径按绝对路径登记。</summary>
    /// <param name="path">文件路径。</param><returns>队列键。</returns>
    private static string ResolveKey(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full)) return full;
        var current = Path.GetPathRoot(full)!;
        foreach (var segment in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo item = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (item.LinkTarget is not null) current = item.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
        }
        return current;
    }
}

/// <summary>【CodingAgent】【编辑后端】供本地与远程文件系统实现读取、写入及访问检查。</summary>
public interface ICodingAgentEditOperations
{
    /// <summary>【CodingAgent】【读取文件】读取原始字节以保留 UTF-8 BOM。</summary>
    /// <param name="path">绝对路径。</param><returns>文件字节。</returns>
    Task<byte[]> ReadFileAsync(string path);
    /// <summary>【CodingAgent】【写入文件】完成 UTF-8 内容写入后返回。</summary>
    /// <param name="path">绝对路径。</param><param name="content">待写文本。</param><returns>写入任务。</returns>
    Task WriteFileAsync(string path, string content);
    /// <summary>【CodingAgent】【访问检查】确认文件可读写，失败时抛出异常。</summary>
    /// <param name="path">绝对路径。</param><returns>检查任务。</returns>
    Task AccessAsync(string path);
}

/// <summary>【CodingAgent】【写入后端】供本地与远程文件系统实现目录创建与文件写入。</summary>
public interface ICodingAgentWriteOperations
{
    /// <summary>【CodingAgent】【写入文件】完成内容写入后返回。</summary>
    /// <param name="path">绝对路径。</param><param name="content">待写文本。</param><returns>写入任务。</returns>
    Task WriteFileAsync(string path, string content);
    /// <summary>【CodingAgent】【父目录】递归创建目录。</summary>
    /// <param name="path">绝对目录。</param><returns>创建任务。</returns>
    Task CreateDirectoryAsync(string path);
}

/// <summary>【CodingAgent】【本地文件】以无隐式 BOM 的 UTF-8 实现本地编辑与写入。</summary>
internal sealed class LocalCodingAgentFileOperations : ICodingAgentEditOperations, ICodingAgentWriteOperations
{
    /// <summary>【CodingAgent】【读取文件】读取完整原始字节。</summary>
    /// <param name="path">绝对路径。</param><returns>文件字节。</returns>
    public Task<byte[]> ReadFileAsync(string path) => File.ReadAllBytesAsync(path);
    /// <summary>【CodingAgent】【写入文件】写入完整文本，调用方在写入真正结束后处理取消。</summary>
    /// <param name="path">绝对路径。</param><param name="content">文件文本。</param><returns>写入任务。</returns>
    public Task WriteFileAsync(string path, string content) => File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(false));
    /// <summary>【CodingAgent】【访问检查】打开文件验证读取和写入权限，不修改内容。</summary>
    /// <param name="path">绝对路径。</param><returns>检查完成的任务。</returns>
    public Task AccessAsync(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        return Task.CompletedTask;
    }
    /// <summary>【CodingAgent】【父目录】递归创建所需目录。</summary>
    /// <param name="path">绝对目录。</param><returns>创建完成的任务。</returns>
    public Task CreateDirectoryAsync(string path) { Directory.CreateDirectory(path); return Task.CompletedTask; }
}
