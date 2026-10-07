using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentExtensionToolAdapter : ICodingAgentToolDefinition
{
    private readonly CodingAgentExtensionTool _definition;
    private readonly CodingAgentJavaScriptExtensionRuntime _runtime;

    public CodingAgentExtensionToolAdapter(
        CodingAgentExtensionTool definition,
        CodingAgentJavaScriptExtensionRuntime runtime)
    {
        _definition = definition;
        _runtime = runtime;
    }

    public string Name => _definition.Name;
    internal string FilePath => _definition.FilePath;
    public string Label => string.IsNullOrWhiteSpace(_definition.Label) ? _definition.Name : _definition.Label;
    public string Description => _definition.Description;
    public JsonElement ParameterSchema => _definition.ParameterSchema;
    public JsonElement? OutputSchema => _definition.OutputSchema;
    public string Exposure => _definition.Exposure;
    public bool? DefaultActive => _definition.DefaultActive;
    public JsonElement? Namespace => _definition.Namespace;
    public JsonElement? Annotations => _definition.Annotations;
    public CodingAgentSourceInfo? SourceInfo => _definition.SourceInfo ?? _runtime.GetExtensionSource(_definition.FilePath);
    public ConstrainedSamplingConfig? ConstrainedSampling => _definition.ConstrainedSampling;
    /// <summary>【CodingAgent】【工具组合】调用扩展的声明准备钩子。</summary>
    /// <param name="loadout">完整工具组合。</param>
    /// <returns>模型呈现变更。</returns>
    public CodingAgentToolLoadoutChanges? PrepareLoadout(CodingAgentToolLoadout loadout) => _definition.HasPrepareLoadout
        ? _runtime.PrepareToolLoadout(_definition.FilePath, Name, loadout) : null;
    public string? PromptSnippet => _definition.PromptSnippet;
    public IReadOnlyList<string> PromptGuidelines => _definition.PromptGuidelines;
    public ToolExecutionMode ExecutionMode =>
        string.Equals(_definition.ExecutionMode, "sequential", StringComparison.OrdinalIgnoreCase)
            ? ToolExecutionMode.Sequential
            : ToolExecutionMode.Parallel;

    public ValueTask<JsonElement> PrepareArgumentsAsync(JsonElement rawArgs, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_definition.HasPrepareArguments)
        {
            return new ValueTask<JsonElement>(rawArgs);
        }

        var result = _runtime.PrepareToolArguments(_definition.FilePath, _definition.Name, rawArgs, ct);
        if (!result.Success || !result.PreparedArgs.HasValue)
        {
            throw new InvalidOperationException(result.Error ?? $"extension tool '{_definition.Name}' failed to prepare arguments");
        }

        return new ValueTask<JsonElement>(result.PreparedArgs.Value.Clone());
    }

    /// <summary>【CodingAgent】【扩展工具】异步执行扩展工具，保留多模态内容并转发实时进度。</summary>
    /// <param name="toolCallId">工具调用标识。</param>
    /// <param name="args">结构化调用参数。</param>
    /// <param name="ct">执行取消信号。</param>
    /// <param name="onUpdate">中间结果回调。</param>
    /// <returns>最终工具内容、详情与错误状态。</returns>
    public async Task<ToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct = default,
        Func<ToolUpdate, Task>? onUpdate = null)
    {
        ct.ThrowIfCancellationRequested();

        // 1. 【CodingAgent】【工具调度】异步等待进程响应，让线程池继续处理进度回调及嵌套工具
        var result = await _runtime.ExecuteToolAsync(_definition.FilePath, _definition.Name, toolCallId, args, ct, onUpdate)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            return new ToolResult(
                [new TextContent(result.Error ?? $"extension tool '{_definition.Name}' failed")],
                IsError: true);
        }

        object? details = result.Details.HasValue ? result.Details.Value.Clone() : null;
        return new ToolResult(result.Content, result.IsError, details, result.Terminate)
            { Usage = result.Usage, StructuredContent = result.StructuredContent };
    }
}
