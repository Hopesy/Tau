using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Tau.Ai.Observability;
using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Tests;

public class PodProbeServiceTests
{
    [Fact]
    public async Task ProbePodAsync_WithHttpEndpoint_ReturnsSuccessFor2xx()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, "ok"));
        var service = new PodProbeService(client);
        var pod = new PodDefinition
        {
            Id = "http-pod",
            Provider = "vllm",
            Model = "gpt-oss-120b",
            Region = "lab",
            Endpoint = "http://127.0.0.1:18000/health"
        };

        var result = await service.ProbePodAsync(pod);

        Assert.True(result.Success);
        Assert.Equal("http", result.Transport);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("http 200", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbePodAsync_LogsHttpSuccessTargetAndSummaryFields()
    {
        var sink = new CapturingLogSink();
        using var client = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, "ok"));
        var service = new PodProbeService(client, sink);
        var pod = new PodDefinition
        {
            Id = "http-pod",
            Provider = "vllm",
            Model = "gpt-oss-120b",
            Region = "lab",
            Endpoint = "http://127.0.0.1:18000/health"
        };

        var result = await service.ProbePodAsync(pod);

        Assert.True(result.Success);
        Assert.Contains(sink.Events, evt => evt.Category == "pod" && evt.Event == "probe.start");
        var end = Assert.Single(sink.Events, evt => evt.Event == "probe.end");
        Assert.Equal("http-pod", end.Fields["podId"]);
        Assert.Equal("true", end.Fields["success"]);
        Assert.Equal("http", end.Fields["transport"]);
        Assert.Equal("http 200 OK", end.Fields["summary"]);
        Assert.Equal("200", end.Fields["statusCode"]);
        Assert.Equal("http://127.0.0.1:18000/health", end.Fields["endpoint"]);
        Assert.Equal("none", end.Fields["failureKind"]);
    }

    [Fact]
    public async Task ProbePodAsync_LogsHttpErrorFailureKind()
    {
        var sink = new CapturingLogSink();
        using var client = new HttpClient(new ThrowingHttpMessageHandler(new HttpRequestException("network unavailable")));
        var service = new PodProbeService(client, sink);
        var pod = new PodDefinition
        {
            Id = "http-pod",
            Endpoint = "http://127.0.0.1:18000/health"
        };

        var result = await service.ProbePodAsync(pod);

        Assert.False(result.Success);
        Assert.Contains("http-error", result.Summary, StringComparison.OrdinalIgnoreCase);
        var end = Assert.Single(sink.Events, evt => evt.Event == "probe.end");
        Assert.Equal("false", end.Fields["success"]);
        Assert.Equal("http-error", end.Fields["failureKind"]);
        Assert.Equal("http://127.0.0.1:18000/health", end.Fields["endpoint"]);
        Assert.Null(end.Fields["statusCode"]);
    }

    [Fact]
    public async Task ProbePodAsync_WithTcpTarget_ReturnsSuccessWhenPortOpen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            var service = new PodProbeService();
            var pod = new PodDefinition
            {
                Id = "ssh-pod",
                Provider = "ssh",
                Model = "deepseek-r1",
                Region = "lab",
                SshHost = "127.0.0.1",
                SshPort = port
            };

            var result = await service.ProbePodAsync(pod);
            using var accepted = await acceptTask;

            Assert.True(result.Success);
            Assert.Equal("tcp", result.Transport);
            Assert.Equal(port, result.Port);
            Assert.Contains("connected", result.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw exception;
        }
    }

    private sealed class CapturingLogSink : ITauLogSink
    {
        public List<TauLogEvent> Events { get; } = new();

        public void Log(TauLogEvent evt) => Events.Add(evt);
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain")
            });
        }
    }
}
