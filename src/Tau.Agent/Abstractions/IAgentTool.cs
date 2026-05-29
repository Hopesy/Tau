using System.Text.Json;

namespace Tau.Agent;

/// <summary>
/// Self-describing tool that can be called by the LLM.
/// Mirrors pi-mono's AgentTool with execute + prepareArguments.
/// </summary>
public interface IAgentTool
{
    string Name { get; }
    string Label { get; }
    string Description { get; }
    JsonElement ParameterSchema { get; }
    ToolExecutionMode ExecutionMode => ToolExecutionMode.Parallel;

    ValueTask<JsonElement> PrepareArgumentsAsync(JsonElement rawArgs, CancellationToken ct = default)
        => new(rawArgs);

    Task<ToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct = default,
        Func<ToolUpdate, Task>? onUpdate = null);
}

public enum ToolExecutionMode
{
    Sequential,
    Parallel
}

public record ToolResult(
    IReadOnlyList<Ai.ContentBlock> Content,
    bool IsError = false,
    object? Details = null);

public record ToolUpdate(
    string Text,
    IReadOnlyList<Ai.ContentBlock>? Content = null,
    bool? IsError = null,
    object? Details = null)
{
    internal ToolResult ToPartialResult()
    {
        var content = Content ?? [new Ai.TextContent(Text)];
        return new ToolResult(content, IsError.GetValueOrDefault(), Details);
    }
}
