namespace Tau.Ai;

/// <summary>
/// Base type for all chat messages. Pattern match on subtypes for exhaustive handling.
/// </summary>
public abstract record ChatMessage(string Role);

public sealed record UserMessage : ChatMessage
{
    public UserMessage(string text) : base("user") => Content = [new TextContent(text)];
    public UserMessage(IReadOnlyList<ContentBlock> content) : base("user") => Content = content;
    public IReadOnlyList<ContentBlock> Content { get; init; }
}

public sealed record AssistantMessage : ChatMessage
{
    public AssistantMessage(IReadOnlyList<ContentBlock> content) : base("assistant") => Content = content;
    public AssistantMessage() : base("assistant") => Content = [];

    public IReadOnlyList<ContentBlock> Content { get; init; }
    public Usage? Usage { get; init; }
    public StopReason? StopReason { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Api { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? ResponseId { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
}

public sealed record ToolResultMessage(
    string ToolCallId,
    IReadOnlyList<ContentBlock> Content,
    bool IsError = false) : ChatMessage("toolResult");

public record struct Usage(
    int InputTokens,
    int OutputTokens,
    int? CacheReadTokens = null,
    int? CacheWriteTokens = null);

public enum StopReason
{
    EndTurn,
    MaxTokens,
    ToolUse,
    ContentFilter,
    Error
}
