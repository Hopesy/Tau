using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Tests;

public class PodLifecycleServiceTests
{
    private static PodExecService CreateFakeExecService(int exitCode = 0, string stdout = "ok\n", string stderr = "")
    {
        return new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(exitCode, stdout, stderr)));
    }

    [Fact]
    public async Task DeployAsync_NormalizesDeploymentNameAndShellQuotesMetadata()
    {
        System.Diagnostics.ProcessStartInfo? captured = null;
        var exec = new PodExecService((psi, _) =>
        {
            captured = psi;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "deployed safe\n", ""));
        });
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.DeployAsync(pod, "model/'quoted", "name with spaces; rm");

        Assert.True(result.Success);
        Assert.Equal("name-with-spaces--rm", result.DeploymentName);
        Assert.NotNull(captured);
        Assert.Contains("printf %s", captured!.Arguments, StringComparison.Ordinal);
        Assert.Contains("~/.tau_pods/name-with-spaces--rm.json", captured!.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("model/'quoted", captured.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("~/.tau_pods/name with spaces; rm.json", captured.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthAsync_SshPod_ReturnsHealthyOnSuccess()
    {
        var exec = CreateFakeExecService(0, "ok\n");
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "test", SshHost = "host.example.com", SshPort = 22 };

        var result = await service.HealthAsync(pod);

        Assert.True(result.Healthy);
        Assert.Equal("ssh", result.Transport);
        Assert.Contains("healthy", result.Summary);
    }

    [Fact]
    public async Task HealthAsync_SshPod_ReturnsUnhealthyOnFailure()
    {
        var exec = CreateFakeExecService(1, "", "connection refused");
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "test", SshHost = "host.example.com", SshPort = 22 };

        var result = await service.HealthAsync(pod);

        Assert.False(result.Healthy);
        Assert.Contains("unhealthy", result.Summary);
    }

    [Fact]
    public async Task HealthAsync_NoPodTransport_ReturnsNotConfigured()
    {
        var exec = CreateFakeExecService();
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "empty" };

        var result = await service.HealthAsync(pod);

        Assert.False(result.Healthy);
        Assert.Equal("none", result.Transport);
    }

    [Fact]
    public async Task DeployAsync_SshPod_ReturnsSuccess()
    {
        var exec = CreateFakeExecService(0, "deployed test-model\n");
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.DeployAsync(pod, "meta/llama-3.1-70b", "llama70b");

        Assert.True(result.Success);
        Assert.Equal("llama70b", result.DeploymentName);
        Assert.Contains("Deployed", result.Summary);
    }

    [Fact]
    public async Task DeployAsync_HttpPod_ReturnsUnsupported()
    {
        var exec = CreateFakeExecService();
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "http-pod", Endpoint = "http://localhost:8000" };

        var result = await service.DeployAsync(pod, "model-id");

        Assert.False(result.Success);
        Assert.Contains("requires SSH", result.Summary);
    }

    [Fact]
    public async Task StopAsync_SshPod_ReturnsSuccess()
    {
        var exec = CreateFakeExecService(0, "stopped llama70b\n");
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.StopAsync(pod, "llama70b");

        Assert.True(result.Success);
        Assert.Contains("Stopped", result.Summary);
    }

    [Fact]
    public async Task RestartAsync_SshPod_ReturnsSuccessWhenDeploymentExists()
    {
        var exec = CreateFakeExecService(0, "restarted llama70b\n");
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.RestartAsync(pod, "llama70b");

        Assert.True(result.Success);
        Assert.Contains("Restarted", result.Summary);
    }

    [Fact]
    public async Task RestartAsync_SshPod_ReturnsFailureWhenNotFound()
    {
        var exec = CreateFakeExecService(0, "not found\n");
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.RestartAsync(pod, "nonexistent");

        Assert.False(result.Success);
        Assert.Contains("not found", result.Summary);
    }

    [Fact]
    public async Task LogsAsync_SshPod_BuildsJournalctlCommandAndPassesThroughOutput()
    {
        System.Diagnostics.ProcessStartInfo? captured = null;
        var exec = new PodExecService((psi, _) =>
        {
            captured = psi;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "line1\nline2\nline3\n", ""));
        });
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.LogsAsync(pod, "llama-70b", tail: 50);

        Assert.True(result.Success);
        Assert.Contains("llama-70b", result.Summary);
        Assert.NotNull(result.Output);
        Assert.Contains("line2", result.Output);
        Assert.NotNull(captured);
        Assert.Contains("journalctl -u 'tau-pod-llama-70b' -n 50", captured!.Arguments, StringComparison.Ordinal);
        Assert.Contains("~/.tau_pods/llama-70b.log", captured.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogsAsync_DefaultsToHundredLinesWhenTailMissing()
    {
        System.Diagnostics.ProcessStartInfo? captured = null;
        var exec = new PodExecService((psi, _) =>
        {
            captured = psi;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "ok\n", ""));
        });
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        await service.LogsAsync(pod, "deployment");

        Assert.NotNull(captured);
        Assert.Contains(" -n 100 ", captured!.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogsAsync_NonSshPod_RejectsRequest()
    {
        var exec = CreateFakeExecService();
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "http-only", Endpoint = "https://example.com" };

        var result = await service.LogsAsync(pod, "deployment");

        Assert.False(result.Success);
        Assert.Contains("SSH-based pod", result.Summary);
    }

    [Fact]
    public async Task LogsAsync_FailureSurfacesExecSummary()
    {
        var exec = CreateFakeExecService(exitCode: 1, stdout: "", stderr: "permission denied");
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.LogsAsync(pod, "deployment");

        Assert.False(result.Success);
        Assert.Contains("Logs failed", result.Summary);
    }

    [Fact]
    public async Task ListDeploymentsAsync_NonSshPod_RejectsRequest()
    {
        var exec = CreateFakeExecService();
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "http-pod", Endpoint = "http://localhost:8000" };

        var result = await service.ListDeploymentsAsync(pod);

        Assert.False(result.Success);
        Assert.Empty(result.Deployments);
        Assert.Contains("SSH-based pod", result.Summary);
    }

    [Fact]
    public async Task ListDeploymentsAsync_ParsesMetadataJsonLines()
    {
        System.Diagnostics.ProcessStartInfo? captured = null;
        var stdout = "{\"model\":\"meta/llama-3.1-70b\",\"name\":\"llama70b\",\"status\":\"deployed\",\"ts\":\"2026-05-18T01:02:03Z\"}\n"
                     + "{\"model\":\"qwen/qwen2-7b\",\"name\":\"qwen7\",\"status\":\"deployed\",\"ts\":\"2026-05-18T02:00:00Z\"}\n";
        var exec = new PodExecService((psi, _) =>
        {
            captured = psi;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, stdout, ""));
        });
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.ListDeploymentsAsync(pod);

        Assert.True(result.Success);
        Assert.Equal(2, result.Deployments.Count);
        Assert.Equal("llama70b", result.Deployments[0].Name);
        Assert.Equal("meta/llama-3.1-70b", result.Deployments[0].Model);
        Assert.Equal("deployed", result.Deployments[0].Status);
        Assert.Equal("2026-05-18T01:02:03Z", result.Deployments[0].Timestamp);
        Assert.Equal("qwen7", result.Deployments[1].Name);
        Assert.Contains("Found 2", result.Summary);
        Assert.NotNull(captured);
        Assert.Contains("~/.tau_pods/*.json", captured!.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListDeploymentsAsync_EmptyOutput_ReturnsNoDeployments()
    {
        var exec = CreateFakeExecService(0, "", "");
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.ListDeploymentsAsync(pod);

        Assert.True(result.Success);
        Assert.Empty(result.Deployments);
        Assert.Contains("No deployments", result.Summary);
    }

    [Fact]
    public async Task ListDeploymentsAsync_SkipsMalformedLines()
    {
        var stdout = "not-json\n"
                     + "{\"name\":\"only-name\"}\n"
                     + "{ malformed json\n"
                     + "\n";
        var exec = CreateFakeExecService(0, stdout, "");
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.ListDeploymentsAsync(pod);

        Assert.True(result.Success);
        Assert.Single(result.Deployments);
        Assert.Equal("only-name", result.Deployments[0].Name);
        Assert.Null(result.Deployments[0].Model);
    }

    [Fact]
    public async Task ListDeploymentsAsync_FailureSurfacesExecSummary()
    {
        var exec = CreateFakeExecService(exitCode: 1, stdout: "", stderr: "permission denied");
        var service = new PodLifecycleService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.ListDeploymentsAsync(pod);

        Assert.False(result.Success);
        Assert.Empty(result.Deployments);
        Assert.Contains("Deployments failed", result.Summary);
    }
}
