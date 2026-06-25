namespace Tau.CodingAgent.Runtime;

internal sealed class CodingAgentImageResizeWorker
{
    public static CodingAgentImageResizeWorker Default { get; } = new();

    private readonly Func<byte[], string, bool, int, int, long, CodingAgentImagePreprocessResult?> _process;

    public CodingAgentImageResizeWorker()
        : this(CodingAgentImagePreprocessor.Process)
    {
    }

    internal CodingAgentImageResizeWorker(
        Func<byte[], string, bool, int, int, long, CodingAgentImagePreprocessResult?> process)
    {
        _process = process;
    }

    public Task<CodingAgentImagePreprocessResult?> ProcessAsync(
        byte[] bytes,
        string mimeType,
        bool autoResizeImages,
        int maxWidth = CodingAgentImagePreprocessor.DefaultMaxWidth,
        int maxHeight = CodingAgentImagePreprocessor.DefaultMaxHeight,
        long maxBase64Bytes = CodingAgentImagePreprocessor.DefaultMaxBase64Bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(mimeType);

        cancellationToken.ThrowIfCancellationRequested();

        // Match the upstream worker transfer boundary: the worker owns its byte
        // buffer, so callers can safely reuse or mutate their original array.
        var workerBytes = bytes.ToArray();
        return Task.Run(
            () => _process(workerBytes, mimeType, autoResizeImages, maxWidth, maxHeight, maxBase64Bytes),
            cancellationToken);
    }
}
