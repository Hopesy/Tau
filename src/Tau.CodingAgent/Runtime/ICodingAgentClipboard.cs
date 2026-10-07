namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentClipboardImage(byte[] Bytes, string MimeType);

public interface ICodingAgentClipboard
{
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);

    Task<CodingAgentClipboardImage?> ReadImageAsync(CancellationToken cancellationToken = default);

    /// <summary>【CodingAgent】【剪贴板文本】读取原始文本，旧剪贴板实现默认不提供。</summary>
    /// <param name="cancellationToken">取消信号。</param><returns>文本，无内容时为空引用。</returns>
    Task<string?> ReadTextAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    /// <summary>【CodingAgent】【剪贴板文件】读取复制的本地文件路径，旧剪贴板实现默认不提供。</summary>
    /// <param name="cancellationToken">取消信号。</param><returns>原始路径列表，无文件时为空引用。</returns>
    Task<IReadOnlyList<string>?> ReadFilePathsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>?>(null);
}
