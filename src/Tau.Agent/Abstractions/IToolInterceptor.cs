namespace Tau.Agent;

/// <summary>
/// Intercepts tool calls before/after execution.
/// Mirrors pi-mono's beforeToolCall/afterToolCall hooks.
/// </summary>
public interface IToolInterceptor
{
    Task<ToolCallDecision> BeforeToolCallAsync(ToolCallContext context, CancellationToken ct = default)
        => Task.FromResult(ToolCallDecision.Allow);

    Task<ToolResult> AfterToolCallAsync(ToolCallContext context, ToolResult result, CancellationToken ct = default)
        => Task.FromResult(result);
}

public record ToolCallContext(
    string ToolCallId,
    string ToolName,
    System.Text.Json.JsonElement Arguments,
    IReadOnlyList<Ai.ChatMessage> ConversationHistory);

public record struct ToolCallDecision(bool Blocked, string? Reason = null)
{
    public static ToolCallDecision Allow => new(false);
    public static ToolCallDecision Block(string reason) => new(true, reason);
}
