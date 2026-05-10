using System.Text.Json;

namespace Tau.Mom;

public sealed class ChannelStatusStore
{
    private readonly ILogger<ChannelStatusStore> _logger;

    public ChannelStatusStore(ILogger<ChannelStatusStore> logger)
    {
        _logger = logger;
    }

    public async Task WriteRunningAsync(
        string requestFile,
        DelegationRequest request,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await WriteAsync(
            request.WorkingDirectory,
            new ChannelStatus(
                "running",
                requestFile,
                request.Provider ?? string.Empty,
                request.Model ?? string.Empty,
                request.WorkingDirectory ?? string.Empty,
                startedAt,
                startedAt,
                request.Title,
                Preview(request.Prompt),
                request.Metadata,
                request.Attachments),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteCompletedAsync(
        string requestFile,
        DelegationRequest request,
        DelegationExecution execution,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var state = string.IsNullOrWhiteSpace(execution.Error) ? "completed" : "failed";
        await WriteAsync(
            execution.WorkingDirectory,
            new ChannelStatus(
                state,
                requestFile,
                execution.Provider,
                execution.Model,
                execution.WorkingDirectory,
                startedAt,
                completedAt,
                request.Title,
                Preview(request.Prompt),
                execution.Metadata ?? request.Metadata,
                request.Attachments,
                completedAt,
                Math.Max(0, (long)(completedAt - startedAt).TotalMilliseconds),
                execution.StopReason,
                execution.Error,
                Preview(execution.Response)),
            cancellationToken).ConfigureAwait(false);
    }

    public ChannelStatus? TryRead(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(Path.GetFullPath(workingDirectory), "status.json");
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, MomJsonContext.Default.ChannelStatus);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read channel status for working directory {WorkingDirectory}", workingDirectory);
            return null;
        }
    }

    public bool IsRunning(string? workingDirectory, DateTimeOffset now, TimeSpan staleAfter)
    {
        var status = TryRead(workingDirectory);
        if (status is null || !string.Equals(status.State, "running", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var updatedAt = status.UpdatedAt == default ? status.StartedAt : status.UpdatedAt;
        return now - updatedAt < staleAfter;
    }

    private async Task WriteAsync(string? workingDirectory, ChannelStatus status, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return;
        }

        try
        {
            var path = Path.Combine(workingDirectory, "status.json");
            var json = JsonSerializer.Serialize(status, MomJsonContext.Default.ChannelStatus);
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write channel status for working directory {WorkingDirectory}", workingDirectory);
        }
    }

    private static string? Preview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();
        normalized = string.Join(" ", normalized.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 240 ? normalized : normalized[..237] + "...";
    }
}


