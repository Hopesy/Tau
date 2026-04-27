using System.Diagnostics;
using System.Net.Sockets;
using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodProbeService
{
    private readonly HttpClient _httpClient;

    public PodProbeService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
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
        if (!string.IsNullOrWhiteSpace(pod.Endpoint))
        {
            return await ProbeEndpointAsync(pod, cancellationToken).ConfigureAwait(false);
        }

        return await ProbeSshAsync(pod, cancellationToken).ConfigureAwait(false);
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
