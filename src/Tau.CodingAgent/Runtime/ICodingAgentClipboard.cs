namespace Tau.CodingAgent.Runtime;

public interface ICodingAgentClipboard
{
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);
}
