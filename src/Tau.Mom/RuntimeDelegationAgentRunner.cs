using System.Diagnostics;
using System.Text;
using Tau.Agent;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;

namespace Tau.Mom;

public sealed class RuntimeDelegationAgentRunner : IDelegationAgentRunner
{
    private readonly Func<string, string, string, Action<string, string?>, ICodingAgentRunner> _runnerFactory;
    private readonly MomOptions _options;
    private readonly ITauLogSink _logSink;
    private readonly MomModelSelectionResolver _selectionResolver;

    public RuntimeDelegationAgentRunner()
        : this(new MomOptions())
    {
    }

    public RuntimeDelegationAgentRunner(MomOptions options)
        : this(options, CreateDefaultRunnerFactory(options, logSink: null), logSink: null)
    {
    }

    public RuntimeDelegationAgentRunner(MomOptions options, ITauLogSink logSink)
        : this(options, CreateDefaultRunnerFactory(options, logSink), logSink)
    {
    }

    public RuntimeDelegationAgentRunner(Func<string, string, ICodingAgentRunner> runnerFactory)
        : this(new MomOptions(), runnerFactory)
    {
    }

    public RuntimeDelegationAgentRunner(MomOptions options, Func<string, string, ICodingAgentRunner> runnerFactory)
        : this(options, (provider, model, _, _) => runnerFactory(provider, model))
    {
    }

    public RuntimeDelegationAgentRunner(
        MomOptions options,
        Func<string, string, string, Action<string, string?>, ICodingAgentRunner> runnerFactory)
        : this(options, runnerFactory, logSink: null)
    {
    }

    public RuntimeDelegationAgentRunner(
        MomOptions options,
        Func<string, string, string, Action<string, string?>, ICodingAgentRunner> runnerFactory,
        ITauLogSink? logSink)
    {
        _options = options;
        _runnerFactory = runnerFactory;
        _logSink = logSink ?? NullTauLogSink.Instance;
        _selectionResolver = new MomModelSelectionResolver(options);
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
        var attachedFiles = new List<string>();

        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(request.WorkingDirectory);
        var selection = _selectionResolver.Resolve(request.Provider, request.Model, workingDirectory);
        var provider = selection.Provider;
        var model = selection.ModelId;
        var logContext = CreateDelegationLogContext(request).EnsureCorrelationId();

        LogMomEvent(
            "delegation.start",
            new Dictionary<string, string?>
            {
                ["provider"] = provider,
                ["model"] = model,
                ["workingDirectory"] = workingDirectory,
                ["title"] = request.Title
            },
            logContext);

        try
        {
            var workspaceLayout = ChannelWorkspaceLayout.For(_options, workingDirectory);
            workspaceLayout.EnsureDirectories();
            var previousDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(workingDirectory);
            try
            {
                var runner = _runnerFactory(provider, model, workingDirectory, (path, _) => attachedFiles.Add(path));
                var sessionStore = new ChannelSessionStore(workingDirectory);
                var requestedSessionName = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
                var snapshot = sessionStore.Load(provider, model, requestedSessionName);
                if (snapshot.Messages.Count > 0)
                {
                    runner.RestoreSession(snapshot);
                }

                runner.SessionName = snapshot.Name;
                var promptParts = BuildDelegationPromptParts(request, workingDirectory);
                ChannelPromptDebugStore.Write(
                    workingDirectory,
                    promptParts,
                    runner.Messages,
                    request,
                    provider,
                    model,
                    runner.SessionName);

                await foreach (var evt in runner.RunAsync(promptParts.RunnerInput, logContext, cancellationToken).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case MessageUpdateEvent { StreamEvent: TextDeltaEvent delta }:
                            responseBuilder.Append(delta.Delta);
                            break;
                        case MessageStartEvent:
                            LogResponseStart(provider, model, workingDirectory, logContext);
                            break;
                        case ToolExecutionStartEvent toolStart:
                            pendingTools[toolStart.ToolCallId] = new PendingToolCall(toolStart.ToolName, stopwatch.ElapsedMilliseconds);
                            toolEvents.Add(new DelegationToolEvent("start", toolStart.ToolName, toolStart.ToolCallId));
                            LogToolStart(provider, model, workingDirectory, toolStart.ToolCallId, toolStart.ToolName, logContext);
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
                                toolName = toolEnd.ToolName ?? string.Empty;
                                duration = null;
                            }
                            toolEvents.Add(new DelegationToolEvent(
                                "end",
                                toolName,
                                toolEnd.ToolCallId,
                                IsError: toolEnd.Result.IsError,
                                DurationMs: duration));
                            LogToolEnd(provider, model, workingDirectory, toolEnd.ToolCallId, toolName, toolEnd.Result, duration, logContext);
                            break;
                        case MessageEndEvent { Message: AssistantMessage assistantMessage }:
                            ApplyAssistantMessage(assistantMessage, runner.Model, aggregatedUsage, ref stopReason);
                            LogResponseEnd(provider, model, workingDirectory, assistantMessage, stopReason, logContext);
                            if (assistantMessage.Usage is { } usage)
                            {
                                LogUsage(provider, model, workingDirectory, runner.Model, usage, logContext);
                            }
                            break;
                        case AgentEndEvent end when end.ErrorMessage is not null:
                            error = end.ErrorMessage;
                            stopReason = "error";
                            break;
                    }
                }

                sessionStore.Save(runner);
            }
            finally
            {
                Directory.SetCurrentDirectory(previousDirectory);
            }
        }
        catch (OperationCanceledException)
        {
            stopReason = "cancelled";
            LogDelegationEnd(provider, model, workingDirectory, stopReason, error, stopwatch.ElapsedMilliseconds, toolEvents, logContext);
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            stopReason ??= "error";
        }

        LogDelegationEnd(provider, model, workingDirectory, stopReason, error, stopwatch.ElapsedMilliseconds, toolEvents, logContext);

        return new DelegationExecution(
            responseBuilder.ToString(),
            toolEvents,
            error,
            provider,
            model,
            workingDirectory,
            request.Metadata,
            stopReason,
            aggregatedUsage.ToDelegationUsage(),
            attachedFiles.Count == 0 ? null : attachedFiles);
    }

    private static Func<string, string, string, Action<string, string?>, ICodingAgentRunner> CreateDefaultRunnerFactory(MomOptions options, ITauLogSink? logSink)
    {
        return (provider, model, workingDirectory, attachFile) =>
        {
            var executor = MomSandboxExecutorFactory.Create(options, workingDirectory);
            var tools = MomToolSet.Create(executor, attachFile);
            return RuntimeCodingAgentRunner.Create(provider, model, toolsOverride: tools, logSink: logSink);
        };
    }

    private static TauRuntimeLogContext CreateDelegationLogContext(DelegationRequest request)
    {
        var requestId = GetMetadataValue(request.Metadata, "requestId", "request_id", "id", "eventId", "event_id");
        var messageId = requestId
            ?? GetMetadataValue(request.Metadata, "ts", "slackTs", "slack_ts", "messageTs", "message_ts");
        var correlationId = requestId ?? messageId ?? Guid.NewGuid().ToString("N");
        var sessionId = BuildSessionId(
            GetMetadataValue(request.Metadata, "channel", "channelId", "channel_id"),
            GetMetadataValue(request.Metadata, "threadTs", "thread_ts")
                ?? GetMetadataValue(request.Metadata, "ts", "slackTs", "slack_ts", "messageTs", "message_ts"));

        return new TauRuntimeLogContext(correlationId, sessionId, messageId ?? correlationId);
    }

    private static string? BuildSessionId(string? channel, string? threadOrMessage)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            return null;
        }

        var normalizedChannel = channel.Trim();
        return string.IsNullOrWhiteSpace(threadOrMessage)
            ? normalizedChannel
            : $"{normalizedChannel}:{threadOrMessage.Trim()}";
    }

    private static string? GetMetadataValue(IReadOnlyDictionary<string, string>? metadata, params string[] keys)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var exactValue) && !string.IsNullOrWhiteSpace(exactValue))
            {
                return exactValue.Trim();
            }
        }

        foreach (var key in keys)
        {
            foreach (var pair in metadata)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                {
                    return pair.Value.Trim();
                }
            }
        }

        return null;
    }

    private void LogDelegationEnd(
        string provider,
        string model,
        string workingDirectory,
        string? stopReason,
        string? error,
        long elapsedMs,
        IReadOnlyList<DelegationToolEvent> toolEvents,
        TauRuntimeLogContext logContext)
    {
        LogMomEvent(
            "delegation.end",
            new Dictionary<string, string?>
            {
                ["provider"] = provider,
                ["model"] = model,
                ["workingDirectory"] = workingDirectory,
                ["stopReason"] = stopReason,
                ["error"] = error,
                ["elapsedMs"] = elapsedMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["toolCalls"] = toolEvents.Count(e => e.Phase == "start").ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            logContext);
    }

    private DelegationPromptParts BuildDelegationPromptParts(DelegationRequest request, string workingDirectory)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(request.Title);
        var hasMetadata = request.Metadata is { Count: > 0 };
        var hasAttachments = request.Attachments is { Count: > 0 };
        var channelHistory = BuildChannelHistory(workingDirectory, request.Metadata);
        var hasChannelHistory = !string.IsNullOrWhiteSpace(channelHistory);
        var workspaceMemory = BuildWorkspaceMemory(workingDirectory);
        var hasWorkspaceMemory = !string.IsNullOrWhiteSpace(workspaceMemory);
        var workspaceLayout = ChannelWorkspaceLayout.For(_options, workingDirectory);
        var systemLog = workspaceLayout.ReadSystemLog();
        var hasSystemLog = !string.IsNullOrWhiteSpace(systemLog);
        var momRuntimeContext = MomRuntimeContext.Build(_options, workingDirectory).Trim();
        var builder = new StringBuilder();
        builder.AppendLine(momRuntimeContext);
        builder.AppendLine();

        if (!hasTitle && !hasMetadata && !hasAttachments && !hasChannelHistory && !hasWorkspaceMemory && !hasSystemLog)
        {
            builder.Append(request.Prompt);
            return new DelegationPromptParts(momRuntimeContext, null, builder.ToString());
        }

        var delegationBuilder = new StringBuilder();
        delegationBuilder.AppendLine("<delegation_context>");
        if (hasTitle)
        {
            delegationBuilder.Append("title: ").AppendLine(request.Title!.Trim());
        }

        if (hasMetadata)
        {
            delegationBuilder.AppendLine("metadata:");
            foreach (var pair in request.Metadata!.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                delegationBuilder.Append("- ").Append(pair.Key).Append(": ").AppendLine(pair.Value);
            }
        }

        if (hasAttachments)
        {
            delegationBuilder.AppendLine("attachments:");
            foreach (var attachment in request.Attachments!)
            {
                delegationBuilder.Append("- ").AppendLine(attachment);
            }
            delegationBuilder.AppendLine("Use these attachment paths as local file context when relevant.");
        }

        if (hasChannelHistory)
        {
            delegationBuilder.AppendLine("channel_history:");
            delegationBuilder.AppendLine(channelHistory!.Trim());
        }

        if (hasWorkspaceMemory)
        {
            delegationBuilder.AppendLine("workspace_memory:");
            delegationBuilder.AppendLine(workspaceMemory!.Trim());
        }

        if (hasSystemLog)
        {
            delegationBuilder.AppendLine("system_configuration_log:");
            delegationBuilder.AppendLine(systemLog!.Trim());
        }

        delegationBuilder.AppendLine("</delegation_context>");
        var delegationContext = delegationBuilder.ToString().Trim();
        builder.AppendLine(delegationContext);
        builder.AppendLine();
        builder.Append(request.Prompt);
        return new DelegationPromptParts(momRuntimeContext, delegationContext, builder.ToString());
    }

    private static string? BuildChannelHistory(string? workingDirectory, IReadOnlyDictionary<string, string>? metadata)
    {
        return ChannelLogStore.BuildHistory(workingDirectory, metadata);
    }

    private static string? BuildWorkspaceMemory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        var sections = new List<string>();
        var currentDirectory = Path.GetFullPath(workingDirectory);
        var parentDirectory = Directory.GetParent(currentDirectory)?.FullName;

        AddMemorySection(currentDirectory, "Current Workspace Memory", sections);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            AddMemorySection(parentDirectory, "Parent Workspace Memory", sections);
        }

        if (sections.Count == 0)
        {
            return null;
        }

        return string.Join("\n\n", sections);
    }

    private static void AddMemorySection(string directory, string heading, ICollection<string> sections)
    {
        var memoryPath = Path.Combine(directory, "MEMORY.md");
        if (!File.Exists(memoryPath))
        {
            return;
        }

        try
        {
            var content = File.ReadAllText(memoryPath).Trim();
            if (!string.IsNullOrWhiteSpace(content))
            {
                sections.Add($"### {heading}\n{content}");
            }
        }
        catch
        {
            // Ignore unreadable memory files and continue with the rest of the context.
        }
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

    private void LogToolStart(
        string provider,
        string model,
        string workingDirectory,
        string toolCallId,
        string toolName,
        TauRuntimeLogContext logContext)
    {
        LogMomEvent(
            "tool.start",
            new Dictionary<string, string?>
            {
                ["provider"] = provider,
                ["model"] = model,
                ["workingDirectory"] = workingDirectory,
                ["toolCallId"] = toolCallId,
                ["toolName"] = toolName
            },
            logContext);
    }

    private void LogToolEnd(
        string provider,
        string model,
        string workingDirectory,
        string toolCallId,
        string toolName,
        ToolResult result,
        long? durationMs,
        TauRuntimeLogContext logContext)
    {
        LogMomEvent(
            "tool.end",
            new Dictionary<string, string?>
            {
                ["provider"] = provider,
                ["model"] = model,
                ["workingDirectory"] = workingDirectory,
                ["toolCallId"] = toolCallId,
                ["toolName"] = toolName,
                ["isError"] = result.IsError ? "true" : "false",
                ["durationMs"] = FormatInvariant(durationMs),
                ["preview"] = PreviewToolResult(result)
            },
            logContext);
    }

    private void LogResponseStart(string provider, string model, string workingDirectory, TauRuntimeLogContext logContext)
    {
        LogMomEvent(
            "response.start",
            new Dictionary<string, string?>
            {
                ["provider"] = provider,
                ["model"] = model,
                ["workingDirectory"] = workingDirectory
            },
            logContext);
    }

    private void LogResponseEnd(
        string provider,
        string model,
        string workingDirectory,
        AssistantMessage message,
        string? stopReason,
        TauRuntimeLogContext logContext)
    {
        var text = ExtractText(message.Content);
        LogMomEvent(
            "response.end",
            new Dictionary<string, string?>
            {
                ["provider"] = provider,
                ["model"] = model,
                ["workingDirectory"] = workingDirectory,
                ["stopReason"] = stopReason,
                ["characters"] = FormatInvariant(text.Length),
                ["preview"] = Preview(text)
            },
            logContext);
    }

    private void LogUsage(
        string provider,
        string model,
        string workingDirectory,
        Model resolvedModel,
        Usage usage,
        TauRuntimeLogContext logContext)
    {
        var fields = new Dictionary<string, string?>
        {
            ["provider"] = provider,
            ["model"] = model,
            ["workingDirectory"] = workingDirectory,
            ["inputTokens"] = FormatInvariant(usage.InputTokens),
            ["outputTokens"] = FormatInvariant(usage.OutputTokens),
            ["cacheReadTokens"] = FormatInvariant(usage.CacheReadTokens),
            ["cacheWriteTokens"] = FormatInvariant(usage.CacheWriteTokens),
            ["serviceTier"] = usage.ServiceTier
        };

        if (resolvedModel.Cost is not null)
        {
            fields["totalCost"] = ModelCatalog.CalculateCost(resolvedModel, usage)
                .Total
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        LogMomEvent("usage", fields, logContext);
    }

    private void LogMomEvent(string eventName, Dictionary<string, string?> fields, TauRuntimeLogContext logContext)
    {
        logContext.AddTo(fields);
        _logSink.Log(new TauLogEvent("mom", eventName, DateTimeOffset.UtcNow, fields));
    }

    private static string PreviewToolResult(ToolResult result)
    {
        return Preview(ExtractText(result.Content));
    }

    private static string ExtractText(IReadOnlyList<ContentBlock> content)
    {
        return string.Join(
            "\n",
            content.OfType<TextContent>().Select(static text => text.Text));
    }

    private static string Preview(string value)
    {
        const int maxLength = 200;
        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength] + "...";
    }

    private static string? FormatInvariant(long? value)
    {
        return value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? FormatInvariant(int? value)
    {
        return value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatInvariant(int value)
    {
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NormalizeStopReason(StopReason stopReason) => stopReason switch
    {
        StopReason.EndTurn => "end_turn",
        StopReason.MaxTokens => "max_tokens",
        StopReason.ToolUse => "tool_use",
        StopReason.ContentFilter => "content_filter",
        StopReason.Error => "error",
        StopReason.Aborted => "aborted",
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
