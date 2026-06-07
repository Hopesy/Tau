using System.Diagnostics;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using Tau.Ai.Observability;
using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodLifecycleService
{
    private readonly PodExecService _execService;
    private readonly ITauLogSink _logSink;

    public PodLifecycleService(PodExecService? execService = null, ITauLogSink? logSink = null)
    {
        _execService = execService ?? new PodExecService();
        _logSink = logSink ?? NullTauLogSink.Instance;
    }

    public async Task<PodHealthResult> HealthAsync(PodDefinition pod, CancellationToken cancellationToken = default)
    {
        LogLifecycleStart("health", pod.Id, transport: GetLifecycleTransport(pod));
        PodHealthResult result;
        if (!string.IsNullOrWhiteSpace(pod.Endpoint))
        {
            result = await HttpHealthCheckAsync(pod, cancellationToken).ConfigureAwait(false);
            LogHealthEnd(result);
            return result;
        }

        if (!string.IsNullOrWhiteSpace(pod.SshHost))
        {
            result = await SshHealthCheckAsync(pod, cancellationToken).ConfigureAwait(false);
            LogHealthEnd(result);
            return result;
        }

        result = new PodHealthResult(pod.Id, false, "none", "No endpoint or SSH host configured.");
        LogHealthEnd(result);
        return result;
    }

    public async Task<PodDeployResult> DeployAsync(
        PodDefinition pod, string modelId, string? name = null, CancellationToken cancellationToken = default)
    {
        var deployName = NormalizeDeploymentName(name ?? modelId);
        LogLifecycleStart("deploy", pod.Id, deployName, modelId, GetLifecycleTransport(pod));
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var unsupportedResult = new PodDeployResult(
                pod.Id,
                false,
                "Deploy requires SSH-based pod.",
                DeploymentName: deployName,
                ModelId: modelId);
            LogDeployEnd(unsupportedResult, "unsupported-transport");
            return unsupportedResult;
        }

        var metadata = BuildDeploymentMetadata(modelId, deployName);
        var command = $"mkdir -p ~/.tau_pods && printf %s {ShellSingleQuote(metadata)} > ~/.tau_pods/{deployName}.json && echo {ShellSingleQuote($"deployed {deployName}")}";

        var execResult = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        var finalResult = new PodDeployResult(
            pod.Id,
            execResult.Success,
            execResult.Success ? $"Deployed '{deployName}' on {pod.Id}." : $"Deploy failed: {execResult.Summary}",
            DeploymentName: deployName,
            ModelId: modelId,
            Execution: execResult);
        LogDeployEnd(finalResult);
        return finalResult;
    }

    public async Task<PodStopResult> StopAsync(
        PodDefinition pod, string deploymentName, CancellationToken cancellationToken = default)
    {
        var deployName = NormalizeDeploymentName(deploymentName);
        LogLifecycleStart("stop", pod.Id, deployName, transport: GetLifecycleTransport(pod));
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var unsupportedResult = new PodStopResult(
                pod.Id,
                false,
                "Stop requires SSH-based pod.",
                DeploymentName: deployName);
            LogStopEnd("stop", unsupportedResult, "unsupported-transport");
            return unsupportedResult;
        }

        var command = $"rm -f ~/.tau_pods/{deployName}.json && echo {ShellSingleQuote($"stopped {deployName}")}";
        var execResult = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        var finalResult = new PodStopResult(
            pod.Id,
            execResult.Success,
            execResult.Success ? $"Stopped '{deployName}' on {pod.Id}." : $"Stop failed: {execResult.Summary}",
            DeploymentName: deployName,
            Execution: execResult);
        LogStopEnd("stop", finalResult);
        return finalResult;
    }

    public async Task<PodStopResult> RestartAsync(
        PodDefinition pod, string deploymentName, CancellationToken cancellationToken = default)
    {
        var deployName = NormalizeDeploymentName(deploymentName);
        LogLifecycleStart("restart", pod.Id, deployName, transport: GetLifecycleTransport(pod));
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var unsupportedResult = new PodStopResult(
                pod.Id,
                false,
                "Restart requires SSH-based pod.",
                DeploymentName: deployName);
            LogStopEnd("restart", unsupportedResult, "unsupported-transport");
            return unsupportedResult;
        }

        var command = $"test -f ~/.tau_pods/{deployName}.json && echo {ShellSingleQuote($"restarted {deployName}")} || echo 'not found'";
        var execResult = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        var success = execResult.Success && execResult.StdOut.Contains("restarted");
        var failureSummary = execResult.Success ? "deployment not found" : execResult.Summary;
        var finalResult = new PodStopResult(
            pod.Id,
            success,
            success ? $"Restarted '{deployName}' on {pod.Id}." : $"Restart failed: {failureSummary}.",
            DeploymentName: deployName,
            Execution: execResult);
        LogStopEnd("restart", finalResult, success ? null : execResult.Success ? "deployment-not-found" : null);
        return finalResult;
    }

    public async Task<PodLogsResult> LogsAsync(
        PodDefinition pod,
        string deploymentName,
        int tail = 100,
        CancellationToken cancellationToken = default)
    {
        var deployName = NormalizeDeploymentName(deploymentName);
        LogLifecycleStart(
            "logs",
            pod.Id,
            deployName,
            transport: GetLifecycleTransport(pod),
            extraFields: new Dictionary<string, string?>
            {
                ["tail"] = tail <= 0 ? "100" : tail.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        if (tail <= 0)
        {
            tail = 100;
        }

        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var unsupportedResult = new PodLogsResult(
                pod.Id,
                false,
                "Logs require SSH-based pod.",
                DeploymentName: deployName,
                Tail: tail,
                FailureKind: PodExecFailureKinds.UnsupportedTransport);
            LogLogsEnd(unsupportedResult, PodExecFailureKinds.UnsupportedTransport);
            return unsupportedResult;
        }

        var unitName = $"tau-pod-{deployName}";
        var vllmLogPath = $"~/.vllm_logs/{deployName}.log";
        var tauLogPath = $"~/.tau_pods/{deployName}.log";
        var tailValue = tail.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var command =
            $"if test -f {vllmLogPath}; then " +
            $"tail -n {tailValue} {vllmLogPath}; " +
            $"elif command -v journalctl >/dev/null 2>&1; then " +
            $"journalctl -u {ShellSingleQuote(unitName)} -n {tailValue} --no-pager 2>&1; " +
            $"elif test -f {tauLogPath}; then " +
            $"tail -n {tailValue} {tauLogPath}; " +
            "else echo 'no logs available' && exit 1; fi";

        var execResult = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        if (!execResult.Success)
        {
            var failureResult = new PodLogsResult(
                pod.Id,
                false,
                $"Logs failed: {execResult.Summary}",
                string.IsNullOrEmpty(execResult.StdOut) ? null : execResult.StdOut,
                deployName,
                tail,
                command,
                execResult.ExitCode,
                string.IsNullOrEmpty(execResult.StdErr) ? null : execResult.StdErr,
                GetExecutionFailureKind(execResult));
            LogLogsEnd(failureResult, GetExecutionFailureKind(execResult));
            return failureResult;
        }

        var output = execResult.StdOut ?? string.Empty;
        var finalResult = new PodLogsResult(
            pod.Id,
            true,
            $"Fetched {LineCount(output)} log line(s) for '{deployName}' on {pod.Id}.",
            output,
            deployName,
            tail,
            command,
            execResult.ExitCode,
            string.IsNullOrEmpty(execResult.StdErr) ? null : execResult.StdErr,
            PodExecFailureKinds.None);
        LogLogsEnd(finalResult);
        return finalResult;
    }

    public async Task<PodDeploymentsResult> ListDeploymentsAsync(
        PodDefinition pod, CancellationToken cancellationToken = default)
    {
        LogLifecycleStart("deployments", pod.Id, transport: GetLifecycleTransport(pod));
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var unsupportedResult = new PodDeploymentsResult(pod.Id, false, "Deployments require SSH-based pod.", Array.Empty<PodDeploymentInfo>());
            LogDeploymentsEnd(unsupportedResult, "unsupported-transport");
            return unsupportedResult;
        }

        const string command =
            "for f in ~/.tau_pods/*.json; do [ -f \"$f\" ] && cat \"$f\" && echo; done";

        var execResult = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        if (!execResult.Success)
        {
            var failureResult = new PodDeploymentsResult(
                pod.Id,
                false,
                $"Deployments failed: {execResult.Summary}",
                Array.Empty<PodDeploymentInfo>());
            LogDeploymentsEnd(failureResult, GetExecutionFailureKind(execResult));
            return failureResult;
        }

        var deployments = ParseDeployments(execResult.StdOut);
        var summary = deployments.Count == 0
            ? $"No deployments on {pod.Id}."
            : $"Found {deployments.Count} deployment(s) on {pod.Id}.";

        var finalResult = new PodDeploymentsResult(pod.Id, true, summary, deployments);
        LogDeploymentsEnd(finalResult);
        return finalResult;
    }

    private void LogHealthEnd(PodHealthResult result)
    {
        LogLifecycleEnd(
            "health",
            result.PodId,
            result.Healthy,
            result.Summary,
            extraFields: new Dictionary<string, string?>
            {
                ["transport"] = result.Transport,
                ["latencyMs"] = result.Latency?.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
                ["failureKind"] = result.Healthy ? "none" : result.Transport.Equals("none", StringComparison.OrdinalIgnoreCase) ? "not-configured" : $"{result.Transport}-unhealthy"
            });
    }

    private void LogDeployEnd(PodDeployResult result, string? failureKind = null)
    {
        LogLifecycleEnd(
            "deploy",
            result.PodId,
            result.Success,
            result.Summary,
            result.DeploymentName,
            result.ModelId,
            new Dictionary<string, string?>
            {
                ["exitCode"] = result.Execution?.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["failureKind"] = failureKind ?? GetExecutionFailureKind(result.Execution)
            });
    }

    private void LogStopEnd(string operation, PodStopResult result, string? failureKind = null)
    {
        LogLifecycleEnd(
            operation,
            result.PodId,
            result.Success,
            result.Summary,
            result.DeploymentName,
            extraFields: new Dictionary<string, string?>
            {
                ["exitCode"] = result.Execution?.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["failureKind"] = failureKind ?? GetExecutionFailureKind(result.Execution)
            });
    }

    private void LogLogsEnd(PodLogsResult result, string? failureKind = null)
    {
        LogLifecycleEnd(
            "logs",
            result.PodId,
            result.Success,
            result.Summary,
            result.DeploymentName,
            extraFields: new Dictionary<string, string?>
            {
                ["tail"] = result.Tail?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["lineCount"] = result.Output is null ? null : LineCount(result.Output).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["exitCode"] = result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["failureKind"] = failureKind ?? result.FailureKind
            });
    }

    private void LogDeploymentsEnd(PodDeploymentsResult result, string? failureKind = null)
    {
        LogLifecycleEnd(
            "deployments",
            result.PodId,
            result.Success,
            result.Summary,
            extraFields: new Dictionary<string, string?>
            {
                ["deploymentCount"] = result.Deployments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["failureKind"] = failureKind ?? (result.Success ? "none" : "exec-failed")
            });
    }

    private void LogLifecycleStart(
        string operation,
        string podId,
        string? deploymentName = null,
        string? modelId = null,
        string? transport = null,
        IReadOnlyDictionary<string, string?>? extraFields = null)
    {
        var fields = BuildLifecycleFields(operation, podId, deploymentName, modelId, transport, extraFields);
        _logSink.Log(new TauLogEvent("pod", $"lifecycle.{operation}.start", DateTimeOffset.UtcNow, fields));
    }

    private void LogLifecycleEnd(
        string operation,
        string podId,
        bool success,
        string summary,
        string? deploymentName = null,
        string? modelId = null,
        IReadOnlyDictionary<string, string?>? extraFields = null)
    {
        var fields = BuildLifecycleFields(operation, podId, deploymentName, modelId, null, extraFields);
        fields["success"] = success ? "true" : "false";
        fields["summary"] = summary;
        _logSink.Log(new TauLogEvent("pod", $"lifecycle.{operation}.end", DateTimeOffset.UtcNow, fields));
    }

    private static Dictionary<string, string?> BuildLifecycleFields(
        string operation,
        string podId,
        string? deploymentName,
        string? modelId,
        string? transport,
        IReadOnlyDictionary<string, string?>? extraFields)
    {
        var fields = new Dictionary<string, string?>
        {
            ["podId"] = podId,
            ["operation"] = operation
        };
        if (!string.IsNullOrWhiteSpace(deploymentName))
        {
            fields["deploymentName"] = deploymentName;
        }

        if (!string.IsNullOrWhiteSpace(modelId))
        {
            fields["modelId"] = modelId;
        }

        if (!string.IsNullOrWhiteSpace(transport))
        {
            fields["transport"] = transport;
        }

        if (extraFields is not null)
        {
            foreach (var item in extraFields)
            {
                fields[item.Key] = item.Value;
            }
        }

        return fields;
    }

    private static string GetLifecycleTransport(PodDefinition pod)
    {
        if (!string.IsNullOrWhiteSpace(pod.Endpoint))
        {
            return "http";
        }

        if (!string.IsNullOrWhiteSpace(pod.SshHost))
        {
            return "ssh";
        }

        return "none";
    }

    private static string GetExecutionFailureKind(PodExecResult? result)
    {
        return PodExecFailureKinds.FromResult(result);
    }

    private static IReadOnlyList<PodDeploymentInfo> ParseDeployments(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<PodDeploymentInfo>();
        }

        var list = new List<PodDeploymentInfo>();
        foreach (var rawLine in output.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                list.Add(new PodDeploymentInfo(
                    Name: GetString(root, "name") ?? string.Empty,
                    Model: GetString(root, "model"),
                    Status: GetString(root, "status"),
                    Timestamp: GetString(root, "ts")));
            }
            catch (JsonException)
            {
                // skip malformed deployment files
            }
        }

        return list;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int LineCount(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static async Task<PodHealthResult> HttpHealthCheckAsync(PodDefinition pod, CancellationToken cancellationToken)
    {
        var endpoint = pod.Endpoint!.TrimEnd('/');
        var healthUrl = $"{endpoint}/health";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var watch = Stopwatch.StartNew();
            using var response = await client.GetAsync(healthUrl, cancellationToken).ConfigureAwait(false);
            watch.Stop();

            return new PodHealthResult(
                pod.Id,
                response.IsSuccessStatusCode,
                "http",
                response.IsSuccessStatusCode
                    ? $"healthy ({watch.Elapsed.TotalMilliseconds:F0}ms)"
                    : $"unhealthy: {response.StatusCode}",
                watch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PodHealthResult(pod.Id, false, "http", $"unreachable: {ex.Message}");
        }
    }

    private async Task<PodHealthResult> SshHealthCheckAsync(PodDefinition pod, CancellationToken cancellationToken)
    {
        var result = await _execService.ExecuteAsync(pod, "echo ok", cancellationToken).ConfigureAwait(false);
        return new PodHealthResult(
            pod.Id,
            result.Success,
            "ssh",
            result.Success ? $"healthy ({result.Duration.TotalMilliseconds:F0}ms)" : $"unhealthy: {result.Summary}",
            result.Duration);
    }

    private static string NormalizeDeploymentName(string value)
    {
        var trimmed = value.Trim();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-');
        }

        var normalized = builder.ToString().Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(normalized) ? "deployment" : normalized;
    }

    private static string BuildDeploymentMetadata(string modelId, string deployName) =>
        "{\"model\":\"" + EscapeJsonString(modelId) +
        "\",\"name\":\"" + EscapeJsonString(deployName) +
        "\",\"status\":\"deployed\",\"ts\":\"" + EscapeJsonString(DateTimeOffset.UtcNow.ToString("O")) + "\"}";

    private static string EscapeJsonString(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string ShellSingleQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
