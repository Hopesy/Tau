using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Tests;

public sealed class PodVllmOrchestrationServiceTests
{
    private static string RemoteCommand(System.Diagnostics.ProcessStartInfo psi)
    {
        Assert.Equal(8, psi.ArgumentList.Count);
        return psi.ArgumentList[7];
    }

    [Fact]
    public async Task DeployAsync_ExecutesPlannerRemoteCommandThroughSshRunner()
    {
        var captured = new List<System.Diagnostics.ProcessStartInfo>();
        var exec = new PodExecService((psi, _) =>
        {
            captured.Add(psi);
            var command = RemoteCommand(psi);
            if (IsPreflightCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/mnt/models/models--meta-llama--Llama-3.1-8B"), ""));
            }

            return command.Contains("/health", StringComparison.Ordinal)
                ? Task.FromResult(new PodExecService.ProcessExecutionResult(0, "ready\n", ""))
                : Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started tau-pod-llama-8b\n", ""));
        });
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            ModelsPath = "/mnt/models"
        };

        var result = await service.DeployAsync(
            pod,
            new PodVllmServeOptions("meta-llama/Llama-3.1-8B", DeploymentName: "llama 8b"));

        Assert.True(result.Success);
        Assert.Equal("deploy", result.Operation);
        Assert.Equal("llama-8b", result.DeploymentName);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Plan);
        Assert.NotNull(result.Preflight);
        Assert.True(result.Preflight.Success);
        Assert.Equal("/mnt/models/models--meta-llama--Llama-3.1-8B/snapshots/main", result.Plan.ModelPath);
        Assert.NotNull(result.Health);
        Assert.True(result.Health.Ready);
        Assert.Equal("ready", result.Health.State);
        Assert.Equal("none", result.Health.FailureKind);
        Assert.Equal(1, result.Health.Attempts);
        Assert.Equal(12, result.Health.MaxAttempts);
        Assert.Equal(3, captured.Count);
        Assert.Contains("vllm_available", RemoteCommand(captured[0]), StringComparison.Ordinal);
        var command = RemoteCommand(captured[1]);
        Assert.Equal(result.Command, command);
        Assert.Contains("cat > ~/.tau_pods/llama-8b.service <<'EOF'", command, StringComparison.Ordinal);
        Assert.Contains("cat > ~/.tau_pods/llama-8b.json <<'EOF'", command, StringComparison.Ordinal);
        Assert.Contains("if mkdir -p ~/.config/systemd/user", command, StringComparison.Ordinal);
        Assert.Contains("systemctl --user enable --now 'tau-pod-llama-8b.service'", command, StringComparison.Ordinal);
        Assert.Contains("WantedBy=default.target", command, StringComparison.Ordinal);
        Assert.Contains("else nohup /usr/bin/env bash -lc", command, StringComparison.Ordinal);
        Assert.Contains("nohup /usr/bin/env bash -lc", command, StringComparison.Ordinal);
        Assert.Contains("> ~/.tau_pods/llama-8b.log 2>&1", command, StringComparison.Ordinal);
        Assert.Contains("echo $! > ~/.tau_pods/llama-8b.pid", command, StringComparison.Ordinal);
        Assert.Contains("vLLM deployment 'llama-8b' started and is ready", result.Summary, StringComparison.Ordinal);
        Assert.Contains("curl -fsS --max-time 2 \"http://127.0.0.1:$port/health\"", RemoteCommand(captured[2]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployAsync_SurfacesExecFailureAndKeepsPlan()
    {
        var captured = new List<string>();
        var exec = new PodExecService((psi, _) =>
        {
            var command = RemoteCommand(psi);
            captured.Add(command);
            if (IsPreflightCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("$HOME/.cache/huggingface/hub/models--org--model"), ""));
            }

            return command.Contains("rolled back", StringComparison.Ordinal)
                ? Task.FromResult(new PodExecService.ProcessExecutionResult(0, "rolled back tau-pod-broken\n", ""))
                : Task.FromResult(new PodExecService.ProcessExecutionResult(42, "", "systemd failed"));
        });
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.DeployAsync(pod, new PodVllmServeOptions("org/model", DeploymentName: "broken"));

        Assert.False(result.Success);
        Assert.Equal(42, result.ExitCode);
        Assert.Equal("systemd failed", result.StdErr);
        Assert.NotNull(result.Plan);
        Assert.NotNull(result.Preflight);
        Assert.NotNull(result.Rollback);
        Assert.True(result.Rollback.Success);
        Assert.Equal(3, captured.Count);
        Assert.Contains("rolled back tau-pod-broken", captured[2], StringComparison.Ordinal);
        Assert.Contains("vLLM deploy failed: ssh exec failed (42)", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployAsync_RollsBackWhenHealthReportsUnhealthy()
    {
        var captured = new List<string>();
        var exec = new PodExecService((psi, _) =>
        {
            var command = RemoteCommand(psi);
            captured.Add(command);
            if (IsPreflightCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("$HOME/.cache/huggingface/hub/models--org--model"), ""));
            }

            if (command.Contains("/health", StringComparison.Ordinal))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(2, "unhealthy\n", ""));
            }

            if (command.Contains("rolled back", StringComparison.Ordinal))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "rolled back tau-pod-broken\n", ""));
            }

            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started tau-pod-broken\n", ""));
        });
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.DeployAsync(pod, new PodVllmServeOptions("org/model", DeploymentName: "broken"));

        Assert.False(result.Success);
        Assert.NotNull(result.Health);
        Assert.False(result.Health.Ready);
        Assert.True(result.Health.Unhealthy);
        Assert.Equal("unhealthy", result.Health.State);
        Assert.Equal("startup-failed", result.Health.FailureKind);
        Assert.Equal(1, result.Health.Attempts);
        Assert.NotNull(result.Rollback);
        Assert.True(result.Rollback.Success);
        Assert.Equal(4, captured.Count);
        Assert.Contains("curl -fsS --max-time 2", captured[2], StringComparison.Ordinal);
        Assert.Contains("systemctl --user disable --now 'tau-pod-broken.service'", captured[3], StringComparison.Ordinal);
        Assert.Contains("vLLM deploy health check failed", result.Summary, StringComparison.Ordinal);
        Assert.Contains("rollback completed", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployAsync_RetriesStartingHealthUntilReady()
    {
        var captured = new List<string>();
        var delays = new List<TimeSpan>();
        var healthAttempts = 0;
        var exec = new PodExecService((psi, _) =>
        {
            var command = RemoteCommand(psi);
            captured.Add(command);
            if (IsPreflightCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("$HOME/.cache/huggingface/hub/models--org--model"), ""));
            }

            if (!command.Contains("/health", StringComparison.Ordinal))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started tau-pod-slow\n", ""));
            }

            healthAttempts++;
            return healthAttempts < 3
                ? Task.FromResult(new PodExecService.ProcessExecutionResult(3, "starting\n", ""))
                : Task.FromResult(new PodExecService.ProcessExecutionResult(0, "ready\nApplication startup complete\n", ""));
        });
        var service = new PodVllmOrchestrationService(
            exec,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.DeployAsync(
            pod,
            new PodVllmServeOptions(
                "org/model",
                DeploymentName: "slow",
                HealthAttempts: 4,
                HealthBackoffMilliseconds: 25));

        Assert.True(result.Success);
        Assert.NotNull(result.Health);
        Assert.True(result.Health.Ready);
        Assert.Equal("ready", result.Health.State);
        Assert.Equal("none", result.Health.FailureKind);
        Assert.Equal(3, result.Health.Attempts);
        Assert.Equal(4, result.Health.MaxAttempts);
        Assert.Null(result.Rollback);
        Assert.Equal(5, captured.Count);
        Assert.Equal(2, delays.Count);
        Assert.All(delays, delay => Assert.Equal(TimeSpan.FromMilliseconds(25), delay));
        Assert.Contains("after 3 health attempt(s)", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreflightAsync_ResolvesRemoteSnapshotPath()
    {
        System.Diagnostics.ProcessStartInfo? captured = null;
        var exec = new PodExecService((psi, _) =>
        {
            captured = psi;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/models/hf/models--org--model", "rev-abc"), ""));
        });
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            ModelsPath = "/models/hf"
        };

        var result = await service.PreflightAsync(pod, new PodVllmServeOptions("org/model", DeploymentName: "served model"));

        Assert.True(result.Success);
        Assert.Equal("served-model", result.DeploymentName);
        Assert.Equal("org/model", result.ModelId);
        Assert.Equal("/models/hf/models--org--model", result.ModelCachePath);
        Assert.Equal("/models/hf/models--org--model/snapshots/rev-abc", result.ResolvedModelPath);
        Assert.True(result.ModelCachePresent);
        Assert.Equal(1, result.SnapshotCount);
        Assert.True(result.VllmAvailable);
        Assert.Equal("none", result.FailureKind);
        Assert.Contains("resolved to", result.Summary, StringComparison.Ordinal);
        Assert.NotNull(captured);
        var command = RemoteCommand(captured!);
        Assert.Contains("command -v vllm", command, StringComparison.Ordinal);
        Assert.Contains("refs/main", command, StringComparison.Ordinal);
        Assert.Contains("snapshot_count=", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreflightAsync_ClassifiesMissingSnapshotWithoutStartingVllm()
    {
        var exec = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                12,
                "model_cache_path=/models/hf/models--org--model\nvllm_available=true\nmodel_cache_present=true\nsnapshot_count=0\nfailure_kind=model-snapshot-missing\n",
                "")));
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            ModelsPath = "/models/hf"
        };

        var result = await service.PreflightAsync(pod, new PodVllmServeOptions("org/model"));

        Assert.False(result.Success);
        Assert.Null(result.ResolvedModelPath);
        Assert.True(result.ModelCachePresent);
        Assert.Equal(0, result.SnapshotCount);
        Assert.True(result.VllmAvailable);
        Assert.Equal("model-snapshot-missing", result.FailureKind);
    }

    [Fact]
    public async Task DeployAsync_StopsBeforeServiceCommandWhenPreflightFails()
    {
        var commands = new List<string>();
        var exec = new PodExecService((psi, _) =>
        {
            var command = RemoteCommand(psi);
            commands.Add(command);
            return Task.FromResult(new PodExecService.ProcessExecutionResult(
                14,
                "model_cache_path=/models/hf/models--org--model\nvllm_available=false\nfailure_kind=vllm-missing\n",
                ""));
        });
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            ModelsPath = "/models/hf"
        };

        var result = await service.DeployAsync(pod, new PodVllmServeOptions("org/model"));

        Assert.False(result.Success);
        Assert.Null(result.Plan);
        Assert.Null(result.Health);
        Assert.Null(result.Rollback);
        Assert.NotNull(result.Preflight);
        Assert.Equal("vllm-missing", result.Preflight.FailureKind);
        Assert.Contains("preflight failed", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Single(commands);
        Assert.DoesNotContain("systemctl --user enable", commands[0], StringComparison.Ordinal);
        Assert.DoesNotContain("nohup /usr/bin/env bash -lc", commands[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployAsync_RejectsHttpOnlyPodWithoutExecutingRunner()
    {
        var executed = false;
        var exec = new PodExecService((_, _) =>
        {
            executed = true;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "", ""));
        });
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition { Id = "http-pod", Endpoint = "http://127.0.0.1:8000" };

        var result = await service.DeployAsync(pod, new PodVllmServeOptions("org/model"));

        Assert.False(result.Success);
        Assert.False(executed);
        Assert.Equal("deploy", result.Operation);
        Assert.Contains("requires SSH-based pod", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusAsync_BuildsSystemdAndPidFallbackCommand()
    {
        System.Diagnostics.ProcessStartInfo? captured = null;
        var exec = new PodExecService((psi, _) =>
        {
            captured = psi;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "active\n", ""));
        });
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.StatusAsync(pod, "llama 8b");

        Assert.True(result.Success);
        Assert.Equal("llama-8b", result.DeploymentName);
        Assert.Equal("active\n", result.StdOut);
        Assert.False(result.Ready);
        Assert.False(result.Unhealthy);
        Assert.Equal("starting", result.State);
        Assert.NotNull(captured);
        var command = RemoteCommand(captured!);
        Assert.Equal(result.Command, command);
        Assert.Contains("curl -fsS --max-time 2 \"http://127.0.0.1:$port/health\"", command, StringComparison.Ordinal);
        Assert.Contains("systemctl --user is-active 'tau-pod-llama-8b.service'", command, StringComparison.Ordinal);
        Assert.Contains("systemctl --user status 'tau-pod-llama-8b.service' --no-pager -l", command, StringComparison.Ordinal);
        Assert.Contains("~/.tau_pods/llama-8b.pid", command, StringComparison.Ordinal);
        Assert.Contains("echo 'not found tau-pod-llama-8b'", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusAsync_ParsesReadyAndUnhealthyOutput()
    {
        var exec = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(0, "ready\nApplication startup complete\n", "")));
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var ready = await service.StatusAsync(pod, "llama");

        Assert.True(ready.Ready);
        Assert.False(ready.Unhealthy);
        Assert.Equal("ready", ready.State);

        exec = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(0, "unhealthy\nCUDA out of memory\n", "")));
        service = new PodVllmOrchestrationService(exec);

        var unhealthy = await service.StatusAsync(pod, "llama");

        Assert.False(unhealthy.Ready);
        Assert.True(unhealthy.Unhealthy);
        Assert.Equal("unhealthy", unhealthy.State);
    }

    [Fact]
    public async Task HealthAsync_BuildsHealthProbeCommandAndRequiresReady()
    {
        System.Diagnostics.ProcessStartInfo? captured = null;
        var exec = new PodExecService((psi, _) =>
        {
            captured = psi;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "ready\n", ""));
        });
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.HealthAsync(pod, "llama 8b");

        Assert.True(result.Success);
        Assert.True(result.Ready);
        Assert.False(result.Unhealthy);
        Assert.Equal("ready", result.State);
        Assert.Contains("is ready", result.Summary, StringComparison.Ordinal);
        Assert.NotNull(captured);
        var command = RemoteCommand(captured!);
        Assert.Contains("curl -fsS --max-time 2 \"http://127.0.0.1:$port/health\"", command, StringComparison.Ordinal);
        Assert.Contains("tail -n 40 ~/.tau_pods/llama-8b.log", command, StringComparison.Ordinal);
        Assert.Contains("echo unhealthy; exit 2", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAsync_BuildsSystemdAndPidCleanupCommand()
    {
        System.Diagnostics.ProcessStartInfo? captured = null;
        var exec = new PodExecService((psi, _) =>
        {
            captured = psi;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "stopped tau-pod-llama-8b\n", ""));
        });
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.StopAsync(pod, "llama 8b");

        Assert.True(result.Success);
        Assert.Equal("stop", result.Operation);
        Assert.Equal("llama-8b", result.DeploymentName);
        Assert.NotNull(captured);
        var command = RemoteCommand(captured!);
        Assert.Equal(result.Command, command);
        Assert.Contains("systemctl --user disable --now 'tau-pod-llama-8b.service'", command, StringComparison.Ordinal);
        Assert.Contains("systemctl --user daemon-reload || true; fi; if test -f ~/.tau_pods/llama-8b.pid", command, StringComparison.Ordinal);
        Assert.Contains("pkill -TERM -P \"$pid\"", command, StringComparison.Ordinal);
        Assert.Contains("kill \"$pid\"", command, StringComparison.Ordinal);
        Assert.Contains("rm -f ~/.tau_pods/llama-8b.json ~/.tau_pods/llama-8b.service", command, StringComparison.Ordinal);
        Assert.Contains("stopped tau-pod-llama-8b", command, StringComparison.Ordinal);
    }

    private static bool IsPreflightCommand(string command) =>
        command.Contains("vllm_available", StringComparison.Ordinal) &&
        command.Contains("resolved_model_path", StringComparison.Ordinal);

    private static string PreflightOk(string modelCachePath, string snapshot = "main") =>
        $"model_cache_path={modelCachePath}\n" +
        "vllm_available=true\n" +
        "model_cache_present=true\n" +
        "snapshot_count=1\n" +
        $"resolved_model_path={modelCachePath}/snapshots/{snapshot}\n" +
        "failure_kind=none\n";
}
