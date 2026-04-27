using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
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
