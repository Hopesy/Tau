namespace Tau.Ai;

/// <summary>
/// Base type for message content blocks. Pattern match on subtypes.
/// </summary>
public abstract record ContentBlock(string Type);

public sealed record TextContent(string Text) : ContentBlock("text")
{
    public string? TextSignature { get; init; }
}

public sealed record ThinkingContent(string Thinking) : ContentBlock("thinking")
{
    public string? ThinkingSignature { get; init; }
    public bool Redacted { get; init; }
}

public sealed record ImageContent(string Data, string MimeType) : ContentBlock("image");

public sealed record ToolCallContent(string Id, string Name, string Arguments) : ContentBlock("toolCall")
{
    public string? ThoughtSignature { get; init; }
    /// <summary>【AI】【工具命名空间】Responses 动态加载工具所属空间，仅同模型请求回放。</summary>
    public string? Namespace { get; init; }
}
