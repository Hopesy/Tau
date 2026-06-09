using System.Text.Json;
using Tau.Agent;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentExtensionToolAdapter : IAgentTool
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
    public string Label => string.IsNullOrWhiteSpace(_definition.Label) ? _definition.Name : _definition.Label;
    public string Description => _definition.Description;
    public JsonElement ParameterSchema => _definition.ParameterSchema;
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

        var result = _runtime.PrepareToolArguments(_definition.FilePath, _definition.Name, rawArgs);
        if (!result.Success || !result.PreparedArgs.HasValue)
        {
            throw new InvalidOperationException(result.Error ?? $"extension tool '{_definition.Name}' failed to prepare arguments");
        }

        return new ValueTask<JsonElement>(result.PreparedArgs.Value.Clone());
    }

    public Task<ToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct = default,
        Func<ToolUpdate, Task>? onUpdate = null)
    {
        ct.ThrowIfCancellationRequested();

        var result = _runtime.ExecuteTool(_definition.FilePath, _definition.Name, toolCallId, args);
        if (!result.Success)
        {
            return Task.FromResult(new ToolResult(
                [new TextContent(result.Error ?? $"extension tool '{_definition.Name}' failed")],
                IsError: true));
        }

        IReadOnlyList<ContentBlock> content = result.Content.Count == 0
            ? [new TextContent(string.Empty)]
            : result.Content.Select(static text => new TextContent(text)).ToArray();
        object? details = result.Details.HasValue ? result.Details.Value.Clone() : null;
        return Task.FromResult(new ToolResult(content, result.IsError, details));
    }
}
