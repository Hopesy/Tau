namespace Tau.Mom;

public sealed class MomChannelMessageProcessor
{
    private readonly MomOptions _options;
    private readonly IDelegationAgentRunner _runner;
    private readonly ChannelStatusStore _statusStore;
    private readonly SlackAttachmentDownloader? _attachmentDownloader;
    private readonly MomChannelRunRegistry _runRegistry;
    private readonly ILogger<MomChannelMessageProcessor> _logger;
    private readonly MomModelSelectionResolver _selectionResolver;

    public MomChannelMessageProcessor(
        MomOptions options,
        IDelegationAgentRunner runner,
        ChannelStatusStore statusStore,
        ILogger<MomChannelMessageProcessor> logger,
        SlackAttachmentDownloader? attachmentDownloader = null,
        MomChannelRunRegistry? runRegistry = null)
    {
        _options = options;
        _runner = runner;
        _statusStore = statusStore;
        _attachmentDownloader = attachmentDownloader;
        _runRegistry = runRegistry ?? new MomChannelRunRegistry();
        _logger = logger;
        _selectionResolver = new MomModelSelectionResolver(options);
    }

    public async Task<bool> ProcessAsync(
        MomChannelMessage message,
        IMomChannelResponder responder,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var workingDirectory = ResolveChannelWorkingDirectory(message.ChannelId);
        Directory.CreateDirectory(workingDirectory);

        if (MomChannelCommands.IsStopCommand(message.Text))
        {
            await RespondToStopAsync(message, responder, workingDirectory, startedAt, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        var staleAfter = TimeSpan.FromMinutes(Math.Max(1, _options.RunningStatusStaleAfterMinutes));
        if (_statusStore.IsRunning(workingDirectory, startedAt, staleAfter))
        {
            await responder.RespondAsync(message, "_Already working. Say `stop` to cancel._", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (_attachmentDownloader is not null)
        {
            message = await _attachmentDownloader.DownloadAttachmentsAsync(message, workingDirectory, cancellationToken)
                .ConfigureAwait(false);
        }

        var selection = _selectionResolver.Resolve(message.Provider, message.Model, workingDirectory);
        var request = (message with
            {
                Provider = selection.Provider,
                Model = selection.ModelId
            })
            .ToDelegationRequest(workingDirectory);
        request = request with
        {
            Attachments = ChannelAttachmentStore.StageRequestAttachments(
                workingDirectory,
                request.Attachments,
                request.Metadata,
                startedAt,
                _logger)
        };

        var requestId = BuildRequestId(message);
        using var activeRun = _runRegistry.TryStart(
            message.ChannelId,
            requestId,
            workingDirectory,
            startedAt,
            cancellationToken);
        if (activeRun is null)
        {
            await responder.RespondAsync(message, "_Already working. Say `stop` to cancel._", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        await _statusStore.WriteRunningAsync(requestId, request, startedAt, cancellationToken)
            .ConfigureAwait(false);
        await responder.SetTypingAsync(message, true, cancellationToken).ConfigureAwait(false);
        var runtimeResponder = responder as IMomChannelMessageRuntimeResponder;
        var responseTs = runtimeResponder is null
            ? null
            : await runtimeResponder.StartResponseAsync(message, "_Thinking_ ...", cancellationToken).ConfigureAwait(false);

        DelegationExecution execution;
        var cancelledByStop = false;
        try
        {
            execution = await _runner.ExecuteAsync(request, activeRun.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (activeRun.StopRequested)
        {
            cancelledByStop = true;
            _logger.LogInformation("Cancelled channel message {ChannelId}/{Ts} after stop request.", message.ChannelId, message.Ts);
            execution = new DelegationExecution(
                "_Stopped_",
                [],
                Error: null,
                request.Provider!,
                request.Model!,
                request.WorkingDirectory!,
                request.Metadata,
                StopReason: "cancelled");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to delegate channel message {ChannelId}/{Ts}", message.ChannelId, message.Ts);
            execution = new DelegationExecution(
                string.Empty,
                [],
                ex.Message,
                request.Provider!,
                request.Model!,
                request.WorkingDirectory!,
                request.Metadata,
                StopReason: "error");
        }
        finally
        {
            await responder.SetTypingAsync(message, false, cancellationToken).ConfigureAwait(false);
        }

        var completedAt = DateTimeOffset.UtcNow;
        if (cancelledByStop)
        {
            await _statusStore.WriteCancelledAsync(requestId, request, execution, startedAt, completedAt, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _statusStore.WriteCompletedAsync(requestId, request, execution, startedAt, completedAt, cancellationToken)
                .ConfigureAwait(false);
        }

        await ChannelLogStore.AppendDelegationAsync(execution.WorkingDirectory, request, execution, startedAt, cancellationToken, _logger)
            .ConfigureAwait(false);

        var response = !string.IsNullOrWhiteSpace(execution.Response)
            ? execution.Response
            : string.IsNullOrWhiteSpace(execution.Error) ? "_No response._" : $"Error: {execution.Error}";
        var silentResponse = IsSilentResponse(response);
        if (runtimeResponder is not null && !string.IsNullOrWhiteSpace(responseTs))
        {
            if (silentResponse)
            {
                await runtimeResponder.DeleteResponseAsync(message, responseTs, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await runtimeResponder.UpdateResponseAsync(message, responseTs, response, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (!silentResponse && !string.IsNullOrWhiteSpace(message.ThreadTs))
        {
            await responder.RespondInThreadAsync(message, response, cancellationToken).ConfigureAwait(false);
        }
        else if (!silentResponse)
        {
            await responder.RespondAsync(message, response, cancellationToken).ConfigureAwait(false);
        }

        foreach (var attachment in execution.Attachments ?? [])
        {
            await responder.UploadFileAsync(message, attachment, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        return !cancelledByStop && string.IsNullOrWhiteSpace(execution.Error);
    }

    private async Task RespondToStopAsync(
        MomChannelMessage message,
        IMomChannelResponder responder,
        string workingDirectory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var stop = _runRegistry.RequestStop(message.ChannelId);
        if (stop.Accepted)
        {
            await responder.RespondAsync(message, "_Stopping..._", cancellationToken).ConfigureAwait(false);
            return;
        }

        var staleAfter = TimeSpan.FromMinutes(Math.Max(1, _options.RunningStatusStaleAfterMinutes));
        if (_statusStore.IsRunning(workingDirectory, now, staleAfter))
        {
            await responder.RespondAsync(
                    message,
                    "_Stop requested, but no active runner is attached in this process._",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await responder.RespondAsync(message, "_Nothing running_", cancellationToken).ConfigureAwait(false);
    }

    private string ResolveChannelWorkingDirectory(string channelId)
    {
        return MomChannelWorkspace.ResolveWorkingDirectory(_options.DefaultWorkingDirectory, channelId);
    }

    private static string BuildRequestId(MomChannelMessage message)
    {
        return $"channel:{MomChannelWorkspace.MakeSafePathSegment(message.ChannelId)}:{MomChannelWorkspace.MakeSafePathSegment(message.Ts)}";
    }

    private static bool IsSilentResponse(string? response)
    {
        var trimmed = response?.Trim();
        return !string.IsNullOrWhiteSpace(trimmed) &&
            (string.Equals(trimmed, "[SILENT]", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("[SILENT]", StringComparison.OrdinalIgnoreCase));
    }
}
