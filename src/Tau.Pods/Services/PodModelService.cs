using System.Text;
using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodModelService
{
    private const string DefaultModelsPath = "$HOME/.cache/huggingface/hub";

    private readonly PodExecService _execService;

    public PodModelService(PodExecService? execService = null)
    {
        _execService = execService ?? new PodExecService();
    }

    public async Task<PodModelListResult> ListAsync(PodDefinition pod, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            return new PodModelListResult(pod.Id, false, "Model list requires SSH-based pod.", Array.Empty<PodCachedModelInfo>());
        }

        var modelsPath = GetModelsPath(pod);
        var root = ShellPath(modelsPath);
        var command = $"if [ -d {root} ]; then find {root} -maxdepth 1 -mindepth 1 -type d -name 'models--*' -printf '%f\\n' 2>/dev/null | sort; fi";
        var result = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return new PodModelListResult(pod.Id, false, $"Model list failed: {result.Summary}", Array.Empty<PodCachedModelInfo>());
        }

        var models = ParseCachedModels(result.StdOut);
        var summary = models.Count == 0
            ? $"No cached models on {pod.Id}."
            : $"Found {models.Count} cached model(s) on {pod.Id}.";
        return new PodModelListResult(pod.Id, true, summary, models);
    }

    public async Task<PodModelOperationResult> PullAsync(
        PodDefinition pod,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            return new PodModelOperationResult(pod.Id, false, "pull", modelId, "Model pull requires SSH-based pod.");
        }

        var root = ShellPath(GetModelsPath(pod));
        var model = ShellSingleQuote(modelId.Trim());
        var command =
            $"mkdir -p {root} && " +
            $"if command -v huggingface-cli >/dev/null 2>&1; then " +
            $"HF_HUB_ENABLE_HF_TRANSFER=1 huggingface-cli download {model} --cache-dir {root}; " +
            $"else HF_HUB_ENABLE_HF_TRANSFER=1 python3 -m huggingface_hub.commands.huggingface_cli download {model} --cache-dir {root}; fi";

        var result = await _execService.ExecuteAsync(pod, command, cancellationToken, keepAlive: true).ConfigureAwait(false);
        return new PodModelOperationResult(
            pod.Id,
            result.Success,
            "pull",
            modelId,
            result.Success ? $"Pulled model '{modelId}' on {pod.Id}." : $"Model pull failed: {result.Summary}",
            string.IsNullOrWhiteSpace(result.StdOut) ? null : result.StdOut);
    }

    public async Task<PodModelOperationResult> RemoveAsync(
        PodDefinition pod,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            return new PodModelOperationResult(pod.Id, false, "remove", modelId, "Model remove requires SSH-based pod.");
        }

        var cachePath = BuildModelCachePath(GetModelsPath(pod), modelId);
        var command = $"rm -rf {cachePath} && echo {ShellSingleQuote($"removed {modelId.Trim()}")}";
        var result = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);

        return new PodModelOperationResult(
            pod.Id,
            result.Success,
            "remove",
            modelId,
            result.Success ? $"Removed model '{modelId}' from {pod.Id}." : $"Model remove failed: {result.Summary}",
            string.IsNullOrWhiteSpace(result.StdOut) ? null : result.StdOut);
    }

    public async Task<PodModelStatusResult> StatusAsync(
        PodDefinition pod,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            return new PodModelStatusResult(pod.Id, false, modelId, false, "Model status requires SSH-based pod.");
        }

        var cachePath = BuildModelCachePath(GetModelsPath(pod), modelId);
        var command = $"if [ -d {cachePath} ]; then echo present; else echo missing; exit 1; fi";
        var result = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        var output = (result.StdOut ?? string.Empty).Trim();

        if (output.Equals("present", StringComparison.OrdinalIgnoreCase))
        {
            return new PodModelStatusResult(pod.Id, true, modelId, true, $"Model '{modelId}' is cached on {pod.Id}.", result.StdOut);
        }

        if (output.Equals("missing", StringComparison.OrdinalIgnoreCase))
        {
            return new PodModelStatusResult(pod.Id, false, modelId, false, $"Model '{modelId}' is missing on {pod.Id}.", result.StdOut);
        }

        return new PodModelStatusResult(
            pod.Id,
            false,
            modelId,
            false,
            $"Model status failed: {result.Summary}",
            string.IsNullOrWhiteSpace(result.StdOut) ? null : result.StdOut);
    }

    private static string GetModelsPath(PodDefinition pod) =>
        string.IsNullOrWhiteSpace(pod.ModelsPath) ? DefaultModelsPath : pod.ModelsPath.Trim();

    private static IReadOnlyList<PodCachedModelInfo> ParseCachedModels(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<PodCachedModelInfo>();
        }

        var models = new List<PodCachedModelInfo>();
        foreach (var rawLine in output.Split('\n'))
        {
            var cacheDirectory = rawLine.Trim();
            if (!cacheDirectory.StartsWith("models--", StringComparison.Ordinal) || cacheDirectory.Length <= "models--".Length)
            {
                continue;
            }

            models.Add(new PodCachedModelInfo(
                CacheDirectoryToModelId(cacheDirectory),
                cacheDirectory));
        }

        return models;
    }

    private static string BuildModelCachePath(string modelsPath, string modelId) =>
        $"{ShellPath(modelsPath.TrimEnd('/'))}/{NormalizeModelCacheDirectoryName(modelId)}";

    private static string NormalizeModelCacheDirectoryName(string modelId)
    {
        var trimmed = modelId.Trim();
        var builder = new StringBuilder("models--".Length + trimmed.Length);
        builder.Append("models--");
        foreach (var ch in trimmed)
        {
            if (ch == '/')
            {
                builder.Append("--");
            }
            else if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('-');
            }
        }

        return builder.ToString().TrimEnd('-');
    }

    private static string CacheDirectoryToModelId(string cacheDirectory) =>
        cacheDirectory["models--".Length..].Replace("--", "/", StringComparison.Ordinal);

    private static string ShellPath(string path) =>
        path.Equals(DefaultModelsPath, StringComparison.Ordinal) ? path : ShellSingleQuote(path);

    private static string ShellSingleQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
