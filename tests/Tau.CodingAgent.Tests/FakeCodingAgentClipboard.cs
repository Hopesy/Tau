using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class FakeCodingAgentClipboard : ICodingAgentClipboard
{
    public List<string> CopiedTexts { get; } = [];
    public CodingAgentClipboardImage? Image { get; set; }
    public string? Text { get; set; }
    public IReadOnlyList<string>? Files { get; set; }
    public List<string> Reads { get; } = [];

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        CopiedTexts.Add(text);
        return Task.CompletedTask;
    }

    /// <summary>【CodingAgent】【图片夹具】记录读取优先级并返回图片。</summary><param name="cancellationToken">取消信号。</param><returns>图片。</returns>
    public Task<CodingAgentClipboardImage?> ReadImageAsync(CancellationToken cancellationToken = default)
    { Reads.Add("image"); return Task.FromResult(Image); }
    /// <summary>【CodingAgent】【文本夹具】记录读取优先级并返回文本。</summary><param name="cancellationToken">取消信号。</param><returns>文本。</returns>
    public Task<string?> ReadTextAsync(CancellationToken cancellationToken = default)
    { Reads.Add("text"); return Task.FromResult(Text); }
    /// <summary>【CodingAgent】【文件夹具】记录读取优先级并返回文件。</summary><param name="cancellationToken">取消信号。</param><returns>文件列表。</returns>
    public Task<IReadOnlyList<string>?> ReadFilePathsAsync(CancellationToken cancellationToken = default)
    { Reads.Add("files"); return Task.FromResult(Files); }
}
