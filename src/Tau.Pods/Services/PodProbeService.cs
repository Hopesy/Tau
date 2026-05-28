using System.Diagnostics;
using System.Net.Sockets;
using Tau.Ai.Observability;
using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodProbeService
{
    private readonly HttpClient _httpClient;
    private readonly ITauLogSink _logSink;

    public PodProbeService(HttpClient? httpClient = null, ITauLogSink? logSink = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _logSink = logSink ?? NullTauLogSink.Instance;
    }

    public Task<IReadOnlyList<PodProbeResult>> ProbeAsync(PodsConfig config, CancellationToken cancellationToken = default)
    {
        return ProbeAsync(config.Pods, cancellationToken);
    }

    public async Task<IReadOnlyList<PodProbeResult>> ProbeAsync(IEnumerable<PodDefinition> pods, CancellationToken cancellationToken = default)
    {
        var tasks = pods.Select(pod => ProbePodAsync(pod, cancellationToken)).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<PodProbeResult> ProbePodAsync(PodDefinition pod, CancellationToken cancellationToken = default)
    {
        _logSink.Log(new TauLogEvent(
            "pod",
            "probe.start",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["podId"] = pod.Id,
                ["transport"] = !string.IsNullOrWhiteSpace(pod.Endpoint) ? "http" : "tcp"
            }));

        PodProbeResult result;
        if (!string.IsNullOrWhiteSpace(pod.Endpoint))
        {
            result = await ProbeEndpointAsync(pod, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await ProbeSshAsync(pod, cancellationToken).ConfigureAwait(false);
        }

        _logSink.Log(new TauLogEvent(
            "pod",
            "probe.end",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["podId"] = pod.Id,
                ["success"] = result.Success ? "true" : "false",
                ["transport"] = result.Transport,
                ["latencyMs"] = result.Latency?.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
                ["summary"] = result.Summary,
                ["statusCode"] = result.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["endpoint"] = result.Endpoint,
                ["host"] = result.Host,
                ["port"] = result.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["failureKind"] = GetProbeFailureKind(result)
            }));

        return result;
    }

    private static string GetProbeFailureKind(PodProbeResult result)
    {
        if (result.Success)
        {
            return "none";
        }

        if (result.Transport.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            return result.StatusCode.HasValue ? "http-status" : "http-error";
        }

        if (result.Transport.Equals("tcp", StringComparison.OrdinalIgnoreCase))
        {
            return "tcp-error";
        }

        return "unknown";
    }

    private async Task<PodProbeResult> ProbeEndpointAsync(PodDefinition pod, CancellationToken cancellationToken)
    {
        var endpoint = pod.Endpoint!;
        var watch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            watch.Stop();

            var success = (int)response.StatusCode is >= 200 and < 500;
            var summary = success
                ? $"http {(int)response.StatusCode} {response.ReasonPhrase}".Trim()
                : $"http {(int)response.StatusCode} {response.ReasonPhrase}".Trim();

            return new PodProbeResult(
                pod.Id,
                success,
                "http",
                summary,
                (int)response.StatusCode,
                watch.Elapsed,
                endpoint,
                null,
                null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            watch.Stop();
            return new PodProbeResult(
                pod.Id,
                false,
                "http",
                $"http-error: {ex.Message}",
                null,
                watch.Elapsed,
                endpoint,
                null,
                null);
        }
    }

    private static async Task<PodProbeResult> ProbeSshAsync(PodDefinition pod, CancellationToken cancellationToken)
    {
        var host = pod.SshHost ?? string.Empty;
        var port = pod.SshPort ?? 22;
        var watch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            watch.Stop();

            return new PodProbeResult(
                pod.Id,
                true,
                "tcp",
                "tcp connected",
                null,
                watch.Elapsed,
                null,
                host,
                port);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            watch.Stop();
            return new PodProbeResult(
                pod.Id,
                false,
                "tcp",
                $"tcp-error: {ex.Message}",
                null,
                watch.Elapsed,
                null,
                host,
                port);
        }
    }
}
