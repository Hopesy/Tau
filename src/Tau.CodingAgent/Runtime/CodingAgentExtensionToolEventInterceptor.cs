using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentExtensionToolEventModule(
    string FilePath,
    string Scope,
    string Runtime,
    bool HandlesToolCall,
    bool HandlesToolResult);

public sealed class CodingAgentExtensionToolEventInterceptor : IToolInterceptor
{
    private readonly IReadOnlyList<CodingAgentExtensionToolEventModule> _modules;
    private readonly CodingAgentJavaScriptExtensionRuntime _runtime;

    public CodingAgentExtensionToolEventInterceptor(
        IReadOnlyList<CodingAgentExtensionToolEventModule> modules,
        CodingAgentJavaScriptExtensionRuntime runtime)
    {
        _modules = modules;
        _runtime = runtime;
    }

    public Task<ToolCallDecision> BeforeToolCallAsync(
        ToolCallContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var effectiveContext = context;
        var hasArgumentsMutation = false;
        foreach (var module in _modules)
        {
            if (!module.HandlesToolCall)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            var result = _runtime.EmitToolCall(
                module.FilePath,
                effectiveContext.ToolName,
                effectiveContext.ToolCallId,
                effectiveContext.Arguments);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.Error ?? $"extension tool_call handler failed for '{effectiveContext.ToolName}'");
            }

            if (result.Blocked)
            {
                return Task.FromResult(ToolCallDecision.Block(
                    string.IsNullOrWhiteSpace(result.Reason)
                        ? "blocked by extension"
                        : result.Reason));
            }

            if (result.Arguments.HasValue)
            {
                var arguments = result.Arguments.Value.Clone();
                effectiveContext = effectiveContext with { Arguments = arguments };
                hasArgumentsMutation = true;
            }
        }

        return Task.FromResult(hasArgumentsMutation
            ? ToolCallDecision.AllowWithArguments(effectiveContext.Arguments)
            : ToolCallDecision.Allow);
    }

    public Task<ToolResult> AfterToolCallAsync(
        ToolCallContext context,
        ToolResult result,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var current = result;
        foreach (var module in _modules)
        {
            if (!module.HandlesToolResult)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            var eventResult = _runtime.EmitToolResult(
                module.FilePath,
                context.ToolName,
                context.ToolCallId,
                context.Arguments,
                current);
            if (!eventResult.Success)
            {
                throw new InvalidOperationException(
                    eventResult.Error ?? $"extension tool_result handler failed for '{context.ToolName}'");
            }

            current = new ToolResult(
                eventResult.Content.Select(static text => new TextContent(text)).ToArray(),
                eventResult.IsError,
                eventResult.Details.HasValue ? eventResult.Details.Value.Clone() : null);
        }

        return Task.FromResult(current);
    }
}
