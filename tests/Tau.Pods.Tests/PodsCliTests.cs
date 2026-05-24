using System.Text.Json;
using Tau.Pods.Cli;
using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Tests;

public class PodsCliTests
{
    private static readonly SemaphoreSlim CurrentDirectoryGate = new(1, 1);

    [Fact]
    public async Task Deploy_WithoutConfigPath_UsesDefaultConfig()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Environment.CurrentDirectory = tempDir.FullName;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            new PodsConfigStore().Save("tau.pods.json", ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["deploy", "http-pod", "meta/llama-3"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("http-pod | ok=False | Deploy requires SSH-based pod.", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task Deploy_WithExplicitConfigPath_UsesProvidedConfig()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            new PodsConfigStore().Save(configPath, ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["deploy", configPath, "http-pod", "meta/llama-3"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("http-pod | ok=False | Deploy requires SSH-based pod.", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Logs_WithoutConfigPath_RejectsNonSshPod()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-logs-");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Environment.CurrentDirectory = tempDir.FullName;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            new PodsConfigStore().Save("tau.pods.json", ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["logs", "http-pod", "demo"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("http-pod | ok=False | Logs require SSH-based pod.", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task Logs_RejectsInvalidTail()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-logs-tail-");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Environment.CurrentDirectory = tempDir.FullName;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            new PodsConfigStore().Save("tau.pods.json", ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["logs", "http-pod", "demo", "notanumber"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Invalid tail value", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task Deployments_WithoutConfigPath_RejectsNonSshPod()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-deployments-");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Environment.CurrentDirectory = tempDir.FullName;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            new PodsConfigStore().Save("tau.pods.json", ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["deployments", "http-pod"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("http-pod | ok=False | Deployments require SSH-based pod.", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task Deployments_MissingPodId_PrintsUsage()
    {
        var stderr = new StringWriter();
        var previousError = Console.Error;
        try
        {
            Console.SetError(stderr);
            var exitCode = await PodsCli.RunAsync(["deployments"]);
            Assert.Equal(1, exitCode);
            Assert.Contains("Usage: deployments", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task Deployments_UnknownPod_ReportsNotFound()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-deployments-unknown-");
        var stderr = new StringWriter();
        var previousError = Console.Error;

        try
        {
            Environment.CurrentDirectory = tempDir.FullName;
            Console.SetError(stderr);
            new PodsConfigStore().Save("tau.pods.json", ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["deployments", "missing-pod"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Pod not found: missing-pod", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task ModelList_WithoutConfigPath_RejectsNonSshPod()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-model-list-");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Environment.CurrentDirectory = tempDir.FullName;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            new PodsConfigStore().Save("tau.pods.json", ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["model", "list", "http-pod"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("http-pod | ok=False | Model list requires SSH-based pod.", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task ModelPull_WithExplicitConfigPath_UsesProvidedConfig()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-model-pull-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            new PodsConfigStore().Save(configPath, ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["model", "pull", configPath, "http-pod", "meta-llama/Llama-3.1-8B"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("http-pod | ok=False | operation=pull | model=meta-llama/Llama-3.1-8B | Model pull requires SSH-based pod.", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ModelStatus_UnknownPod_ReportsNotFound()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-model-status-unknown-");
        var stderr = new StringWriter();
        var previousError = Console.Error;

        try
        {
            Environment.CurrentDirectory = tempDir.FullName;
            Console.SetError(stderr);
            new PodsConfigStore().Save("tau.pods.json", ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["model", "status", "missing-pod", "model"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Pod not found: missing-pod", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task Model_UnknownSubcommand_PrintsUsage()
    {
        var stderr = new StringWriter();
        var previousError = Console.Error;

        try
        {
            Console.SetError(stderr);

            var exitCode = await PodsCli.RunAsync(["model", "unknown"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Unknown model subcommand: unknown", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("Usage: model", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task VllmPlan_WithoutConfigPath_UsesDefaultConfigAndPrintsPlan()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Environment.CurrentDirectory = tempDir.FullName;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            new PodsConfigStore().Save("tau.pods.json", ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["vllm", "plan", "ssh-pod", "meta-llama/Llama-3.1-8B", "llama-8b"]);

            var output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("pod=ssh-pod", output, StringComparison.Ordinal);
            Assert.Contains("deployment=llama-8b", output, StringComparison.Ordinal);
            Assert.Contains("model=meta-llama/Llama-3.1-8B", output, StringComparison.Ordinal);
            Assert.Contains("modelPath=/mnt/models/models--meta-llama--Llama-3.1-8B", output, StringComparison.Ordinal);
            Assert.Contains("port=8000", output, StringComparison.Ordinal);
            Assert.Contains("servedModel=llama-8b", output, StringComparison.Ordinal);
            Assert.Contains("unit=tau-pod-llama-8b.service", output, StringComparison.Ordinal);
            Assert.Contains("[systemd-unit]", output, StringComparison.Ordinal);
            Assert.Contains("ExecStart=/usr/bin/env bash -lc", output, StringComparison.Ordinal);
            Assert.Contains("[metadata-json]", output, StringComparison.Ordinal);
            Assert.Contains("\"status\":\"planned-vllm\"", output, StringComparison.Ordinal);
            Assert.Contains("[remote-command]", output, StringComparison.Ordinal);
            Assert.Contains("cat > ~/.tau_pods/llama-8b.service <<'EOF'", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task VllmPlan_WithExplicitConfigPath_UsesProvidedConfig()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-explicit-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["vllm", "plan", configPath, "ssh-pod", "org/model"]);

            var output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("deployment=org-model", output, StringComparison.Ordinal);
            Assert.Contains("modelPath=/mnt/models/models--org--model", output, StringComparison.Ordinal);
            Assert.Contains("servedModel=org-model", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmPlan_WithJsonOption_PrintsMachineReadablePlan()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-json-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["vllm", "plan", "--json", configPath, "ssh-pod", "org/model", "served model"]);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("[metadata-json]", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);

            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("pod").GetString());
            Assert.Equal("served-model", root.GetProperty("deployment").GetString());
            Assert.Equal("org/model", root.GetProperty("model").GetString());
            Assert.Equal("/mnt/models/models--org--model", root.GetProperty("modelPath").GetString());
            Assert.Equal(8000, root.GetProperty("port").GetInt32());
            Assert.Equal("served-model", root.GetProperty("servedModel").GetString());
            Assert.Equal("tau-pod-served-model.service", root.GetProperty("unit").GetString());
            var serveCommand = root.GetProperty("serveCommand").GetString()!;
            var systemdUnit = root.GetProperty("systemdUnit").GetString()!;
            var remoteCommand = root.GetProperty("remoteCommand").GetString()!;
            Assert.Contains("vllm serve '/mnt/models/models--org--model'", serveCommand, StringComparison.Ordinal);
            Assert.Contains("ExecStart=/usr/bin/env bash -lc", systemdUnit, StringComparison.Ordinal);
            Assert.Contains("cat > ~/.tau_pods/served-model.service <<'EOF'", remoteCommand, StringComparison.Ordinal);
            Assert.DoesNotContain("ssh ", remoteCommand, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("systemctl start", remoteCommand, StringComparison.OrdinalIgnoreCase);

            var metadata = root.GetProperty("metadata");
            Assert.Equal("planned-vllm", metadata.GetProperty("status").GetString());
            Assert.Equal("served-model", metadata.GetProperty("name").GetString());
            Assert.Equal("/mnt/models/models--org--model", metadata.GetProperty("modelPath").GetString());
            Assert.Equal(8000, metadata.GetProperty("port").GetInt32());
            Assert.Equal("tau-pod-served-model.service", metadata.GetProperty("unit").GetString());

            using var metadataJson = JsonDocument.Parse(root.GetProperty("metadataJson").GetString()!);
            Assert.Equal(metadata.GetProperty("ts").GetString(), metadataJson.RootElement.GetProperty("ts").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmPlan_MissingModel_PrintsUsage()
    {
        var stderr = new StringWriter();
        var previousError = Console.Error;

        try
        {
            Console.SetError(stderr);

            var exitCode = await PodsCli.RunAsync(["vllm", "plan", "ssh-pod"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Usage: vllm plan", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task VllmPlan_UnknownPod_ReportsNotFound()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-unknown-");
        var stderr = new StringWriter();
        var previousError = Console.Error;

        try
        {
            Environment.CurrentDirectory = tempDir.FullName;
            Console.SetError(stderr);
            new PodsConfigStore().Save("tau.pods.json", ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["vllm", "plan", "missing-pod", "org/model"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Pod not found: missing-pod", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task VllmPlan_DoesNotExecuteSshRunner()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-no-exec-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var processStarted = false;
        var execService = new PodExecService((_, _) =>
        {
            processStarted = true;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, string.Empty, string.Empty));
        });

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["vllm", "plan", configPath, "ssh-pod", "org/model"], execService);

            Assert.Equal(0, exitCode);
            Assert.False(processStarted);
            Assert.Contains("[remote-command]", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmPreflight_WithTextOutput_PrintsStructuredContract()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-preflight-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        string? capturedCommand = null;
        var execService = new PodExecService((psi, _) =>
        {
            capturedCommand = psi.ArgumentList[7];
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/mnt/models/models--org--model", "rev-abc"), ""));
        });

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "preflight", configPath, "ssh-pod", "org/model", "served model"],
                execService);

            var output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("ssh-pod | ok=True | operation=preflight | deployment=served-model | model=org/model", output, StringComparison.Ordinal);
            Assert.Contains("modelCachePresent=True", output, StringComparison.Ordinal);
            Assert.Contains("snapshotCount=1", output, StringComparison.Ordinal);
            Assert.Contains("vllmAvailable=True", output, StringComparison.Ordinal);
            Assert.Contains("failure=none", output, StringComparison.Ordinal);
            Assert.Contains("modelCachePath=/mnt/models/models--org--model", output, StringComparison.Ordinal);
            Assert.Contains("resolvedModelPath=/mnt/models/models--org--model/snapshots/rev-abc", output, StringComparison.Ordinal);
            Assert.Contains("[preflight-command]", output, StringComparison.Ordinal);
            Assert.Contains("[stdout]", output, StringComparison.Ordinal);
            Assert.NotNull(capturedCommand);
            Assert.Contains("command -v vllm", capturedCommand!, StringComparison.Ordinal);
            Assert.Contains("refs/main", capturedCommand!, StringComparison.Ordinal);
            Assert.DoesNotContain("systemctl --user enable", capturedCommand!, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmPreflight_WithJsonOutput_PrintsFailureAndReturnsNonZero()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-preflight-json-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                15,
                "model_cache_path=/mnt/models/models--org--model\nvllm_available=true\nmodel_cache_present=true\nsnapshot_count=2\nfailure_kind=model-snapshot-ambiguous\n",
                "")));

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "preflight", "--json", configPath, "ssh-pod", "org/model", "served model"],
                execService);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("pod").GetString());
            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.Equal("preflight", root.GetProperty("operation").GetString());
            Assert.Equal("served-model", root.GetProperty("deployment").GetString());
            Assert.Equal("org/model", root.GetProperty("model").GetString());
            Assert.Equal("/mnt/models/models--org--model", root.GetProperty("modelCachePath").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("resolvedModelPath").ValueKind);
            Assert.True(root.GetProperty("modelCachePresent").GetBoolean());
            Assert.Equal(2, root.GetProperty("snapshotCount").GetInt32());
            Assert.True(root.GetProperty("vllmAvailable").GetBoolean());
            Assert.Equal("model-snapshot-ambiguous", root.GetProperty("failureKind").GetString());
            Assert.Equal(15, root.GetProperty("exitCode").GetInt32());
            Assert.Contains("model-snapshot-ambiguous", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmDeploy_WithTextOutput_ExecutesRemoteCommandAndPrintsContract()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-deploy-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var captured = new List<System.Diagnostics.ProcessStartInfo>();
        var execService = new PodExecService((psi, _) =>
        {
            captured.Add(psi);
            var command = psi.ArgumentList[7];
            if (IsPreflightCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/mnt/models/models--meta-llama--Llama-3.1-8B"), ""));
            }

            return command.Contains("/health", StringComparison.Ordinal)
                ? Task.FromResult(new PodExecService.ProcessExecutionResult(0, "ready\n", ""))
                : Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started tau-pod-llama-8b\n", ""));
        });

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "deploy", configPath, "ssh-pod", "meta-llama/Llama-3.1-8B", "llama 8b"],
                execService);

            var output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("ssh-pod | ok=True | operation=deploy | deployment=llama-8b | exit=0", output, StringComparison.Ordinal);
            Assert.Contains("[remote-command]", output, StringComparison.Ordinal);
            Assert.Contains("systemctl --user enable --now 'tau-pod-llama-8b.service'", output, StringComparison.Ordinal);
            Assert.Contains("[serve-command]", output, StringComparison.Ordinal);
            Assert.Contains("vllm serve '/mnt/models/models--meta-llama--Llama-3.1-8B/snapshots/main'", output, StringComparison.Ordinal);
            Assert.Contains("[preflight]", output, StringComparison.Ordinal);
            Assert.Contains("preflight-ssh-pod | ok=True | operation=preflight", output, StringComparison.Ordinal);
            Assert.Contains("preflight-resolvedModelPath=/mnt/models/models--meta-llama--Llama-3.1-8B/snapshots/main", output, StringComparison.Ordinal);
            Assert.Contains("[stdout]", output, StringComparison.Ordinal);
            Assert.Contains("started tau-pod-llama-8b", output, StringComparison.Ordinal);
            Assert.Contains("[health]", output, StringComparison.Ordinal);
            Assert.Contains("operation=health | deployment=llama-8b | state=ready | ready=True | unhealthy=False | failure=none | attempts=1/12 | exit=0", output, StringComparison.Ordinal);
            Assert.Equal(3, captured.Count);
            Assert.Contains("vllm_available", captured[0].ArgumentList[7], StringComparison.Ordinal);
            Assert.Contains("cat > ~/.tau_pods/llama-8b.service <<'EOF'", captured[1].ArgumentList[7], StringComparison.Ordinal);
            Assert.Contains("curl -fsS --max-time 2 \"http://127.0.0.1:$port/health\"", captured[2].ArgumentList[7], StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmDeploy_WithJsonOutput_PrintsResultAndNestedPlan()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-deploy-json-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((psi, _) =>
        {
            var command = psi.ArgumentList[7];
            if (IsPreflightCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/mnt/models/models--org--model"), ""));
            }

            return command.Contains("/health", StringComparison.Ordinal)
                ? Task.FromResult(new PodExecService.ProcessExecutionResult(0, "ready\n", ""))
                : Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started\n", ""));
        });

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "deploy", "--json", configPath, "ssh-pod", "org/model", "served model"],
                execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("pod").GetString());
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal("deploy", root.GetProperty("operation").GetString());
            Assert.Equal("served-model", root.GetProperty("deployment").GetString());
            Assert.Contains("systemctl --user enable --now 'tau-pod-served-model.service'", root.GetProperty("remoteCommand").GetString(), StringComparison.Ordinal);
            Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
            Assert.Equal("started\n", root.GetProperty("stdout").GetString());

            var plan = root.GetProperty("plan");
            Assert.Equal("org/model", plan.GetProperty("model").GetString());
            Assert.Equal("/mnt/models/models--org--model/snapshots/main", plan.GetProperty("modelPath").GetString());
            Assert.Equal("planned-vllm", plan.GetProperty("metadata").GetProperty("status").GetString());
            Assert.Contains("cat > ~/.tau_pods/served-model.service <<'EOF'", plan.GetProperty("planRemoteCommand").GetString(), StringComparison.Ordinal);

            var preflight = root.GetProperty("preflight");
            Assert.True(preflight.GetProperty("ok").GetBoolean());
            Assert.Equal("preflight", preflight.GetProperty("operation").GetString());
            Assert.Equal("none", preflight.GetProperty("failureKind").GetString());
            Assert.Equal("/mnt/models/models--org--model", preflight.GetProperty("modelCachePath").GetString());
            Assert.Equal("/mnt/models/models--org--model/snapshots/main", preflight.GetProperty("resolvedModelPath").GetString());
            Assert.True(preflight.GetProperty("vllmAvailable").GetBoolean());
            Assert.Contains("command -v vllm", preflight.GetProperty("remoteCommand").GetString(), StringComparison.Ordinal);

            var health = root.GetProperty("health");
            Assert.True(health.GetProperty("ok").GetBoolean());
            Assert.Equal("health", health.GetProperty("operation").GetString());
            Assert.Equal("ready", health.GetProperty("state").GetString());
            Assert.True(health.GetProperty("ready").GetBoolean());
            Assert.False(health.GetProperty("unhealthy").GetBoolean());
            Assert.Equal("none", health.GetProperty("failureKind").GetString());
            Assert.Equal(1, health.GetProperty("attempts").GetInt32());
            Assert.Equal(12, health.GetProperty("maxAttempts").GetInt32());
            Assert.Equal("ready\n", health.GetProperty("stdout").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmDeploy_WhenHealthFails_PrintsRollbackInJson()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-deploy-health-fail-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((psi, _) =>
        {
            var command = psi.ArgumentList[7];
            if (IsPreflightCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/mnt/models/models--org--model"), ""));
            }

            if (command.Contains("/health", StringComparison.Ordinal))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(2, "unhealthy\nCUDA out of memory\n", ""));
            }

            if (command.Contains("rolled back", StringComparison.Ordinal))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "rolled back tau-pod-served-model\n", ""));
            }

            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started\n", ""));
        });

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "deploy", "--json", configPath, "ssh-pod", "org/model", "served model"],
                execService);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.Equal("deploy", root.GetProperty("operation").GetString());
            Assert.Contains("rollback completed", root.GetProperty("summary").GetString(), StringComparison.Ordinal);

            var preflight = root.GetProperty("preflight");
            Assert.True(preflight.GetProperty("ok").GetBoolean());
            Assert.Equal("none", preflight.GetProperty("failureKind").GetString());

            var health = root.GetProperty("health");
            Assert.False(health.GetProperty("ok").GetBoolean());
            Assert.Equal("unhealthy", health.GetProperty("state").GetString());
            Assert.False(health.GetProperty("ready").GetBoolean());
            Assert.True(health.GetProperty("unhealthy").GetBoolean());
            Assert.Equal("startup-failed", health.GetProperty("failureKind").GetString());
            Assert.Equal(1, health.GetProperty("attempts").GetInt32());
            Assert.Equal(12, health.GetProperty("maxAttempts").GetInt32());

            var rollback = root.GetProperty("rollback");
            Assert.True(rollback.GetProperty("ok").GetBoolean());
            Assert.Equal("rollback", rollback.GetProperty("operation").GetString());
            Assert.Equal("served-model", rollback.GetProperty("deployment").GetString());
            Assert.Contains("systemctl --user disable --now 'tau-pod-served-model.service'", rollback.GetProperty("remoteCommand").GetString(), StringComparison.Ordinal);
            Assert.Equal("rolled back tau-pod-served-model\n", rollback.GetProperty("stdout").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmDeploy_WithHealthRetryOptions_RetriesUntilReadyAndPrintsJsonAttempts()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-deploy-retry-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var healthAttempts = 0;
        var execService = new PodExecService((psi, _) =>
        {
            var command = psi.ArgumentList[7];
            if (IsPreflightCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/mnt/models/models--org--model"), ""));
            }

            if (!command.Contains("/health", StringComparison.Ordinal))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started\n", ""));
            }

            healthAttempts++;
            return healthAttempts < 2
                ? Task.FromResult(new PodExecService.ProcessExecutionResult(3, "starting\n", ""))
                : Task.FromResult(new PodExecService.ProcessExecutionResult(0, "ready\n", ""));
        });
        var service = new PodVllmOrchestrationService(
            execService,
            delayAsync: (_, _) => Task.CompletedTask);

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "deploy", "--json", "--health-attempts", "3", "--health-backoff-ms", "1", configPath, "ssh-pod", "org/model", "served model"],
                execService,
                vllmService: service);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Contains("after 2 health attempt(s)", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
            var health = root.GetProperty("health");
            Assert.Equal("ready", health.GetProperty("state").GetString());
            Assert.Equal("none", health.GetProperty("failureKind").GetString());
            Assert.Equal(2, health.GetProperty("attempts").GetInt32());
            Assert.Equal(3, health.GetProperty("maxAttempts").GetInt32());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmDeploy_WithNoHealth_SkipsHealthProbe()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-deploy-no-health-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var commands = new List<string>();
        var execService = new PodExecService((psi, _) =>
        {
            var command = psi.ArgumentList[7];
            commands.Add(command);
            if (IsPreflightCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/mnt/models/models--org--model"), ""));
            }

            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started\n", ""));
        });

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "deploy", "--no-health", configPath, "ssh-pod", "org/model", "served model"],
                execService);

            Assert.Equal(0, exitCode);
            Assert.Equal(2, commands.Count);
            Assert.DoesNotContain("[health]", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("/health", string.Join('\n', commands), StringComparison.Ordinal);
            Assert.Contains("vllm_available", commands[0], StringComparison.Ordinal);
            Assert.Contains("systemctl --user enable --now 'tau-pod-served-model.service'", commands[1], StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmStatus_WithJsonOutput_PrintsRemoteStdout()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-status-json-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(0, "active\nLoaded: loaded\n", "")));

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["vllm", "status", "--json", configPath, "ssh-pod", "served model"], execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("status", root.GetProperty("operation").GetString());
            Assert.Equal("served-model", root.GetProperty("deployment").GetString());
            Assert.Equal("starting", root.GetProperty("state").GetString());
            Assert.False(root.GetProperty("ready").GetBoolean());
            Assert.False(root.GetProperty("unhealthy").GetBoolean());
            Assert.Equal("active\nLoaded: loaded\n", root.GetProperty("stdout").GetString());
            Assert.Contains("systemctl --user status 'tau-pod-served-model.service'", root.GetProperty("remoteCommand").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmHealth_WithJsonOutput_PrintsReadyContract()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-health-json-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(0, "ready\n", "")));

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["vllm", "health", "--json", configPath, "ssh-pod", "served model"], execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal("health", root.GetProperty("operation").GetString());
            Assert.Equal("served-model", root.GetProperty("deployment").GetString());
            Assert.Equal("ready", root.GetProperty("state").GetString());
            Assert.True(root.GetProperty("ready").GetBoolean());
            Assert.False(root.GetProperty("unhealthy").GetBoolean());
            Assert.Equal("none", root.GetProperty("failureKind").GetString());
            Assert.Equal(1, root.GetProperty("attempts").GetInt32());
            Assert.Equal(1, root.GetProperty("maxAttempts").GetInt32());
            Assert.Equal("ready\n", root.GetProperty("stdout").GetString());
            Assert.Contains("curl -fsS --max-time 2 \"http://127.0.0.1:$port/health\"", root.GetProperty("remoteCommand").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmHealth_WithTextOutput_ReturnsNonZeroForUnhealthy()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-health-text-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(2, "unhealthy\nTraceback\n", "")));

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["vllm", "health", configPath, "ssh-pod", "served model"], execService);

            var output = stdout.ToString();
            Assert.Equal(1, exitCode);
            Assert.Contains("ssh-pod | ok=False | operation=health | deployment=served-model | state=unhealthy | ready=False | unhealthy=True | failure=startup-failed | attempts=1/1 | exit=2", output, StringComparison.Ordinal);
            Assert.Contains("[health-command]", output, StringComparison.Ordinal);
            Assert.Contains("[stdout]", output, StringComparison.Ordinal);
            Assert.Contains("Traceback", output, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmStop_WhenRunnerFails_ReturnsNonZeroAndPrintsError()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-stop-fail-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(255, "", "permission denied")));

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["vllm", "stop", configPath, "ssh-pod", "served model"], execService);

            var output = stdout.ToString();
            Assert.Equal(1, exitCode);
            Assert.Contains("ssh-pod | ok=False | operation=stop | deployment=served-model | exit=255", output, StringComparison.Ordinal);
            Assert.Contains("vLLM stop failed: ssh exec failed (255)", output, StringComparison.Ordinal);
            Assert.Contains("systemctl --user disable --now 'tau-pod-served-model.service'", output, StringComparison.Ordinal);
            Assert.Contains("[stderr]", output, StringComparison.Ordinal);
            Assert.Contains("permission denied", output, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    private static PodsConfig ConfigWithHttpPod() => new()
    {
        Pods =
        [
            new PodDefinition
            {
                Id = "http-pod",
                Provider = "vllm",
                Model = "llama",
                Region = "local",
                Endpoint = "http://127.0.0.1:9",
                Enabled = true
            }
        ]
    };

    private static PodsConfig ConfigWithSshPod() => new()
    {
        Pods =
        [
            new PodDefinition
            {
                Id = "ssh-pod",
                Provider = "ssh",
                Model = "llama",
                Region = "lab",
                SshHost = "pods.example.internal",
                SshPort = 2222,
                ModelsPath = "/mnt/models",
                Enabled = true
            }
        ]
    };

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
