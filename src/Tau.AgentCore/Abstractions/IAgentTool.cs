using System.Text.Json;

namespace Tau.AgentCore;

/// <summary>
/// Self-describing tool that can be called by the LLM.
/// Mirrors pi-main's AgentTool with execute + prepareArguments.
/// </summary>
public interface IAgentTool
{
    string Name { get; }
    string Label { get; }
    string Description { get; }
    JsonElement ParameterSchema { get; }
    /// <summary>【AgentCore】【工具结构】供程序调用者解释 structuredContent 的可选输出 Schema。</summary>
    JsonElement? OutputSchema => null;
    /// <summary>系统提示中展示的一行简介；空值表示不列入提示中的工具清单。</summary>
    string? PromptSnippet => null;
    /// <summary>启用工具时加入系统提示的规则。</summary>
    IReadOnlyList<string> PromptGuidelines => [];
    /// <summary>模型可见的约束采样配置，默认使用普通 JSON Schema。</summary>
    Ai.ConstrainedSamplingConfig? ConstrainedSampling => null;
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
    object? Details = null,
    bool Terminate = false)
{
    /// <summary>【AgentCore】【工具结果】工具自身产生的用量，不计入主模型上下文大小。</summary>
    public Ai.Usage? Usage { get; init; }
    /// <summary>【AgentCore】【工具结果】仅供程序调用者读取的结构化输出，不发送给模型。</summary>
    public JsonElement? StructuredContent { get; init; }
}

public record ToolUpdate(
    string Text,
    IReadOnlyList<Ai.ContentBlock>? Content = null,
    bool? IsError = null,
    object? Details = null,
    bool? Terminate = null)
{
    /// <summary>【AgentCore】【工具进度】当前中间结果的用量。</summary>
    public Ai.Usage? Usage { get; init; }
    /// <summary>【AgentCore】【工具进度】当前中间结果的结构化输出。</summary>
    public JsonElement? StructuredContent { get; init; }

    /// <summary>【AgentCore】【工具进度】转换为完整的中间结果对象。</summary>
    /// <returns>保留内容和附加元数据的结果。</returns>
    public ToolResult ToPartialResult()
    {
        var content = Content ?? [new Ai.TextContent(Text)];
        return new ToolResult(content, IsError.GetValueOrDefault(), Details, Terminate.GetValueOrDefault())
        { Usage = Usage, StructuredContent = StructuredContent };
    }
}
