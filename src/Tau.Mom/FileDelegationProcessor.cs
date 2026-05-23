using System.Globalization;
using System.Text.Json;

namespace Tau.Mom;

public sealed class FileDelegationProcessor
{
    private readonly MomOptions _options;
    private readonly IDelegationAgentRunner _runner;
    private readonly ChannelStatusStore _statusStore;
    private readonly ILogger<FileDelegationProcessor> _logger;
    private readonly MomModelSelectionResolver _selectionResolver;

    public FileDelegationProcessor(
        MomOptions options,
        IDelegationAgentRunner runner,
        ChannelStatusStore statusStore,
        ILogger<FileDelegationProcessor> logger)
    {
        _options = options;
        _runner = runner;
        _statusStore = statusStore;
        _logger = logger;
        _selectionResolver = new MomModelSelectionResolver(options);
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.InboxPath);
        Directory.CreateDirectory(_options.OutboxPath);
        Directory.CreateDirectory(_options.ArchivePath);

        var files = Directory.EnumerateFiles(_options.InboxPath, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, _options.MaxFilesPerPoll))
            .ToArray();

        var processed = 0;
        foreach (var file in files)
        {
            if (await ProcessFileAsync(file, cancellationToken).ConfigureAwait(false))
            {
                processed++;
            }
        }

        return processed;
    }

    private async Task<bool> ProcessFileAsync(string file, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var request = await LoadRequestAsync(file, startedAt, cancellationToken).ConfigureAwait(false);
        var staleAfter = TimeSpan.FromMinutes(Math.Max(1, _options.RunningStatusStaleAfterMinutes));
        if (_statusStore.IsRunning(request.WorkingDirectory, startedAt, staleAfter))
        {
            _logger.LogInformation(
                "Skipping local delegation request {File} because working directory {WorkingDirectory} is already running.",
                file,
                request.WorkingDirectory);
            return false;
        }

        await _statusStore.WriteRunningAsync(Path.GetFileName(file), request, startedAt, cancellationToken)
            .ConfigureAwait(false);
        DelegationExecution execution;

        try
        {
            execution = await _runner.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to delegate request from {File}", file);
            execution = new DelegationExecution(
                string.Empty,
                [],
                ex.Message,
                request.Provider!,
                request.Model!,
                ResolveWorkingDirectory(request.WorkingDirectory),
                request.Metadata,
                StopReason: "error");
        }

        var completedAt = DateTimeOffset.UtcNow;
        var result = new DelegationResult(
            Path.GetFileName(file),
            request.Prompt,
            execution.Response,
            execution.ToolEvents,
            execution.Error,
            execution.Provider,
            execution.Model,
            execution.WorkingDirectory,
            execution.Metadata,
            completedAt,
            completedAt - startedAt,
            execution.StopReason,
            execution.Usage,
            request.Title,
            MergeAttachments(request.Attachments, execution.Attachments));

        var outFile = Path.Combine(_options.OutboxPath, Path.GetFileNameWithoutExtension(file) + ".json");
        var json = JsonSerializer.Serialize(result, MomJsonContext.Default.DelegationResult);
        await File.WriteAllTextAsync(outFile, json, cancellationToken).ConfigureAwait(false);
        await _statusStore.WriteCompletedAsync(Path.GetFileName(file), request, execution, startedAt, completedAt, cancellationToken)
            .ConfigureAwait(false);
        await ChannelLogStore.AppendDelegationAsync(execution.WorkingDirectory, request, execution, startedAt, cancellationToken, _logger)
            .ConfigureAwait(false);

        var archiveName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{Path.GetFileName(file)}";
        var archivePath = Path.Combine(_options.ArchivePath, archiveName);
        File.Move(file, archivePath, overwrite: true);
        return true;
    }

    private async Task<DelegationRequest> LoadRequestAsync(
        string file,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize(json, MomJsonContext.Default.DelegationRequest);
            if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
            {
                throw new InvalidOperationException($"Delegation request '{file}' is invalid.");
            }

            var workingDirectory = ResolveWorkingDirectory(request.WorkingDirectory);
            var resolvedSelection = _selectionResolver.Resolve(request.Provider, request.Model, workingDirectory);
            var channelMessage = MomChannelMessage.FromDelegationRequest(
                request with
                {
                    Provider = resolvedSelection.Provider,
                    Model = resolvedSelection.ModelId,
                    WorkingDirectory = workingDirectory,
                    Title = NormalizeOptional(request.Title)
                },
                startedAt);
            var normalizedRequest = channelMessage.ToDelegationRequest(workingDirectory);

            return normalizedRequest with
            {
                Attachments = ChannelAttachmentStore.StageRequestAttachments(
                    workingDirectory,
                    normalizedRequest.Attachments,
                    normalizedRequest.Metadata,
                    startedAt,
                    _logger)
            };
        }

        var prompt = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException($"Delegation request '{file}' is empty.");
        }

        var defaultWorkingDirectory = ResolveWorkingDirectory(null);
        var defaultSelection = _selectionResolver.Resolve(null, null, defaultWorkingDirectory);
        return new MomChannelMessage(
                "local",
                prompt,
                startedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                "local",
                Provider: defaultSelection.Provider,
                Model: defaultSelection.ModelId,
                Title: Path.GetFileNameWithoutExtension(file))
            .ToDelegationRequest(defaultWorkingDirectory);
    }

    private string ResolveWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Path.GetFullPath(_options.DefaultWorkingDirectory);
        }

        var candidate = workingDirectory.Trim();
        return Path.IsPathRooted(candidate)
            ? Path.GetFullPath(candidate)
            : Path.GetFullPath(candidate, Path.GetFullPath(_options.DefaultWorkingDirectory));
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static IReadOnlyList<string>? MergeAttachments(
        IReadOnlyList<string>? requestAttachments,
        IReadOnlyList<string>? executionAttachments)
    {
        if ((requestAttachments is null || requestAttachments.Count == 0) &&
            (executionAttachments is null || executionAttachments.Count == 0))
        {
            return null;
        }

        return [.. (requestAttachments ?? []), .. (executionAttachments ?? [])];
    }
}
