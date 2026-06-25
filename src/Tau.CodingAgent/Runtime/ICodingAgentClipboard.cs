namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentClipboardImage(byte[] Bytes, string MimeType);

public interface ICodingAgentClipboard
{
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);

    Task<CodingAgentClipboardImage?> ReadImageAsync(CancellationToken cancellationToken = default);
}
