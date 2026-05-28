using System.Text;
using Tau.Ai.Observability;
using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodModelService
{
    private const string DefaultModelsPath = "$HOME/.cache/huggingface/hub";

    private readonly PodExecService _execService;
    private readonly ITauLogSink _logSink;

    public PodModelService(PodExecService? execService = null, ITauLogSink? logSink = null)
    {
        _execService = execService ?? new PodExecService();
        _logSink = logSink ?? NullTauLogSink.Instance;
    }

    public async Task<PodModelListResult> ListAsync(PodDefinition pod, CancellationToken cancellationToken = default)
    {
        var transport = GetModelTransport(pod);
        LogModelStart("list", pod.Id, transport: transport);
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var unsupportedResult = new PodModelListResult(pod.Id, false, "Model list requires SSH-based pod.", Array.Empty<PodCachedModelInfo>());
            LogModelListEnd(unsupportedResult, transport, "unsupported-transport");
            return unsupportedResult;
        }

        var modelsPath = GetModelsPath(pod);
        var root = ShellPath(modelsPath);
        var command = BuildModelListCommand(root);
        var result = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            var failureResult = new PodModelListResult(pod.Id, false, $"Model list failed: {result.Summary}", Array.Empty<PodCachedModelInfo>());
            LogModelListEnd(failureResult, transport, GetExecutionFailureKind(result), result.ExitCode);
            return failureResult;
        }

        var models = ParseCachedModels(result.StdOut);
        var summary = models.Count == 0
            ? $"No cached models on {pod.Id}."
            : $"Found {models.Count} cached model(s) on {pod.Id}.";
        var finalResult = new PodModelListResult(pod.Id, true, summary, models);
        LogModelListEnd(finalResult, transport, "none", result.ExitCode);
        return finalResult;
    }

    public Task<PodModelOperationResult> PullAsync(
        PodDefinition pod,
        string modelId,
        CancellationToken cancellationToken = default) =>
        PullAsync(pod, modelId, revision: null, cancellationToken);

    public async Task<PodModelOperationResult> PullAsync(
        PodDefinition pod,
        string modelId,
        string? revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var trimmedModelId = modelId.Trim();
        var trimmedRevision = revision?.Trim();
        var transport = GetModelTransport(pod);
        LogModelStart("pull", pod.Id, trimmedModelId, transport, trimmedRevision);
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var unsupportedResult = new PodModelOperationResult(pod.Id, false, "pull", trimmedModelId, "Model pull requires SSH-based pod.");
            LogModelOperationEnd(unsupportedResult, transport, "unsupported-transport");
            return unsupportedResult;
        }

        var root = ShellPath(GetModelsPath(pod));
        var model = ShellSingleQuote(trimmedModelId);
        var revisionArg = string.IsNullOrWhiteSpace(trimmedRevision)
            ? string.Empty
            : $" --revision {ShellSingleQuote(trimmedRevision)}";
        var command =
            $"mkdir -p {root} && " +
            $"if command -v huggingface-cli >/dev/null 2>&1; then " +
            $"HF_HUB_ENABLE_HF_TRANSFER=1 huggingface-cli download {model}{revisionArg} --cache-dir {root}; " +
            $"else HF_HUB_ENABLE_HF_TRANSFER=1 python3 -m huggingface_hub.commands.huggingface_cli download {model}{revisionArg} --cache-dir {root}; fi";

        var result = await _execService.ExecuteAsync(pod, command, cancellationToken, keepAlive: true).ConfigureAwait(false);
        var finalResult = new PodModelOperationResult(
            pod.Id,
            result.Success,
            "pull",
            trimmedModelId,
            result.Success ? $"Pulled model '{trimmedModelId}' on {pod.Id}." : $"Model pull failed: {result.Summary}",
            string.IsNullOrWhiteSpace(result.StdOut) ? null : result.StdOut,
            trimmedRevision);
        LogModelOperationEnd(finalResult, transport, GetExecutionFailureKind(result), result.ExitCode);
        return finalResult;
    }

    public async Task<PodModelOperationResult> RemoveAsync(
        PodDefinition pod,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var trimmedModelId = modelId.Trim();
        var transport = GetModelTransport(pod);
        LogModelStart("remove", pod.Id, trimmedModelId, transport);
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var unsupportedResult = new PodModelOperationResult(pod.Id, false, "remove", trimmedModelId, "Model remove requires SSH-based pod.");
            LogModelOperationEnd(unsupportedResult, transport, "unsupported-transport");
            return unsupportedResult;
        }

        var cachePath = BuildModelCachePath(GetModelsPath(pod), trimmedModelId);
        var command = $"rm -rf {cachePath} && echo {ShellSingleQuote($"removed {trimmedModelId}")}";
        var result = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);

        var finalResult = new PodModelOperationResult(
            pod.Id,
            result.Success,
            "remove",
            trimmedModelId,
            result.Success ? $"Removed model '{trimmedModelId}' from {pod.Id}." : $"Model remove failed: {result.Summary}",
            string.IsNullOrWhiteSpace(result.StdOut) ? null : result.StdOut);
        LogModelOperationEnd(finalResult, transport, GetExecutionFailureKind(result), result.ExitCode);
        return finalResult;
    }

    public async Task<PodModelStatusResult> StatusAsync(
        PodDefinition pod,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var trimmedModelId = modelId.Trim();
        var transport = GetModelTransport(pod);
        LogModelStart("status", pod.Id, trimmedModelId, transport);
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var unsupportedResult = new PodModelStatusResult(pod.Id, false, trimmedModelId, false, "Model status requires SSH-based pod.");
            LogModelStatusEnd(unsupportedResult, transport, "unsupported-transport");
            return unsupportedResult;
        }

        var cachePath = BuildModelCachePath(GetModelsPath(pod), trimmedModelId);
        var command = BuildModelStatusCommand(cachePath);
        var result = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        var output = (result.StdOut ?? string.Empty).Trim();
        var values = ParseKeyValueOutput(result.StdOut ?? string.Empty);
        var presentValue = GetValue(values, "present");
        if (presentValue is not null)
        {
            var present = presentValue.Equals("true", StringComparison.OrdinalIgnoreCase);
            var modelCachePath = GetValue(values, "model_cache_path");
            var snapshotCount = GetInt(values, "snapshot_count");
            var resolvedModelPath = GetValue(values, "resolved_model_path");
            var snapshotFailureKind = GetValue(values, "failure_kind") ?? (present ? "unknown" : "model-cache-missing");
            var summary = present
                ? BuildPresentStatusSummary(pod.Id, trimmedModelId, resolvedModelPath, snapshotFailureKind, snapshotCount)
                : $"Model '{trimmedModelId}' is missing on {pod.Id}.";

            var structuredResult = new PodModelStatusResult(
                pod.Id,
                present,
                trimmedModelId,
                present,
                summary,
                string.IsNullOrWhiteSpace(result.StdOut) ? null : result.StdOut,
                modelCachePath,
                snapshotCount,
                resolvedModelPath,
                snapshotFailureKind);
            LogModelStatusEnd(structuredResult, transport, GetStatusFailureKind(structuredResult, result), result.ExitCode);
            return structuredResult;
        }

        if (output.Equals("present", StringComparison.OrdinalIgnoreCase))
        {
            var presentResult = new PodModelStatusResult(pod.Id, true, trimmedModelId, true, $"Model '{trimmedModelId}' is cached on {pod.Id}.", result.StdOut);
            LogModelStatusEnd(presentResult, transport, "none", result.ExitCode);
            return presentResult;
        }

        if (output.Equals("missing", StringComparison.OrdinalIgnoreCase))
        {
            var missingResult = new PodModelStatusResult(pod.Id, false, trimmedModelId, false, $"Model '{trimmedModelId}' is missing on {pod.Id}.", result.StdOut);
            LogModelStatusEnd(missingResult, transport, "model-cache-missing", result.ExitCode);
            return missingResult;
        }

        var failureResult = new PodModelStatusResult(
            pod.Id,
            false,
            trimmedModelId,
            false,
            $"Model status failed: {result.Summary}",
            string.IsNullOrWhiteSpace(result.StdOut) ? null : result.StdOut);
        LogModelStatusEnd(
            failureResult,
            transport,
            result.Success ? "unknown-output" : GetExecutionFailureKind(result),
            result.ExitCode);
        return failureResult;
    }

    private void LogModelStart(
        string operation,
        string podId,
        string? modelId = null,
        string? transport = null,
        string? requestedRevision = null)
    {
        var fields = BuildModelFields(operation, podId, modelId, transport, requestedRevision);
        _logSink.Log(new TauLogEvent("pod", $"model.{operation}.start", DateTimeOffset.UtcNow, fields));
    }

    private void LogModelListEnd(
        PodModelListResult result,
        string transport,
        string failureKind,
        int? exitCode = null)
    {
        var fields = BuildModelFields("list", result.PodId, modelId: null, transport);
        fields["success"] = result.Success ? "true" : "false";
        fields["summary"] = result.Summary;
        fields["modelCount"] = result.Models.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        fields["failureKind"] = failureKind;
        if (exitCode.HasValue)
        {
            fields["exitCode"] = exitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        _logSink.Log(new TauLogEvent("pod", "model.list.end", DateTimeOffset.UtcNow, fields));
    }

    private void LogModelOperationEnd(
        PodModelOperationResult result,
        string transport,
        string failureKind,
        int? exitCode = null)
    {
        var fields = BuildModelFields(result.Operation, result.PodId, result.ModelId, transport, result.RequestedRevision);
        fields["success"] = result.Success ? "true" : "false";
        fields["summary"] = result.Summary;
        fields["failureKind"] = failureKind;
        if (exitCode.HasValue)
        {
            fields["exitCode"] = exitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        _logSink.Log(new TauLogEvent("pod", $"model.{result.Operation}.end", DateTimeOffset.UtcNow, fields));
    }

    private void LogModelStatusEnd(
        PodModelStatusResult result,
        string transport,
        string failureKind,
        int? exitCode = null)
    {
        var fields = BuildModelFields("status", result.PodId, result.ModelId, transport);
        fields["success"] = result.Success ? "true" : "false";
        fields["summary"] = result.Summary;
        fields["available"] = result.Present ? "true" : "false";
        fields["failureKind"] = failureKind;
        if (exitCode.HasValue)
        {
            fields["exitCode"] = exitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        _logSink.Log(new TauLogEvent("pod", "model.status.end", DateTimeOffset.UtcNow, fields));
    }

    private static Dictionary<string, string?> BuildModelFields(
        string operation,
        string podId,
        string? modelId,
        string? transport,
        string? requestedRevision = null)
    {
        var fields = new Dictionary<string, string?>
        {
            ["podId"] = podId,
            ["operation"] = operation
        };
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            fields["modelId"] = modelId;
        }

        if (!string.IsNullOrWhiteSpace(transport))
        {
            fields["transport"] = transport;
        }

        if (!string.IsNullOrWhiteSpace(requestedRevision))
        {
            fields["requestedRevision"] = requestedRevision;
        }

        return fields;
    }

    private static string GetModelTransport(PodDefinition pod)
    {
        if (!string.IsNullOrWhiteSpace(pod.SshHost))
        {
            return "ssh";
        }

        if (!string.IsNullOrWhiteSpace(pod.Endpoint))
        {
            return "http";
        }

        return "none";
    }

    private static string GetStatusFailureKind(PodModelStatusResult status, PodExecResult execResult)
    {
        if (!status.Success)
        {
            return status.SnapshotFailureKind == "unknown"
                ? GetExecutionFailureKind(execResult)
                : status.SnapshotFailureKind;
        }

        return status.SnapshotFailureKind == "unknown" ? "none" : status.SnapshotFailureKind;
    }

    private static string GetExecutionFailureKind(PodExecResult result)
    {
        return PodExecFailureKinds.FromResult(result);
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
            var columns = rawLine.TrimEnd('\r').Split('\t');
            var cacheDirectory = columns[0].Trim();
            if (!cacheDirectory.StartsWith("models--", StringComparison.Ordinal) || cacheDirectory.Length <= "models--".Length)
            {
                continue;
            }

            models.Add(new PodCachedModelInfo(
                CacheDirectoryToModelId(cacheDirectory),
                cacheDirectory,
                GetIntColumn(columns, 1),
                GetColumn(columns, 2),
                GetColumn(columns, 3) ?? "unknown"));
        }

        return models;
    }

    private static string BuildModelListCommand(string root) =>
        $"if [ -d {root} ]; then " +
        $"find {root} -maxdepth 1 -mindepth 1 -type d -name 'models--*' -print 2>/dev/null | sort | while IFS= read -r cache; do " +
        "name=$(basename \"$cache\"); " +
        AppendSnapshotResolutionShell(emitPresent: false) +
        "printf '%s\\t%s\\t%s\\t%s\\n' \"$name\" \"$snapshot_count\" \"$resolved_model_path\" \"$failure_kind\"; " +
        "done; fi";

    private static string BuildModelStatusCommand(string cachePath) =>
        $"cache={cachePath}; " +
        "echo \"model_cache_path=$cache\"; " +
        "if [ ! -d \"$cache\" ]; then echo present=false; echo snapshot_count=0; echo failure_kind=model-cache-missing; echo missing; exit 1; fi; " +
        "echo present=true; " +
        AppendSnapshotResolutionShell(emitPresent: true);

    private static string AppendSnapshotResolutionShell(bool emitPresent) =>
        "resolved_model_path=; failure_kind=unknown; snapshot_count=0; snapshots=\"$cache/snapshots\"; ref_file=\"$cache/refs/main\"; " +
        "if [ ! -d \"$snapshots\" ]; then failure_kind=model-snapshots-missing; else " +
        "snapshot_count=$(find \"$snapshots\" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | wc -l | tr -d ' '); " +
        "if [ \"$snapshot_count\" -eq 0 ]; then failure_kind=model-snapshot-missing; " +
        "elif [ -f \"$ref_file\" ]; then ref=$(head -n 1 \"$ref_file\" | tr -d '\\r\\n'); " +
        "if [ -n \"$ref\" ] && [ -d \"$snapshots/$ref\" ]; then resolved_model_path=\"$snapshots/$ref\"; failure_kind=none; else failure_kind=model-snapshot-ref-missing; fi; " +
        "elif [ \"$snapshot_count\" -eq 1 ]; then resolved_model_path=$(find \"$snapshots\" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | head -n 1); failure_kind=none; " +
        "else failure_kind=model-snapshot-ambiguous; fi; fi; " +
        (emitPresent
            ? "echo \"snapshot_count=$snapshot_count\"; if [ -n \"$resolved_model_path\" ]; then echo \"resolved_model_path=$resolved_model_path\"; fi; echo \"failure_kind=$failure_kind\"; "
            : string.Empty);

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

    private static IReadOnlyDictionary<string, string> ParseKeyValueOutput(string stdout)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            values[key] = line[(separator + 1)..].Trim();
        }

        return values;
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static string? GetColumn(string[] columns, int index) =>
        index < columns.Length && columns[index].Trim().Length > 0 ? columns[index].Trim() : null;

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) &&
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static int GetIntColumn(string[] columns, int index) =>
        index < columns.Length &&
        int.TryParse(columns[index].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static string BuildPresentStatusSummary(
        string podId,
        string modelId,
        string? resolvedModelPath,
        string snapshotFailureKind,
        int snapshotCount) =>
        snapshotFailureKind == "none"
            ? $"Model '{modelId}' is cached on {podId}; resolved snapshot '{resolvedModelPath}' ({snapshotCount} snapshot(s))."
            : $"Model '{modelId}' is cached on {podId}; snapshot status: {snapshotFailureKind} ({snapshotCount} snapshot(s)).";

    private static string ShellPath(string path)
    {
        const string homePrefix = "$HOME/";
        if (!path.StartsWith(homePrefix, StringComparison.Ordinal))
        {
            return ShellSingleQuote(path);
        }

        return "\"$HOME/" + ShellDoubleQuoteContent(path[homePrefix.Length..]) + "\"";
    }

    private static string ShellDoubleQuoteContent(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

    private static string ShellSingleQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
