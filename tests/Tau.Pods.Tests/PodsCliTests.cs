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
}
