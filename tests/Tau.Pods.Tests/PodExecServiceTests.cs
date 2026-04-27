using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Tests;

public class PodExecServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WithEndpointPod_ReturnsUnsupported()
    {
        var service = new PodExecService((_, _) => throw new InvalidOperationException("should not start process"));
        var pod = new PodDefinition
        {
            Id = "http-pod",
            Provider = "vllm",
            Model = "gpt-oss-120b",
            Region = "lab",
            Endpoint = "http://127.0.0.1:8000/v1"
        };

        var result = await service.ExecuteAsync(pod, "ls");

        Assert.False(result.Success);
        Assert.Equal("http", result.Transport);
        Assert.Contains("do not support remote exec yet", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WithSshPod_UsesInjectedProcessResult()
    {
        var service = new PodExecService((psi, _) =>
        {
            Assert.Equal("ssh", psi.FileName, ignoreCase: true);
            Assert.Contains("pods.example.internal", psi.Arguments, StringComparison.Ordinal);
            Assert.Contains("echo hello", psi.Arguments, StringComparison.Ordinal);
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "hello\n", string.Empty));
        });

        var pod = new PodDefinition
        {
            Id = "ssh-pod",
            Provider = "ssh",
            Model = "deepseek-r1",
            Region = "lab",
            SshHost = "pods.example.internal",
            SshPort = 22
        };

        var result = await service.ExecuteAsync(pod, "echo hello");

        Assert.True(result.Success);
        Assert.Equal("ssh", result.Transport);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello\n", result.StdOut);
        Assert.Contains("ssh exec ok", result.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
