using System.Diagnostics;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodLifecycleService
{
    private readonly PodExecService _execService;

    public PodLifecycleService(PodExecService? execService = null)
    {
        _execService = execService ?? new PodExecService();
    }

    public async Task<PodHealthResult> HealthAsync(PodDefinition pod, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(pod.Endpoint))
        {
            return await HttpHealthCheckAsync(pod, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(pod.SshHost))
        {
            return await SshHealthCheckAsync(pod, cancellationToken).ConfigureAwait(false);
        }

        return new PodHealthResult(pod.Id, false, "none", "No endpoint or SSH host configured.");
    }

    public async Task<PodDeployResult> DeployAsync(
        PodDefinition pod, string modelId, string? name = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            return new PodDeployResult(pod.Id, false, "Deploy requires SSH-based pod.");
        }

        var deployName = NormalizeDeploymentName(name ?? modelId);
        var metadata = BuildDeploymentMetadata(modelId, deployName);
        var command = $"mkdir -p ~/.tau_pods && printf %s {ShellSingleQuote(metadata)} > ~/.tau_pods/{deployName}.json && echo {ShellSingleQuote($"deployed {deployName}")}";

        var result = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        return new PodDeployResult(
            pod.Id,
            result.Success,
            result.Success ? $"Deployed '{deployName}' on {pod.Id}." : $"Deploy failed: {result.Summary}",
            deployName);
    }

    public async Task<PodStopResult> StopAsync(
        PodDefinition pod, string deploymentName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            return new PodStopResult(pod.Id, false, "Stop requires SSH-based pod.");
        }

        var deployName = NormalizeDeploymentName(deploymentName);
        var command = $"rm -f ~/.tau_pods/{deployName}.json && echo {ShellSingleQuote($"stopped {deployName}")}";
        var result = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        return new PodStopResult(
            pod.Id,
            result.Success,
            result.Success ? $"Stopped '{deployName}' on {pod.Id}." : $"Stop failed: {result.Summary}");
    }

    public async Task<PodStopResult> RestartAsync(
        PodDefinition pod, string deploymentName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            return new PodStopResult(pod.Id, false, "Restart requires SSH-based pod.");
        }

        var deployName = NormalizeDeploymentName(deploymentName);
        var command = $"test -f ~/.tau_pods/{deployName}.json && echo {ShellSingleQuote($"restarted {deployName}")} || echo 'not found'";
        var result = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        var success = result.Success && result.StdOut.Contains("restarted");
        return new PodStopResult(
            pod.Id,
            success,
            success ? $"Restarted '{deployName}' on {pod.Id}." : $"Restart failed: deployment not found or SSH error.");
    }

    public async Task<PodLogsResult> LogsAsync(
        PodDefinition pod,
        string deploymentName,
        int tail = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            return new PodLogsResult(pod.Id, false, "Logs require SSH-based pod.");
        }

        if (tail <= 0)
        {
            tail = 100;
        }

        var deployName = NormalizeDeploymentName(deploymentName);
        var unitName = $"tau-pod-{deployName}";
        var command =
            $"if command -v journalctl >/dev/null 2>&1; then " +
            $"journalctl -u {ShellSingleQuote(unitName)} -n {tail.ToString(System.Globalization.CultureInfo.InvariantCulture)} --no-pager 2>&1; " +
            $"elif test -f ~/.tau_pods/{deployName}.log; then " +
            $"tail -n {tail.ToString(System.Globalization.CultureInfo.InvariantCulture)} ~/.tau_pods/{deployName}.log; " +
            "else echo 'no logs available' && exit 1; fi";

        var result = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return new PodLogsResult(
                pod.Id,
                false,
                $"Logs failed: {result.Summary}",
                string.IsNullOrEmpty(result.StdOut) ? null : result.StdOut);
        }

        var output = result.StdOut ?? string.Empty;
        return new PodLogsResult(
            pod.Id,
            true,
            $"Fetched {LineCount(output)} log line(s) for '{deployName}' on {pod.Id}.",
            output);
    }

    public async Task<PodDeploymentsResult> ListDeploymentsAsync(
        PodDefinition pod, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            return new PodDeploymentsResult(pod.Id, false, "Deployments require SSH-based pod.", Array.Empty<PodDeploymentInfo>());
        }

        const string command =
            "for f in ~/.tau_pods/*.json; do [ -f \"$f\" ] && cat \"$f\" && echo; done";

        var result = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return new PodDeploymentsResult(
                pod.Id,
                false,
                $"Deployments failed: {result.Summary}",
                Array.Empty<PodDeploymentInfo>());
        }

        var deployments = ParseDeployments(result.StdOut);
        var summary = deployments.Count == 0
            ? $"No deployments on {pod.Id}."
            : $"Found {deployments.Count} deployment(s) on {pod.Id}.";

        return new PodDeploymentsResult(pod.Id, true, summary, deployments);
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
