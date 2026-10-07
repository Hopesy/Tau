// 作者：xxx
namespace Tau.CodingAgent.Runtime;

internal sealed class CodingAgentImageResizeWorker
{
    public static CodingAgentImageResizeWorker Default { get; } = new();

    private readonly Func<byte[], string, bool, int, int, long, int, CodingAgentImagePreprocessResult?> _process;

    /// <summary>【CodingAgent】【图片工作线程】创建使用托管图片后端的工作器。</summary>
    public CodingAgentImageResizeWorker()
    {
        _process = CodingAgentImagePreprocessor.Process;
    }

    /// <summary>【CodingAgent】【工作器注入】注入兼容旧参数的处理函数，用于隔离工作线程测试。</summary>
    /// <param name="process">图片处理回调。</param>
    internal CodingAgentImageResizeWorker(
        Func<byte[], string, bool, int, int, long, CodingAgentImagePreprocessResult?> process)
    {
        _process = (bytes, mime, resize, width, height, budget, _) => process(bytes, mime, resize, width, height, budget);
    }

    /// <summary>【CodingAgent】【异步缩放】复制输入后在工作线程处理，调用方取消时立即结束等待。</summary>
    /// <param name="bytes">调用方图片字节。</param><param name="mimeType">格式。</param><param name="autoResizeImages">是否缩放。</param>
    /// <param name="maxWidth">最大宽度。</param><param name="maxHeight">最大高度。</param><param name="maxBase64Bytes">编码预算。</param>
    /// <param name="cancellationToken">取消信号。</param><param name="jpegQuality">首选 JPEG 质量。</param><returns>图片处理任务。</returns>
    public Task<CodingAgentImagePreprocessResult?> ProcessAsync(
        byte[] bytes,
        string mimeType,
        bool autoResizeImages,
        int maxWidth = CodingAgentImagePreprocessor.DefaultMaxWidth,
        int maxHeight = CodingAgentImagePreprocessor.DefaultMaxHeight,
        long maxBase64Bytes = CodingAgentImagePreprocessor.DefaultMaxBase64Bytes,
        CancellationToken cancellationToken = default,
        int jpegQuality = 80)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(mimeType);

        cancellationToken.ThrowIfCancellationRequested();

        // 1. 【CodingAgent】【工作线程边界】工作器拥有独立字节，调用方可以继续复用自己的缓冲区
        var workerBytes = bytes.ToArray();
        return Task.Run(
            () => _process(workerBytes, mimeType, autoResizeImages, maxWidth, maxHeight, maxBase64Bytes, jpegQuality),
            cancellationToken).WaitAsync(cancellationToken);
    }
}
