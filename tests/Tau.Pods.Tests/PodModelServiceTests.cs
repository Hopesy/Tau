using System.Diagnostics;
using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Tests;

public class PodModelServiceTests
{
    [Fact]
    public async Task ListAsync_NonSshPod_RejectsRequest()
    {
        var service = new PodModelService(CreateFakeExecService());
        var pod = new PodDefinition { Id = "http-pod", Endpoint = "http://localhost:8000" };

        var result = await service.ListAsync(pod);

        Assert.False(result.Success);
        Assert.Empty(result.Models);
        Assert.Contains("SSH-based pod", result.Summary);
    }

    [Fact]
    public async Task ListAsync_SshPod_BuildsFindCommandAndParsesCacheDirectories()
    {
        ProcessStartInfo? captured = null;
        var stdout = "models--Qwen--Qwen2.5-7B\nnot-a-model\nmodels--meta-llama--Llama-3.1-8B\n";
        var service = new PodModelService(new PodExecService((psi, _) =>
        {
            captured = psi;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, stdout, string.Empty));
        }));

        var result = await service.ListAsync(SshPod());

        Assert.True(result.Success);
        Assert.Equal(2, result.Models.Count);
        Assert.Equal("Qwen/Qwen2.5-7B", result.Models[0].ModelId);
        Assert.Equal("meta-llama/Llama-3.1-8B", result.Models[1].ModelId);
        Assert.NotNull(captured);
        var command = RemoteCommand(captured!);
        Assert.Contains("find '/models/hf' -maxdepth 1 -mindepth 1 -type d -name 'models--*'", command, StringComparison.Ordinal);
        Assert.Contains("-printf '%f\\n'", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_SshPod_UsesHuggingFaceCliAndKeepAliveOptions()
    {
        ProcessStartInfo? captured = null;
        var service = new PodModelService(new PodExecService((psi, _) =>
        {
            captured = psi;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "downloaded\n", string.Empty));
        }));

        var result = await service.PullAsync(SshPod(), "meta-llama/Llama-3.1-8B");

        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.Contains("ServerAliveInterval=30", captured!.ArgumentList);
        Assert.Contains("ServerAliveCountMax=120", captured.ArgumentList);
        var command = RemoteCommand(captured);
        Assert.Contains("mkdir -p '/models/hf'", command, StringComparison.Ordinal);
        Assert.Contains("huggingface-cli download 'meta-llama/Llama-3.1-8B' --cache-dir '/models/hf'", command, StringComparison.Ordinal);
        Assert.Contains("python3 -m huggingface_hub.commands.huggingface_cli download 'meta-llama/Llama-3.1-8B'", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveAsync_SshPod_NormalizesModelCachePath()
    {
        ProcessStartInfo? captured = null;
        var service = new PodModelService(new PodExecService((psi, _) =>
        {
            captured = psi;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "removed\n", string.Empty));
        }));

        var result = await service.RemoveAsync(SshPod(), "meta-llama/Llama 3.1");

        Assert.True(result.Success);
        Assert.NotNull(captured);
        var command = RemoteCommand(captured!);
        Assert.Contains("rm -rf '/models/hf'/models--meta-llama--Llama-3.1", command, StringComparison.Ordinal);
        Assert.Contains("echo 'removed meta-llama/Llama 3.1'", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusAsync_SshPod_ReturnsPresentWhenCacheDirectoryExists()
    {
        ProcessStartInfo? captured = null;
        var service = new PodModelService(new PodExecService((psi, _) =>
        {
            captured = psi;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "present\n", string.Empty));
        }));

        var result = await service.StatusAsync(SshPod(), "meta-llama/Llama-3.1-8B");

        Assert.True(result.Success);
        Assert.True(result.Present);
        Assert.NotNull(captured);
        Assert.Contains("[ -d '/models/hf'/models--meta-llama--Llama-3.1-8B ]", RemoteCommand(captured!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusAsync_SshPod_ReturnsMissingWithoutTreatingItAsTransportFailure()
    {
        var service = new PodModelService(new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(1, "missing\n", string.Empty))));

        var result = await service.StatusAsync(SshPod(), "meta-llama/Llama-3.1-8B");

        Assert.False(result.Success);
        Assert.False(result.Present);
        Assert.Contains("missing", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ssh exec failed", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PullAsync_SurfacesExecFailure()
    {
        var service = new PodModelService(new PodExecService((_, _) =>
            Task.FromResult(PodExecService.ProcessExecutionResult.StartFailed("ssh process start failed: Win32Exception: not found"))));

        var result = await service.PullAsync(SshPod(), "meta-llama/Llama-3.1-8B");

        Assert.False(result.Success);
        Assert.Contains("ssh process start failed", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private static PodExecService CreateFakeExecService() =>
        new((_, _) => Task.FromResult(new PodExecService.ProcessExecutionResult(0, string.Empty, string.Empty)));

    private static string RemoteCommand(ProcessStartInfo psi) =>
        psi.ArgumentList[psi.ArgumentList.Count - 1];

    private static PodDefinition SshPod() => new()
    {
        Id = "gpu-1",
        Provider = "ssh",
        Model = "deepseek-r1",
        Region = "lab",
        SshHost = "pods.example.internal",
        SshPort = 2222,
        ModelsPath = "/models/hf"
    };
}
