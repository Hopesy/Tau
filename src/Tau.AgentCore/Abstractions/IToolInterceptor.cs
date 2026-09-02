namespace Tau.AgentCore;

/// <summary>
/// Intercepts tool calls before/after execution.
/// Mirrors pi-main's beforeToolCall/afterToolCall hooks.
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

public record struct ToolCallDecision(
    bool Blocked,
    string? Reason = null,
    System.Text.Json.JsonElement? Arguments = null,
    bool Terminate = false)
{
    public static ToolCallDecision Allow => new(false);
    public static ToolCallDecision AllowWithArguments(System.Text.Json.JsonElement arguments) =>
        new(false, null, arguments.Clone());

    /// <summary>构造一个阻止工具执行的决定，并可要求本批工具调用终止后续模型轮次。</summary>
    /// <param name="reason">阻止原因。</param>
    /// <param name="terminate">是否将该阻止结果标记为终止本批处理。</param>
    public static ToolCallDecision Block(string reason, bool terminate = false) => new(true, reason, null, terminate);
}
