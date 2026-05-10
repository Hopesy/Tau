using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;

namespace Tau.Mom;

public sealed class MomChannelMessageProcessor
{
    private readonly MomOptions _options;
    private readonly IDelegationAgentRunner _runner;
    private readonly ChannelStatusStore _statusStore;
    private readonly ILogger<MomChannelMessageProcessor> _logger;
    private readonly ModelCatalog _catalog = new();

    public MomChannelMessageProcessor(
        MomOptions options,
        IDelegationAgentRunner runner,
        ChannelStatusStore statusStore,
        ILogger<MomChannelMessageProcessor> logger)
    {
        _options = options;
        _runner = runner;
        _statusStore = statusStore;
        _logger = logger;
    }

    public async Task<bool> ProcessAsync(
        MomChannelMessage message,
        IMomChannelResponder responder,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var workingDirectory = ResolveChannelWorkingDirectory(message.ChannelId);
        Directory.CreateDirectory(workingDirectory);

        if (IsStopCommand(message.Text))
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

        var selection = ResolveSelection(message.Provider, message.Model);
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

        await _statusStore.WriteRunningAsync(BuildRequestId(message), request, startedAt, cancellationToken)
            .ConfigureAwait(false);
        await responder.SetTypingAsync(message, true, cancellationToken).ConfigureAwait(false);

        DelegationExecution execution;
        try
        {
            execution = await _runner.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
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
        await _statusStore.WriteCompletedAsync(BuildRequestId(message), request, execution, startedAt, completedAt, cancellationToken)
            .ConfigureAwait(false);
        await ChannelLogStore.AppendDelegationAsync(execution.WorkingDirectory, request, execution, startedAt, cancellationToken, _logger)
            .ConfigureAwait(false);

        var response = !string.IsNullOrWhiteSpace(execution.Response)
            ? execution.Response
            : string.IsNullOrWhiteSpace(execution.Error) ? "_No response._" : $"Error: {execution.Error}";
        if (!string.IsNullOrWhiteSpace(message.ThreadTs))
        {
            await responder.RespondInThreadAsync(message, response, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await responder.RespondAsync(message, response, cancellationToken).ConfigureAwait(false);
        }

        return string.IsNullOrWhiteSpace(execution.Error);
    }

    private async Task RespondToStopAsync(
        MomChannelMessage message,
        IMomChannelResponder responder,
        string workingDirectory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var staleAfter = TimeSpan.FromMinutes(Math.Max(1, _options.RunningStatusStaleAfterMinutes));
        if (_statusStore.IsRunning(workingDirectory, now, staleAfter))
        {
            await responder.RespondAsync(
                    message,
                    "_Stop requested. Cancellation is not wired for this local runner yet._",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await responder.RespondAsync(message, "_Nothing running_", cancellationToken).ConfigureAwait(false);
    }

    private ResolvedModelSelection ResolveSelection(string? provider, string? model)
    {
        var defaultProvider = string.IsNullOrWhiteSpace(_options.DefaultProvider)
            ? RuntimeCodingAgentRunner.GetDefaultProviderId()
            : _options.DefaultProvider.Trim();
        var defaultModel = string.IsNullOrWhiteSpace(_options.DefaultModel)
            ? null
            : _options.DefaultModel.Trim();

        if (string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(defaultModel))
        {
            return _catalog.ResolveSelection(defaultProvider, defaultModel, defaultProvider);
        }

        if (string.IsNullOrWhiteSpace(provider) && string.IsNullOrWhiteSpace(model))
        {
            return _catalog.ResolveSelection(defaultProvider, null, defaultProvider);
        }

        if (!string.IsNullOrWhiteSpace(provider) && provider.Trim().Equals("google", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedModel = string.IsNullOrWhiteSpace(model)
                ? ModelCatalog.GetDefaultModelId("google-gemini-cli")
                : model.Trim();
            return _catalog.ResolveSelection("google-gemini-cli", normalizedModel, defaultProvider);
        }

        return _catalog.ResolveSelection(provider, model, defaultProvider);
    }

    private string ResolveChannelWorkingDirectory(string channelId)
    {
        var safeChannelId = MakeSafePathSegment(channelId);
        return Path.GetFullPath(safeChannelId, Path.GetFullPath(_options.DefaultWorkingDirectory));
    }

    private static string BuildRequestId(MomChannelMessage message)
    {
        return $"channel:{MakeSafePathSegment(message.ChannelId)}:{MakeSafePathSegment(message.Ts)}";
    }

    private static string MakeSafePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "channel";
        }

        var chars = value.Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_')
            .ToArray();
        var safe = new string(chars).Trim('_');
        return safe.Length == 0 ? "channel" : safe;
    }

    private static bool IsStopCommand(string text)
    {
        return string.Equals(text.Trim(), "stop", StringComparison.OrdinalIgnoreCase);
    }
}
