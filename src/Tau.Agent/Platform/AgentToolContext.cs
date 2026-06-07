using System.Text.Json;

namespace Tau.Agent.Platform;

public sealed record AgentToolContext(
    string ToolCallId,
    string ToolName,
    JsonElement Arguments,
    Func<ToolUpdate, Task>? OnUpdate = null)
{
    public Task ReportUpdateAsync(ToolUpdate update) =>
        OnUpdate is null ? Task.CompletedTask : OnUpdate(update);
}
