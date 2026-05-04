using System.Diagnostics;
using System.Text;
using Tau.Agent;
using Tau.Ai;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;

namespace Tau.Mom;

public sealed class RuntimeDelegationAgentRunner : IDelegationAgentRunner
{
    private readonly Func<string, string, ICodingAgentRunner> _runnerFactory;

    public RuntimeDelegationAgentRunner()
        : this(static (provider, model) => RuntimeCodingAgentRunner.Create(provider, model))
    {
    }

    public RuntimeDelegationAgentRunner(Func<string, string, ICodingAgentRunner> runnerFactory)
    {
        _runnerFactory = runnerFactory;
    }

    public async Task<DelegationExecution> ExecuteAsync(DelegationRequest request, CancellationToken cancellationToken = default)
    {
        var responseBuilder = new StringBuilder();
        var toolEvents = new List<DelegationToolEvent>();
        var pendingTools = new Dictionary<string, PendingToolCall>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();
        string? error = null;
        string? stopReason = null;
        var aggregatedUsage = new MutableUsage();

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
                var runner = _runnerFactory(provider, model);
                await foreach (var evt in runner.RunAsync(request.Prompt, cancellationToken).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case MessageUpdateEvent { StreamEvent: TextDeltaEvent delta }:
                            responseBuilder.Append(delta.Delta);
                            break;
                        case ToolExecutionStartEvent toolStart:
                            pendingTools[toolStart.ToolCallId] = new PendingToolCall(toolStart.ToolName, stopwatch.ElapsedMilliseconds);
                            toolEvents.Add(new DelegationToolEvent("start", toolStart.ToolName, toolStart.ToolCallId));
                            break;
                        case ToolExecutionEndEvent toolEnd:
                            var endMs = stopwatch.ElapsedMilliseconds;
                            string toolName;
                            long? duration;
                            if (pendingTools.TryGetValue(toolEnd.ToolCallId, out var pending))
                            {
                                toolName = pending.ToolName;
                                duration = Math.Max(0, endMs - pending.StartedMs);
                                pendingTools.Remove(toolEnd.ToolCallId);
                            }
                            else
                            {
                                toolName = string.Empty;
                                duration = null;
                            }
                            toolEvents.Add(new DelegationToolEvent(
                                "end",
                                toolName,
                                toolEnd.ToolCallId,
                                IsError: toolEnd.Result.IsError,
                                DurationMs: duration));
                            break;
                        case MessageEndEvent messageEnd:
                            ApplyAssistantMessage(messageEnd.Message, runner.Model, aggregatedUsage, ref stopReason);
                            break;
                        case AgentEndEvent end when end.ErrorMessage is not null:
                            error = end.ErrorMessage;
                            stopReason = "error";
                            break;
                    }
                }
            }
            finally
            {
                Directory.SetCurrentDirectory(previousDirectory);
            }
        }
        catch (OperationCanceledException)
        {
            stopReason = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            stopReason ??= "error";
        }

        return new DelegationExecution(
            responseBuilder.ToString(),
            toolEvents,
            error,
            provider,
            model,
            workingDirectory,
            request.Metadata,
            stopReason,
            aggregatedUsage.ToDelegationUsage());
    }

    private static void ApplyAssistantMessage(AssistantMessage message, Model resolvedModel, MutableUsage usage, ref string? stopReason)
    {
        if (message.Usage is { } u)
        {
            usage.InputTokens += u.InputTokens;
            usage.OutputTokens += u.OutputTokens;
            usage.CacheReadTokens += u.CacheReadTokens.GetValueOrDefault();
            usage.CacheWriteTokens += u.CacheWriteTokens.GetValueOrDefault();
            usage.HasCacheRead |= u.CacheReadTokens.HasValue;
            usage.HasCacheWrite |= u.CacheWriteTokens.HasValue;

            if (resolvedModel.Cost is not null)
            {
                usage.TotalCost += ModelCatalog.CalculateCost(resolvedModel, u).Total;
                usage.HasCost = true;
            }
        }

        if (message.StopReason is { } s)
        {
            stopReason = NormalizeStopReason(s);
        }
        else if (!string.IsNullOrWhiteSpace(message.ErrorMessage))
        {
            stopReason = "error";
        }
    }

    private static string NormalizeStopReason(StopReason stopReason) => stopReason switch
    {
        StopReason.EndTurn => "end_turn",
        StopReason.MaxTokens => "max_tokens",
        StopReason.ToolUse => "tool_use",
        StopReason.ContentFilter => "content_filter",
        StopReason.Error => "error",
        _ => stopReason.ToString().ToLowerInvariant()
    };

    private readonly record struct PendingToolCall(string ToolName, long StartedMs);

    private sealed class MutableUsage
    {
        public int InputTokens;
        public int OutputTokens;
        public int CacheReadTokens;
        public int CacheWriteTokens;
        public decimal TotalCost;
        public bool HasCacheRead;
        public bool HasCacheWrite;
        public bool HasCost;

        public DelegationUsage? ToDelegationUsage()
        {
            if (InputTokens == 0 && OutputTokens == 0 && CacheReadTokens == 0 && CacheWriteTokens == 0 && !HasCost)
            {
                return null;
            }

            return new DelegationUsage(
                InputTokens,
                OutputTokens,
                HasCacheRead ? CacheReadTokens : null,
                HasCacheWrite ? CacheWriteTokens : null,
                HasCost ? TotalCost : null);
        }
    }
}
