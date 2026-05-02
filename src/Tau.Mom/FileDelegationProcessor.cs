using System.Text.Json;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;

namespace Tau.Mom;

public sealed class FileDelegationProcessor
{
    private readonly MomOptions _options;
    private readonly IDelegationAgentRunner _runner;
    private readonly ILogger<FileDelegationProcessor> _logger;
    private readonly ModelCatalog _catalog = new();

    public FileDelegationProcessor(MomOptions options, IDelegationAgentRunner runner, ILogger<FileDelegationProcessor> logger)
    {
        _options = options;
        _runner = runner;
        _logger = logger;
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

        foreach (var file in files)
        {
            await ProcessFileAsync(file, cancellationToken).ConfigureAwait(false);
        }

        return files.Length;
    }

    private async Task ProcessFileAsync(string file, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var request = await LoadRequestAsync(file, cancellationToken).ConfigureAwait(false);
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
                request.Metadata);
        }

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
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow - startedAt);

        var outFile = Path.Combine(_options.OutboxPath, Path.GetFileNameWithoutExtension(file) + ".json");
        var json = JsonSerializer.Serialize(result, MomJsonContext.Default.DelegationResult);
        await File.WriteAllTextAsync(outFile, json, cancellationToken).ConfigureAwait(false);

        var archiveName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{Path.GetFileName(file)}";
        var archivePath = Path.Combine(_options.ArchivePath, archiveName);
        File.Move(file, archivePath, overwrite: true);
    }

    private async Task<DelegationRequest> LoadRequestAsync(string file, CancellationToken cancellationToken)
    {
        if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize(json, MomJsonContext.Default.DelegationRequest);
            if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
            {
                throw new InvalidOperationException($"Delegation request '{file}' is invalid.");
            }

            var resolvedSelection = ResolveSelection(request.Provider, request.Model);
            return request with
            {
                Provider = resolvedSelection.Provider,
                Model = resolvedSelection.ModelId,
                WorkingDirectory = ResolveWorkingDirectory(request.WorkingDirectory)
            };
        }

        var prompt = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException($"Delegation request '{file}' is empty.");
        }

        var defaultSelection = ResolveSelection(null, null);
        return new DelegationRequest(
            prompt.Trim(),
            defaultSelection.Provider,
            defaultSelection.ModelId,
            ResolveWorkingDirectory(null),
            Path.GetFileNameWithoutExtension(file));
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
}
