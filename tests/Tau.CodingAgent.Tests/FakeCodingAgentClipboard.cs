using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class FakeCodingAgentClipboard : ICodingAgentClipboard
{
    public List<string> CopiedTexts { get; } = [];
    public CodingAgentClipboardImage? Image { get; set; }

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        CopiedTexts.Add(text);
        return Task.CompletedTask;
    }

    public Task<CodingAgentClipboardImage?> ReadImageAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Image);
}
