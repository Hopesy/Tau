using System.Text;
using Tau.Agent;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.Mom;

public sealed class RuntimeDelegationAgentRunner : IDelegationAgentRunner
{
    public async Task<DelegationExecution> ExecuteAsync(DelegationRequest request, CancellationToken cancellationToken = default)
    {
        var responseBuilder = new StringBuilder();
        var toolEvents = new List<string>();
        string? error = null;

        var provider = string.IsNullOrWhiteSpace(request.Provider) ? "openai" : request.Provider.Trim();
        var model = string.IsNullOrWhiteSpace(request.Model)
            ? RuntimeCodingAgentRunner.GetDefaultModelId(provider)
            : request.Model.Trim();
        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(request.WorkingDirectory);

        try
        {
            var previousDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(workingDirectory);
            try
            {
                var runner = RuntimeCodingAgentRunner.Create(provider, model);
                await foreach (var evt in runner.RunAsync(request.Prompt, cancellationToken).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case MessageUpdateEvent { StreamEvent: TextDeltaEvent delta }:
                            responseBuilder.Append(delta.Delta);
                            break;
                        case ToolExecutionStartEvent toolStart:
                            toolEvents.Add($"start:{toolStart.ToolName}");
                            break;
                        case ToolExecutionEndEvent toolEnd:
                            toolEvents.Add($"end:{toolEnd.ToolCallId}:{(toolEnd.Result.IsError ? "error" : "ok")}");
                            break;
                        case AgentEndEvent end when end.ErrorMessage is not null:
                            error = end.ErrorMessage;
                            break;
                    }
                }
            }
            finally
            {
                Directory.SetCurrentDirectory(previousDirectory);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }

        return new DelegationExecution(
            responseBuilder.ToString(),
            toolEvents,
            error,
            provider,
            model,
            workingDirectory,
            request.Metadata);
    }
}
