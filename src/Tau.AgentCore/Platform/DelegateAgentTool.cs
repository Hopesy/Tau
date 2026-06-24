using System.Text.Json;

namespace Tau.AgentCore.Platform;

public delegate Task<ToolResult> AgentToolDelegate(
    AgentToolContext context,
    CancellationToken cancellationToken);

public sealed class DelegateAgentTool : IAgentTool
{
    private readonly JsonElement _parameterSchema;
    private readonly AgentToolDelegate _execute;
    private readonly Func<JsonElement, CancellationToken, ValueTask<JsonElement>>? _prepareArguments;

    public DelegateAgentTool(
        string name,
        string label,
        string description,
        JsonElement parameterSchema,
        AgentToolDelegate execute,
        ToolExecutionMode executionMode = ToolExecutionMode.Parallel,
        Func<JsonElement, CancellationToken, ValueTask<JsonElement>>? prepareArguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(execute);

        Name = name;
        Label = label;
        Description = description ?? string.Empty;
        _parameterSchema = parameterSchema.Clone();
        _execute = execute;
        ExecutionMode = executionMode;
        _prepareArguments = prepareArguments;
    }

    public string Name { get; }
    public string Label { get; }
    public string Description { get; }
    public JsonElement ParameterSchema => _parameterSchema;
    public ToolExecutionMode ExecutionMode { get; }

    public ValueTask<JsonElement> PrepareArgumentsAsync(JsonElement rawArgs, CancellationToken ct = default) =>
        _prepareArguments is null ? new(rawArgs) : _prepareArguments(rawArgs, ct);

    public Task<ToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct = default,
        Func<ToolUpdate, Task>? onUpdate = null) =>
        _execute(new AgentToolContext(toolCallId, Name, args, onUpdate), ct);
}
