using System.Diagnostics;
using Tau.Ai.Observability;
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
        var stdout =
            "models--Qwen--Qwen2.5-7B\t1\t/models/hf/models--Qwen--Qwen2.5-7B/snapshots/rev-qwen\tnone\n" +
            "not-a-model\n" +
            "models--meta-llama--Llama-3.1-8B\t2\t\tmodel-snapshot-ambiguous\n";
        var service = new PodModelService(new PodExecService((psi, _) =>
        {
            captured = psi;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, stdout, string.Empty));
        }));

        var result = await service.ListAsync(SshPod());

        Assert.True(result.Success);
        Assert.Equal(2, result.Models.Count);
        Assert.Equal("Qwen/Qwen2.5-7B", result.Models[0].ModelId);
        Assert.Equal(1, result.Models[0].SnapshotCount);
        Assert.Equal("/models/hf/models--Qwen--Qwen2.5-7B/snapshots/rev-qwen", result.Models[0].ResolvedModelPath);
        Assert.Equal("none", result.Models[0].SnapshotFailureKind);
        Assert.Equal("meta-llama/Llama-3.1-8B", result.Models[1].ModelId);
        Assert.Equal(2, result.Models[1].SnapshotCount);
        Assert.Null(result.Models[1].ResolvedModelPath);
        Assert.Equal("model-snapshot-ambiguous", result.Models[1].SnapshotFailureKind);
        Assert.NotNull(captured);
        var command = RemoteCommand(captured!);
        Assert.Contains("find '/models/hf' -maxdepth 1 -mindepth 1 -type d -name 'models--*' -print", command, StringComparison.Ordinal);
        Assert.Contains("snapshots=\"$cache/snapshots\"", command, StringComparison.Ordinal);
        Assert.Contains("printf '%s\\t%s\\t%s\\t%s\\n'", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAsync_LogsModelRuntimeEvents()
    {
        var sink = new CapturingLogSink();
        var service = new PodModelService(new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                0,
                "models--Qwen--Qwen2.5-7B\t1\t/models/hf/models--Qwen--Qwen2.5-7B/snapshots/rev-qwen\tnone\n",
                string.Empty))), sink);

        var result = await service.ListAsync(SshPod());

        Assert.True(result.Success);
        var start = Assert.Single(sink.Events, evt => evt.Event == "model.list.start");
        Assert.Equal("pod", start.Category);
        Assert.Equal("gpu-1", start.Fields["podId"]);
        Assert.Equal("list", start.Fields["operation"]);
        Assert.Equal("ssh", start.Fields["transport"]);

        var end = Assert.Single(sink.Events, evt => evt.Event == "model.list.end");
        Assert.Equal("gpu-1", end.Fields["podId"]);
        Assert.Equal("list", end.Fields["operation"]);
        Assert.Equal("ssh", end.Fields["transport"]);
        Assert.Equal("true", end.Fields["success"]);
        Assert.Equal("1", end.Fields["modelCount"]);
        Assert.Equal("0", end.Fields["exitCode"]);
        Assert.Equal("none", end.Fields["failureKind"]);
        Assert.Equal("Found 1 cached model(s) on gpu-1.", end.Fields["summary"]);
        Assert.False(end.Fields.ContainsKey("command"));
        Assert.False(end.Fields.ContainsKey("stdout"));
        Assert.False(end.Fields.ContainsKey("stderr"));
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
    public async Task PullAsync_SshPod_AddsRevisionToBothDownloadPaths()
    {
        ProcessStartInfo? captured = null;
        var service = new PodModelService(new PodExecService((psi, _) =>
        {
            captured = psi;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "downloaded\n", string.Empty));
        }));

        var result = await service.PullAsync(SshPod(), "meta-llama/Llama-3.1-8B", "refs/pr/17");

        Assert.True(result.Success);
        Assert.NotNull(captured);
        var command = RemoteCommand(captured!);
        Assert.Contains("huggingface-cli download 'meta-llama/Llama-3.1-8B' --revision 'refs/pr/17' --cache-dir '/models/hf'", command, StringComparison.Ordinal);
        Assert.Contains("python3 -m huggingface_hub.commands.huggingface_cli download 'meta-llama/Llama-3.1-8B' --revision 'refs/pr/17' --cache-dir '/models/hf'", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_LogsUnsupportedTransportFailureKind()
    {
        var sink = new CapturingLogSink();
        var service = new PodModelService(CreateFakeExecService(), sink);
        var pod = new PodDefinition { Id = "http-pod", Endpoint = "http://localhost:8000" };

        var result = await service.PullAsync(pod, "meta-llama/Llama-3.1-8B");

        Assert.False(result.Success);
        var start = Assert.Single(sink.Events, evt => evt.Event == "model.pull.start");
        Assert.Equal("pod", start.Category);
        Assert.Equal("http-pod", start.Fields["podId"]);
        Assert.Equal("pull", start.Fields["operation"]);
        Assert.Equal("meta-llama/Llama-3.1-8B", start.Fields["modelId"]);
        Assert.Equal("http", start.Fields["transport"]);

        var end = Assert.Single(sink.Events, evt => evt.Event == "model.pull.end");
        Assert.Equal("false", end.Fields["success"]);
        Assert.Equal("unsupported-transport", end.Fields["failureKind"]);
        Assert.Equal("meta-llama/Llama-3.1-8B", end.Fields["modelId"]);
        Assert.Equal("http", end.Fields["transport"]);
        Assert.False(end.Fields.ContainsKey("command"));
        Assert.False(end.Fields.ContainsKey("stdout"));
        Assert.False(end.Fields.ContainsKey("stderr"));
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
            return Task.FromResult(new PodExecService.ProcessExecutionResult(
                0,
                "model_cache_path=/models/hf/models--meta-llama--Llama-3.1-8B\npresent=true\nsnapshot_count=1\nresolved_model_path=/models/hf/models--meta-llama--Llama-3.1-8B/snapshots/rev-main\nfailure_kind=none\n",
                string.Empty));
        }));

        var result = await service.StatusAsync(SshPod(), "meta-llama/Llama-3.1-8B");

        Assert.True(result.Success);
        Assert.True(result.Present);
        Assert.Equal("/models/hf/models--meta-llama--Llama-3.1-8B", result.ModelCachePath);
        Assert.Equal(1, result.SnapshotCount);
        Assert.Equal("/models/hf/models--meta-llama--Llama-3.1-8B/snapshots/rev-main", result.ResolvedModelPath);
        Assert.Equal("none", result.SnapshotFailureKind);
        Assert.Contains("resolved snapshot", result.Summary, StringComparison.Ordinal);
        Assert.NotNull(captured);
        var command = RemoteCommand(captured!);
        Assert.Contains("cache='/models/hf'/models--meta-llama--Llama-3.1-8B", command, StringComparison.Ordinal);
        Assert.Contains("snapshot_count=", command, StringComparison.Ordinal);
        Assert.Contains("resolved_model_path=", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusAsync_LogsAvailableFieldAndSnapshotFailureKind()
    {
        var sink = new CapturingLogSink();
        var service = new PodModelService(new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                0,
                "model_cache_path=/models/hf/models--org--model\npresent=true\nsnapshot_count=2\nfailure_kind=model-snapshot-ambiguous\n",
                string.Empty))), sink);

        var result = await service.StatusAsync(SshPod(), "org/model");

        Assert.True(result.Success);
        var start = Assert.Single(sink.Events, evt => evt.Event == "model.status.start");
        Assert.Equal("org/model", start.Fields["modelId"]);
        Assert.Equal("ssh", start.Fields["transport"]);

        var end = Assert.Single(sink.Events, evt => evt.Event == "model.status.end");
        Assert.Equal("true", end.Fields["success"]);
        Assert.Equal("true", end.Fields["available"]);
        Assert.Equal("model-snapshot-ambiguous", end.Fields["failureKind"]);
        Assert.Equal("0", end.Fields["exitCode"]);
        Assert.Equal("org/model", end.Fields["modelId"]);
        Assert.Contains("snapshot status: model-snapshot-ambiguous", end.Fields["summary"], StringComparison.Ordinal);
        Assert.False(end.Fields.ContainsKey("command"));
        Assert.False(end.Fields.ContainsKey("stdout"));
        Assert.False(end.Fields.ContainsKey("stderr"));
    }

    [Fact]
    public async Task StatusAsync_SshPod_ReturnsPresentWithSnapshotFailure()
    {
        var service = new PodModelService(new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                0,
                "model_cache_path=/models/hf/models--org--model\npresent=true\nsnapshot_count=2\nfailure_kind=model-snapshot-ambiguous\n",
                string.Empty))));

        var result = await service.StatusAsync(SshPod(), "org/model");

        Assert.True(result.Success);
        Assert.True(result.Present);
        Assert.Equal(2, result.SnapshotCount);
        Assert.Null(result.ResolvedModelPath);
        Assert.Equal("model-snapshot-ambiguous", result.SnapshotFailureKind);
        Assert.Contains("snapshot status: model-snapshot-ambiguous", result.Summary, StringComparison.Ordinal);
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

    [Fact]
    public async Task PullAsync_LogsStableAuthFailureKind()
    {
        var sink = new CapturingLogSink();
        var service = new PodModelService(new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(255, string.Empty, "Permission denied (publickey).\n"))), sink);

        var result = await service.PullAsync(SshPod(), "meta-llama/Llama-3.1-8B");

        Assert.False(result.Success);
        var end = Assert.Single(sink.Events, evt => evt.Event == "model.pull.end");
        Assert.Equal("ssh-auth-failed", end.Fields["failureKind"]);
        Assert.Equal("255", end.Fields["exitCode"]);
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

    private sealed class CapturingLogSink : ITauLogSink
    {
        public List<TauLogEvent> Events { get; } = new();

        public void Log(TauLogEvent evt) => Events.Add(evt);
    }
}
