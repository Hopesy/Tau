using Tau.Ai.Observability;
using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Tests;

public sealed class PodVllmOrchestrationServiceTests
{
    private static string RemoteCommand(System.Diagnostics.ProcessStartInfo psi)
    {
        Assert.True(psi.ArgumentList.Count >= 8);
        return psi.ArgumentList[^1];
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

            if (IsStartupWatchCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "Application startup complete\nstartup_status=ready\n", ""));
            }

            return command.Contains("/health", StringComparison.Ordinal)
                ? Task.FromResult(new PodExecService.ProcessExecutionResult(0, "ready\n", ""))
                : Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started tau-pod-llama-8b\npid=4321\n", ""));
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
        Assert.Equal(4321, result.ProcessId);
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
        Assert.NotNull(result.StartupWatch);
        Assert.True(result.StartupWatch.Ready);
        Assert.Equal("none", result.StartupWatch.FailureKind);
        Assert.Equal(4, captured.Count);
        Assert.Contains("vllm_available", RemoteCommand(captured[0]), StringComparison.Ordinal);
        var command = RemoteCommand(captured[1]);
        Assert.Equal(result.Command, command);
        Assert.Contains("mkdir -p ~/.tau_pods ~/.vllm_logs", command, StringComparison.Ordinal);
        Assert.Contains("cat > ~/.tau_pods/model_run_llama-8b.sh <<'EOF'", command, StringComparison.Ordinal);
        Assert.Contains("HF_HUB_ENABLE_HF_TRANSFER=1 hf download \"$MODEL_ID\"", command, StringComparison.Ordinal);
        Assert.Contains("ERROR: Failed to download model", command, StringComparison.Ordinal);
        Assert.Contains("--api-key \"$PI_API_KEY\"", command, StringComparison.Ordinal);
        Assert.Contains("Model runner exiting with code", command, StringComparison.Ordinal);
        Assert.Contains("cat > ~/.tau_pods/model_wrapper_llama-8b.sh <<'EOF'", command, StringComparison.Ordinal);
        Assert.Contains("script -q -f -c \"$HOME/.tau_pods/model_run_llama-8b.sh\" \"$HOME/.vllm_logs/llama-8b.log\"", command, StringComparison.Ordinal);
        Assert.Contains("cat > ~/.tau_pods/llama-8b.service <<'EOF'", command, StringComparison.Ordinal);
        Assert.Contains("cat > ~/.tau_pods/llama-8b.json <<'EOF'", command, StringComparison.Ordinal);
        Assert.Contains("if mkdir -p ~/.config/systemd/user", command, StringComparison.Ordinal);
        Assert.Contains("ExecStartPre=/usr/bin/env mkdir -p %h/.vllm_logs", command, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/usr/bin/env bash -lc 'exec ~/.tau_pods/model_wrapper_llama-8b.sh >/dev/null 2>&1'", command, StringComparison.Ordinal);
        Assert.Contains("StandardOutput=null", command, StringComparison.Ordinal);
        Assert.Contains("StandardError=null", command, StringComparison.Ordinal);
        Assert.Contains("systemctl --user enable --now 'tau-pod-llama-8b.service'", command, StringComparison.Ordinal);
        Assert.Contains("systemctl --user show 'tau-pod-llama-8b.service' --property=MainPID --value", command, StringComparison.Ordinal);
        Assert.Contains("WantedBy=default.target", command, StringComparison.Ordinal);
        Assert.Contains("if command -v setsid >/dev/null 2>&1", command, StringComparison.Ordinal);
        Assert.Contains("setsid ~/.tau_pods/model_wrapper_llama-8b.sh", command, StringComparison.Ordinal);
        Assert.Contains("nohup ~/.tau_pods/model_wrapper_llama-8b.sh", command, StringComparison.Ordinal);
        Assert.Contains("echo $! > ~/.tau_pods/llama-8b.pid", command, StringComparison.Ordinal);
        Assert.Contains("echo \"pid=$pid\"", command, StringComparison.Ordinal);
        Assert.Contains("startup log is ready", result.Summary, StringComparison.Ordinal);
        Assert.Contains("/health is ready", result.Summary, StringComparison.Ordinal);
        Assert.Contains("tail -n 80 \"$log\"", RemoteCommand(captured[2]), StringComparison.Ordinal);
        Assert.Contains("startup_status=ready", RemoteCommand(captured[2]), StringComparison.Ordinal);
        Assert.Contains("curl -fsS --max-time 2 \"http://127.0.0.1:$port/health\"", RemoteCommand(captured[3]), StringComparison.Ordinal);
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

            if (IsStartupWatchCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "Application startup complete\nstartup_status=ready\n", ""));
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
        Assert.NotNull(result.StartupWatch);
        Assert.True(result.StartupWatch.Ready);
        Assert.NotNull(result.Rollback);
        Assert.True(result.Rollback.Success);
        Assert.Equal(5, captured.Count);
        Assert.Contains("startup_status=ready", captured[2], StringComparison.Ordinal);
        Assert.Contains("curl -fsS --max-time 2", captured[3], StringComparison.Ordinal);
        Assert.Contains("systemctl --user disable --now 'tau-pod-broken.service'", captured[4], StringComparison.Ordinal);
        Assert.Contains("vLLM deploy health check failed", result.Summary, StringComparison.Ordinal);
        Assert.Contains("rollback completed", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployAsync_RollsBackWhenStartupWatcherReportsFailure()
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

            if (IsStartupWatchCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(2, "torch.OutOfMemoryError\nstartup_status=failed\n", ""));
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
        Assert.Equal("startup-failed", result.FailureKind);
        Assert.NotNull(result.StartupWatch);
        Assert.False(result.StartupWatch.Success);
        Assert.True(result.StartupWatch.Unhealthy);
        Assert.Equal("startup-failed", result.StartupWatch.FailureKind);
        Assert.Null(result.Health);
        Assert.NotNull(result.Rollback);
        Assert.True(result.Rollback.Success);
        Assert.Equal(4, captured.Count);
        Assert.Contains("startup_status=failed", captured[2], StringComparison.Ordinal);
        Assert.Contains("systemctl --user disable --now 'tau-pod-broken.service'", captured[3], StringComparison.Ordinal);
        Assert.Contains("startup watcher failed", result.Summary, StringComparison.Ordinal);
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

            if (IsStartupWatchCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "Application startup complete\nstartup_status=ready\n", ""));
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
        Assert.NotNull(result.StartupWatch);
        Assert.True(result.StartupWatch.Ready);
        Assert.Null(result.Rollback);
        Assert.Equal(6, captured.Count);
        Assert.Equal(2, delays.Count);
        Assert.All(delays, delay => Assert.Equal(TimeSpan.FromMilliseconds(25), delay));
        Assert.Contains("after 3 health attempt(s)", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployAsync_LogsRuntimeEventsForPreflightDeployAndHealth()
    {
        var sink = new CapturingLogSink();
        var exec = new PodExecService((psi, _) =>
        {
            var command = RemoteCommand(psi);
            if (IsPreflightCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/models/models--org--model"), ""));
            }

            if (IsStartupWatchCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "Application startup complete\nstartup_status=ready\n", ""));
            }

            return command.Contains("/health", StringComparison.Ordinal)
                ? Task.FromResult(new PodExecService.ProcessExecutionResult(0, "ready\n", ""))
                : Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started tau-pod-served\n", ""));
        });
        var service = new PodVllmOrchestrationService(exec, logSink: sink);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com", ModelsPath = "/models" };

        var result = await service.DeployAsync(pod, new PodVllmServeOptions("org/model", DeploymentName: "served"));

        Assert.True(result.Success);
        Assert.Contains(sink.Events, evt => evt.Category == "pod" && evt.Event == "vllm.deploy.start");
        Assert.Contains(sink.Events, evt => evt.Event == "vllm.preflight.end" && evt.Fields["success"] == "true");
        Assert.Contains(sink.Events, evt => evt.Event == "vllm.startup-watch.end" && evt.Fields["ready"] == "true");
        Assert.Contains(sink.Events, evt => evt.Event == "vllm.health.end" && evt.Fields["ready"] == "true");
        var deployEnd = Assert.Single(sink.Events, evt => evt.Event == "vllm.deploy.end");
        Assert.Equal("gpu-1", deployEnd.Fields["podId"]);
        Assert.Equal("deploy", deployEnd.Fields["operation"]);
        Assert.Equal("served", deployEnd.Fields["deploymentName"]);
        Assert.Equal("org/model", deployEnd.Fields["modelId"]);
        Assert.Equal("true", deployEnd.Fields["success"]);
        Assert.Equal("false", deployEnd.Fields["hasRollback"]);
        Assert.Equal("true", deployEnd.Fields["healthReady"]);
        Assert.False(deployEnd.Fields.ContainsKey("command"));
        Assert.False(deployEnd.Fields.ContainsKey("stdout"));
        Assert.False(deployEnd.Fields.ContainsKey("stderr"));
    }

    [Fact]
    public async Task DeployAsync_LogsRollbackWhenHealthFails()
    {
        var sink = new CapturingLogSink();
        var exec = new PodExecService((psi, _) =>
        {
            var command = RemoteCommand(psi);
            if (IsPreflightCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("$HOME/.cache/huggingface/hub/models--org--model"), ""));
            }

            if (IsStartupWatchCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "Application startup complete\nstartup_status=ready\n", ""));
            }

            if (command.Contains("/health", StringComparison.Ordinal))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(2, "unhealthy\n", ""));
            }

            return command.Contains("rolled back", StringComparison.Ordinal)
                ? Task.FromResult(new PodExecService.ProcessExecutionResult(0, "rolled back tau-pod-broken\n", ""))
                : Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started tau-pod-broken\n", ""));
        });
        var service = new PodVllmOrchestrationService(exec, logSink: sink);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.DeployAsync(pod, new PodVllmServeOptions("org/model", DeploymentName: "broken"));

        Assert.False(result.Success);
        Assert.NotNull(result.Rollback);
        Assert.Contains(sink.Events, evt => evt.Event == "vllm.rollback.start");
        Assert.Contains(sink.Events, evt => evt.Event == "vllm.rollback.end" && evt.Fields["success"] == "true");
        var deployEnd = Assert.Single(sink.Events, evt => evt.Event == "vllm.deploy.end");
        Assert.Equal("false", deployEnd.Fields["success"]);
        Assert.Equal("true", deployEnd.Fields["hasRollback"]);
        Assert.Equal("startup-failed", deployEnd.Fields["failureKind"]);
        Assert.Equal("1", deployEnd.Fields["healthAttempts"]);
    }

    [Fact]
    public async Task HealthAsync_UsesStableFailureKindWhenRemoteOutputIsMissing()
    {
        var exec = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(42, string.Empty, "transport disconnected")));
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var result = await service.HealthAsync(pod, "served-model");

        Assert.False(result.Success);
        Assert.Equal("ssh-exec-failed", result.FailureKind);
        Assert.Equal(1, result.Attempts);
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
    public async Task PreflightAsync_WithRevision_ResolvesRequestedSnapshot()
    {
        string? capturedCommand = null;
        var exec = new PodExecService((psi, _) =>
        {
            capturedCommand = RemoteCommand(psi);
            return Task.FromResult(new PodExecService.ProcessExecutionResult(
                0,
                "model_cache_path=/models/hf/models--org--model\n" +
                "requested_revision=rev-b\n" +
                "vllm_available=true\n" +
                "model_cache_present=true\n" +
                "snapshot_count=2\n" +
                "resolved_model_path=/models/hf/models--org--model/snapshots/rev-b\n" +
                "failure_kind=none\n",
                ""));
        });
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            ModelsPath = "/models/hf"
        };

        var result = await service.PreflightAsync(pod, new PodVllmServeOptions("org/model", Revision: "rev-b"));

        Assert.True(result.Success);
        Assert.Equal("rev-b", result.RequestedRevision);
        Assert.Equal(2, result.SnapshotCount);
        Assert.Equal("/models/hf/models--org--model/snapshots/rev-b", result.ResolvedModelPath);
        Assert.Contains("revision 'rev-b' resolved", result.Summary, StringComparison.Ordinal);
        Assert.NotNull(capturedCommand);
        Assert.Contains("requested_revision='rev-b'", capturedCommand!, StringComparison.Ordinal);
        Assert.Contains("ref_file=\"$cache/refs/$requested_revision\"", capturedCommand!, StringComparison.Ordinal);
        Assert.Contains("snapshots/$requested_revision", capturedCommand!, StringComparison.Ordinal);
        Assert.DoesNotContain("model-snapshot-ambiguous", capturedCommand!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreflightAsync_WithMissingRevision_ClassifiesFailure()
    {
        var exec = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                16,
                "model_cache_path=/models/hf/models--org--model\n" +
                "requested_revision=rev-missing\n" +
                "vllm_available=true\n" +
                "model_cache_present=true\n" +
                "snapshot_count=2\n" +
                "failure_kind=model-snapshot-revision-missing\n",
                "")));
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            ModelsPath = "/models/hf"
        };

        var result = await service.PreflightAsync(pod, new PodVllmServeOptions("org/model", Revision: "rev-missing"));

        Assert.False(result.Success);
        Assert.Equal("rev-missing", result.RequestedRevision);
        Assert.Null(result.ResolvedModelPath);
        Assert.Equal(2, result.SnapshotCount);
        Assert.Equal("model-snapshot-revision-missing", result.FailureKind);
        Assert.Contains("requested revision", result.Summary, StringComparison.OrdinalIgnoreCase);
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

    [Theory]
    [InlineData(
        10,
        "model_cache_path=/models/hf/models--org--model\nvllm_available=true\nmodel_cache_present=false\nsnapshot_count=0\nfailure_kind=model-cache-missing\n",
        "model-cache-missing",
        false,
        0,
        "Pull the model first")]
    [InlineData(
        11,
        "model_cache_path=/models/hf/models--org--model\nvllm_available=true\nmodel_cache_present=true\nsnapshot_count=0\nfailure_kind=model-snapshots-missing\n",
        "model-snapshots-missing",
        true,
        0,
        "Pull the model first")]
    [InlineData(
        13,
        "model_cache_path=/models/hf/models--org--model\nvllm_available=true\nmodel_cache_present=true\nsnapshot_count=2\nfailure_kind=model-snapshot-ref-missing\n",
        "model-snapshot-ref-missing",
        true,
        2,
        "refs/main file points to a snapshot directory that does not exist")]
    [InlineData(
        14,
        "model_cache_path=/models/hf/models--org--model\nvllm_available=false\nfailure_kind=vllm-missing\n",
        "vllm-missing",
        false,
        0,
        "Install or activate vLLM")]
    [InlineData(
        15,
        "model_cache_path=/models/hf/models--org--model\nvllm_available=true\nmodel_cache_present=true\nsnapshot_count=2\nfailure_kind=model-snapshot-ambiguous\n",
        "model-snapshot-ambiguous",
        true,
        2,
        "Multiple snapshots exist without a valid refs/main target")]
    public async Task PreflightAsync_ClassifiesRemoteSnapshotFailures(
        int exitCode,
        string stdout,
        string expectedFailureKind,
        bool expectedCachePresent,
        int expectedSnapshotCount,
        string expectedHint)
    {
        var exec = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(exitCode, stdout, "")));
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
        Assert.Equal(expectedCachePresent, result.ModelCachePresent);
        Assert.Equal(expectedSnapshotCount, result.SnapshotCount);
        Assert.Equal(expectedFailureKind, result.FailureKind);
        Assert.Contains(expectedHint, result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreflightAsync_MapsSshRunnerFailureWithoutPreflightOutput()
    {
        var exec = new PodExecService((_, _) => throw new ApplicationException("runner unavailable"));
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            ModelsPath = "/models/hf"
        };

        var result = await service.PreflightAsync(pod, new PodVllmServeOptions("org/model"));

        Assert.False(result.Success);
        Assert.Equal("ssh-process-runner-failed", result.FailureKind);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("Check the SSH transport", result.Summary, StringComparison.Ordinal);
        Assert.Contains("runner unavailable", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreflightAsync_UsesStableSshFailureKindWhenRemoteOutputIsMissing()
    {
        var exec = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                255,
                string.Empty,
                "Permission denied (publickey).\n")));
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            ModelsPath = "/models/hf"
        };

        var result = await service.PreflightAsync(pod, new PodVllmServeOptions("org/model"));

        Assert.False(result.Success);
        Assert.Equal("ssh-auth-failed", result.FailureKind);
        Assert.Equal(255, result.ExitCode);
        Assert.Contains("Check the SSH transport", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreflightAsync_QuotesHomeBasedCachePathWithSpaces()
    {
        string? capturedCommand = null;
        var exec = new PodExecService((psi, _) =>
        {
            capturedCommand = RemoteCommand(psi);
            return Task.FromResult(new PodExecService.ProcessExecutionResult(
                0,
                PreflightOk("$HOME/hf cache/models--org--model", "rev-abc"),
                ""));
        });
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            ModelsPath = "$HOME/hf cache"
        };

        var result = await service.PreflightAsync(pod, new PodVllmServeOptions("org/model"));

        Assert.True(result.Success);
        Assert.NotNull(capturedCommand);
        Assert.Contains("cache=\"$HOME/hf cache/models--org--model\";", capturedCommand!, StringComparison.Ordinal);
        Assert.DoesNotContain("cache=$HOME/hf cache/models--org--model", capturedCommand!, StringComparison.Ordinal);
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
    public async Task DeployAsync_WithPrefetchAndRevisionMissing_PullsBeforeSecondPreflightAndDeploys()
    {
        var commands = new List<string>();
        var preflightAttempts = 0;
        var exec = new PodExecService((psi, _) =>
        {
            var command = RemoteCommand(psi);
            commands.Add(command);
            if (command.Contains("vllm_available", StringComparison.Ordinal))
            {
                preflightAttempts++;
                if (preflightAttempts == 1)
                {
                    return Task.FromResult(new PodExecService.ProcessExecutionResult(
                        16,
                        "model_cache_path=/models/hf/models--org--model\nvllm_available=true\nmodel_cache_present=true\nsnapshot_count=1\nfailure_kind=model-snapshot-revision-missing\n",
                        ""));
                }

                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/models/hf/models--org--model", "rev-b"), ""));
            }

            if (command.Contains("huggingface-cli download", StringComparison.Ordinal))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "downloaded\n", ""));
            }

            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started tau-pod-served-model\n", ""));
        });
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            ModelsPath = "/models/hf"
        };

        var result = await service.DeployAsync(
            pod,
            new PodVllmServeOptions(
                "org/model",
                DeploymentName: "served model",
                Revision: "rev-b",
                WaitForHealth: false,
                Prefetch: true));

        Assert.True(result.Success);
        Assert.NotNull(result.Prefetch);
        Assert.True(result.Prefetch.Success);
        Assert.Equal("rev-b", result.Prefetch.RequestedRevision);
        Assert.Equal("downloaded\n", result.Prefetch.Output);
        Assert.Equal("model-snapshot-revision-missing", result.PrefetchTriggerFailureKind);
        Assert.NotNull(result.Preflight);
        Assert.True(result.Preflight.Success);
        Assert.Equal("rev-b", result.Preflight.RequestedRevision);
        Assert.NotNull(result.Plan);
        Assert.Equal("/models/hf/models--org--model/snapshots/rev-b", result.Plan.ModelPath);
        Assert.Null(result.Health);
        Assert.Null(result.Rollback);
        Assert.Equal(4, commands.Count);
        Assert.Contains("requested_revision='rev-b'", commands[0], StringComparison.Ordinal);
        Assert.Contains("huggingface-cli download 'org/model' --revision 'rev-b' --cache-dir '/models/hf'", commands[1], StringComparison.Ordinal);
        Assert.Equal("/models/hf/models--org--model/snapshots/rev-b", result.Preflight.ResolvedModelPath);
        Assert.Contains("vllm serve '/models/hf/models--org--model/snapshots/rev-b'", result.Plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("started on", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeployAsync_WithPrefetchAndPullFailure_DoesNotExecuteDeployCommand()
    {
        var commands = new List<string>();
        var exec = new PodExecService((psi, _) =>
        {
            var command = RemoteCommand(psi);
            commands.Add(command);
            if (command.Contains("vllm_available", StringComparison.Ordinal))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(
                    14,
                    "model_cache_path=/models/hf/models--org--model\nvllm_available=true\nmodel_cache_present=true\nsnapshot_count=1\nfailure_kind=model-cache-missing\n",
                    ""));
            }

            if (command.Contains("huggingface-cli download", StringComparison.Ordinal))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(1, "", "download denied"));
            }

            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started tau-pod-broken\n", ""));
        });
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            ModelsPath = "/models/hf"
        };

        var result = await service.DeployAsync(
            pod,
            new PodVllmServeOptions(
                "org/model",
                DeploymentName: "broken",
                Revision: "rev-b",
                WaitForHealth: false,
                Prefetch: true));

        Assert.False(result.Success);
        Assert.NotNull(result.Preflight);
        Assert.False(result.Preflight.Success);
        Assert.NotNull(result.Prefetch);
        Assert.False(result.Prefetch.Success);
        Assert.Equal("rev-b", result.Prefetch.RequestedRevision);
        Assert.Equal("model-cache-missing", result.PrefetchTriggerFailureKind);
        Assert.Null(result.Plan);
        Assert.Null(result.Health);
        Assert.Null(result.Rollback);
        Assert.Equal(2, commands.Count);
        Assert.DoesNotContain("vllm serve", string.Join('\n', commands), StringComparison.Ordinal);
        Assert.Contains("prefetch failed", result.Summary, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("tail -n 80 ~/.vllm_logs/llama-8b.log", command, StringComparison.Ordinal);
        Assert.Contains("Application startup complete", command, StringComparison.Ordinal);
        Assert.Contains("systemctl --user is-active 'tau-pod-llama-8b.service'", command, StringComparison.Ordinal);
        Assert.Contains("systemctl --user status 'tau-pod-llama-8b.service' --no-pager -l", command, StringComparison.Ordinal);
        Assert.Contains("~/.tau_pods/llama-8b.pid", command, StringComparison.Ordinal);
        Assert.Contains("echo 'not found tau-pod-llama-8b'", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusAsync_ParsesReadyAndUnhealthyOutput()
    {
        var exec = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(0, "Application startup complete\n", "")));
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var ready = await service.StatusAsync(pod, "llama");

        Assert.True(ready.Ready);
        Assert.False(ready.Unhealthy);
        Assert.Equal("ready", ready.State);

        exec = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(0, "Model runner exiting with code 1\n", "")));
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
        Assert.Contains("tail -n 80 ~/.vllm_logs/llama-8b.log", command, StringComparison.Ordinal);
        Assert.Contains("Application startup complete", command, StringComparison.Ordinal);
        Assert.Contains("tail -n 40 ~/.tau_pods/llama-8b.log", command, StringComparison.Ordinal);
        Assert.Contains("echo unhealthy; exit 2", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthAsync_ParsesStartupLogMarkers()
    {
        var exec = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(0, "Application startup complete\n", "")));
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var ready = await service.HealthAsync(pod, "llama");

        Assert.True(ready.Success);
        Assert.True(ready.Ready);
        Assert.Equal("ready", ready.State);
        Assert.Equal("none", ready.FailureKind);

        exec = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(2, "Script exited with code 1\n", "")));
        service = new PodVllmOrchestrationService(exec);

        var unhealthy = await service.HealthAsync(pod, "llama");

        Assert.False(unhealthy.Success);
        Assert.False(unhealthy.Ready);
        Assert.True(unhealthy.Unhealthy);
        Assert.Equal("unhealthy", unhealthy.State);
        Assert.Equal("startup-failed", unhealthy.FailureKind);
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
        Assert.Contains("rm -f ~/.tau_pods/llama-8b.json ~/.tau_pods/llama-8b.service ~/.tau_pods/model_run_llama-8b.sh ~/.tau_pods/model_wrapper_llama-8b.sh", command, StringComparison.Ordinal);
        Assert.Contains("stopped tau-pod-llama-8b", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RollbackAsync_RejectsHttpOnlyPodWithStableFailureKind()
    {
        var executed = false;
        var exec = new PodExecService((_, _) =>
        {
            executed = true;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, string.Empty, string.Empty));
        });
        var service = new PodVllmOrchestrationService(exec);
        var pod = new PodDefinition { Id = "http-pod", Endpoint = "http://localhost:8000" };

        var result = await service.RollbackAsync(pod, "llama 8b", CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(executed);
        Assert.Equal("llama-8b", result.DeploymentName);
        Assert.Equal("unsupported-transport", result.FailureKind);
        Assert.Contains("requires SSH-based pod", result.Summary, StringComparison.Ordinal);
    }

    private static bool IsPreflightCommand(string command) =>
        command.Contains("vllm_available", StringComparison.Ordinal) &&
        command.Contains("resolved_model_path", StringComparison.Ordinal);

    private static bool IsStartupWatchCommand(string command) =>
        command.Contains("startup_status", StringComparison.Ordinal) &&
        command.Contains(".vllm_logs", StringComparison.Ordinal);

    private static string PreflightOk(string modelCachePath, string snapshot = "main") =>
        $"model_cache_path={modelCachePath}\n" +
        "vllm_available=true\n" +
        "model_cache_present=true\n" +
        "snapshot_count=1\n" +
        $"resolved_model_path={modelCachePath}/snapshots/{snapshot}\n" +
        "failure_kind=none\n";

    private sealed class CapturingLogSink : ITauLogSink
    {
        public List<TauLogEvent> Events { get; } = new();

        public void Log(TauLogEvent evt) => Events.Add(evt);
    }
}
