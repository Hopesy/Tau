using System.Text.Json;
using Tau.Pods.Cli;
using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Tests;

public class PodsCliTests
{
    private const string PiConfigDirEnvVar = "PI_CONFIG_DIR";
    private static readonly SemaphoreSlim CurrentDirectoryGate = new(1, 1);

    private static string SavePiDefaultConfig(string rootDirectory, PodsConfig config)
    {
        var configDir = Path.Combine(rootDirectory, "pi-config");
        var configPath = Path.Combine(configDir, "pods.json");
        Environment.SetEnvironmentVariable(PiConfigDirEnvVar, configDir);
        new PodsConfigStore().Save(configPath, config);
        return configPath;
    }

    private static string RemoteCommand(System.Diagnostics.ProcessStartInfo psi)
    {
        Assert.True(psi.ArgumentList.Count >= 8);
        return psi.ArgumentList[^1];
    }

    [Fact]
    public void ResolveDefaultConfigPath_UsesUserPiPodsJsonWhenPiConfigDirMissing()
    {
        var homeDirectory = Path.Combine(Path.GetTempPath(), "tau-home");

        var path = PodsCli.ResolveDefaultConfigPath(null, homeDirectory);

        Assert.Equal(Path.Combine(homeDirectory, ".pi", "pods.json"), path);
    }

    [Fact]
    public void ResolveDefaultConfigPath_UsesPiConfigDirWhenSet()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "tau-pi-config");

        var path = PodsCli.ResolveDefaultConfigPath(configDir, Path.Combine(Path.GetTempPath(), "tau-home"));

        Assert.Equal(Path.Combine(configDir, "pods.json"), path);
    }

    [Fact]
    public async Task Deploy_WithoutConfigPath_UsesDefaultConfig()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var previousConfigDir = Environment.GetEnvironmentVariable(PiConfigDirEnvVar);
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
            SavePiDefaultConfig(tempDir.FullName, ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["deploy", "http-pod", "meta/llama-3"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("http-pod | ok=False | Deploy requires SSH-based pod.", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, previousConfigDir);
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
    public async Task List_UsesPiConfigDirPodsJsonWhenNoConfigPath()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var previousConfigDir = Environment.GetEnvironmentVariable(PiConfigDirEnvVar);
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-pi-config-list-");
        var configDir = Path.Combine(tempDir.FullName, "pi-config");
        var envConfigPath = Path.Combine(configDir, "pods.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Environment.CurrentDirectory = tempDir.FullName;
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, configDir);
            Console.SetOut(stdout);
            Console.SetError(stderr);
            new PodsConfigStore().Save(envConfigPath, ConfigWithHttpPod("env-pod"));
            new PodsConfigStore().Save("tau.pods.json", ConfigWithHttpPod("local-pod"));

            var exitCode = await PodsCli.RunAsync(["list"]);

            var output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("env-pod", output, StringComparison.Ordinal);
            Assert.DoesNotContain("local-pod", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, previousConfigDir);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task Init_UsesPiConfigDirPodsJsonWhenNoConfigPath()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var previousConfigDir = Environment.GetEnvironmentVariable(PiConfigDirEnvVar);
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-pi-config-init-");
        var configDir = Path.Combine(tempDir.FullName, "pi-config");
        var envConfigPath = Path.Combine(configDir, "pods.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Environment.CurrentDirectory = tempDir.FullName;
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, configDir);
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await PodsCli.RunAsync(["init", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(envConfigPath));
            Assert.False(File.Exists(Path.Combine(tempDir.FullName, "tau.pods.json")));
            Assert.Equal(string.Empty, stderr.ToString());
            using var document = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(Path.GetFullPath(envConfigPath), document.RootElement.GetProperty("path").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, previousConfigDir);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task List_MissingDefaultConfigReturnsEmptyConfig()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousConfigDir = Environment.GetEnvironmentVariable(PiConfigDirEnvVar);
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-missing-list-");
        var configDir = Path.Combine(tempDir.FullName, "pi-config");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, configDir);
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await PodsCli.RunAsync(["list", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
            using var document = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(0, document.RootElement.GetProperty("podCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("activePodId").ValueKind);
            Assert.Empty(document.RootElement.GetProperty("pods").EnumerateArray());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, previousConfigDir);
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task Validate_MissingDefaultConfigReturnsValid()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousConfigDir = Environment.GetEnvironmentVariable(PiConfigDirEnvVar);
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-missing-validate-");
        var configDir = Path.Combine(tempDir.FullName, "pi-config");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, configDir);
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await PodsCli.RunAsync(["validate", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
            using var document = JsonDocument.Parse(stdout.ToString());
            Assert.True(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Config is valid.", document.RootElement.GetProperty("summary").GetString());
            Assert.Empty(document.RootElement.GetProperty("errors").EnumerateArray());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, previousConfigDir);
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task VllmPlan_ConfigOptionOverridesPiConfigDir()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousConfigDir = Environment.GetEnvironmentVariable(PiConfigDirEnvVar);
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-pi-config-explicit-");
        var configDir = Path.Combine(tempDir.FullName, "pi-config");
        var envConfigPath = Path.Combine(configDir, "pods.json");
        var explicitConfigPath = Path.Combine(tempDir.FullName, "explicit-pods.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, configDir);
            Console.SetOut(stdout);
            Console.SetError(stderr);
            new PodsConfigStore().Save(envConfigPath, ConfigWithHttpPod("env-pod"));
            new PodsConfigStore().Save(explicitConfigPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "plan", "--json", "--config", explicitConfigPath, "--pod", "ssh-pod", "org/model"]);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
            using var document = JsonDocument.Parse(stdout.ToString());
            Assert.Equal("ssh-pod", document.RootElement.GetProperty("pod").GetString());
            Assert.Equal("org/model", document.RootElement.GetProperty("model").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, previousConfigDir);
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task Logs_WithoutConfigPath_RejectsNonSshPod()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var previousConfigDir = Environment.GetEnvironmentVariable(PiConfigDirEnvVar);
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
            SavePiDefaultConfig(tempDir.FullName, ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["logs", "http-pod", "demo"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("http-pod | ok=False | Logs require SSH-based pod.", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, previousConfigDir);
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
        var previousConfigDir = Environment.GetEnvironmentVariable(PiConfigDirEnvVar);
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
            SavePiDefaultConfig(tempDir.FullName, ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["logs", "http-pod", "demo", "notanumber"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Invalid tail value", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, previousConfigDir);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task Logs_WithoutPodId_UsesActivePodAndEmitsJsonFailureKind()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-logs-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        string? capturedCommand = null;
        var execService = new PodExecService((psi, _) =>
        {
            capturedCommand = RemoteCommand(psi);
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "line-1\nline-2\n", string.Empty));
        });

        try
        {
            Console.SetOut(stdout);
            var config = ConfigWithSshPod();
            config.ActivePodId = "ssh-pod";
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(["logs", "--json", configPath, "served-model"], execService);

            Assert.Equal(0, exitCode);
            Assert.NotNull(capturedCommand);
            Assert.Contains("journalctl -u 'tau-pod-served-model' -n 100", capturedCommand!, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("served-model", root.GetProperty("deploymentName").GetString());
            Assert.Equal(100, root.GetProperty("tail").GetInt32());
            Assert.Equal("none", root.GetProperty("failureKind").GetString());
            Assert.Equal("line-1\nline-2\n", root.GetProperty("stdout").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Logs_WithConfigPodOptions_UsesExplicitPodAndTail()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-logs-options-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        string? capturedCommand = null;
        var execService = new PodExecService((psi, _) =>
        {
            capturedCommand = RemoteCommand(psi);
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "line-1\n", string.Empty));
        });

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["logs", "--json", "--config", configPath, "--pod", "ssh-pod", "served model", "42"], execService);

            Assert.Equal(0, exitCode);
            Assert.NotNull(capturedCommand);
            Assert.Contains("journalctl -u 'tau-pod-served-model' -n 42", capturedCommand!, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("served-model", root.GetProperty("deploymentName").GetString());
            Assert.Equal(42, root.GetProperty("tail").GetInt32());
            Assert.Equal("none", root.GetProperty("failureKind").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Logs_WithoutPodIdAndNoActive_ReportsNoActivePod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-logs-no-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stderr = new StringWriter();
        var previousError = Console.Error;

        try
        {
            Console.SetError(stderr);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["logs", configPath, "served model"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("No active pod. Use 'active <pod-id>' to set one.", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Exec_WithJsonOutput_IncludesStableFailureKind()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-exec-failure-kind-");
        var configPath = Path.Combine(tempDir.FullName, "pods.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(255, string.Empty, "Permission denied (publickey).\n")));

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["exec", "--json", configPath, "ssh-pod", "whoami"], execService);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal("ssh-auth-failed", root.GetProperty("failureKind").GetString());
            Assert.Equal(255, root.GetProperty("exitCode").GetInt32());
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Ssh_WithoutPodId_UsesActivePodAndEmitsJson()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-ssh-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        string? capturedCommand = null;
        var execService = new PodExecService((psi, _) =>
        {
            capturedCommand = RemoteCommand(psi);
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "gpu-ok\n", string.Empty));
        });

        try
        {
            Console.SetOut(stdout);
            var config = ConfigWithSshPod();
            config.ActivePodId = "ssh-pod";
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(["ssh", "--json", "--config", configPath, "nvidia-smi"], execService);

            Assert.Equal(0, exitCode);
            Assert.Equal("nvidia-smi", capturedCommand);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("nvidia-smi", root.GetProperty("command").GetString());
            Assert.Equal("gpu-ok\n", root.GetProperty("stdout").GetString());
            Assert.Equal("none", root.GetProperty("failureKind").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Ssh_WithConfigPodOptions_UsesExplicitPod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-ssh-options-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        string? capturedCommand = null;
        var execService = new PodExecService((psi, _) =>
        {
            capturedCommand = RemoteCommand(psi);
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "root\n", string.Empty));
        });

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["ssh", "--json", "--config", configPath, "--pod", "ssh-pod", "whoami"], execService);

            Assert.Equal(0, exitCode);
            Assert.Equal("whoami", capturedCommand);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("whoami", root.GetProperty("command").GetString());
            Assert.Equal("root\n", root.GetProperty("stdout").GetString());
            Assert.Equal("none", root.GetProperty("failureKind").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Ssh_WithoutPodIdAndNoActive_ReportsNoActivePod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-ssh-no-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stderr = new StringWriter();
        var previousError = Console.Error;
        var execCalled = false;
        var execService = new PodExecService((_, _) =>
        {
            execCalled = true;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, string.Empty, string.Empty));
        });

        try
        {
            Console.SetError(stderr);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["ssh", "--config", configPath, "nvidia-smi"], execService);

            Assert.Equal(1, exitCode);
            Assert.False(execCalled);
            Assert.Contains("No active pod. Use 'active <pod-id>' to set one.", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Deployments_WithoutConfigPath_RejectsNonSshPod()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var previousConfigDir = Environment.GetEnvironmentVariable(PiConfigDirEnvVar);
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
            SavePiDefaultConfig(tempDir.FullName, ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["deployments", "http-pod"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("http-pod | ok=False | Deployments require SSH-based pod.", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, previousConfigDir);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task Deployments_WithoutPodIdAndNoActive_ReportsNoActivePod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-deployments-no-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stderr = new StringWriter();
        var previousError = Console.Error;
        try
        {
            Console.SetError(stderr);
            new PodsConfigStore().Save(configPath, ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["deployments", configPath]);

            Assert.Equal(1, exitCode);
            Assert.Contains("No active pod. Use 'active <pod-id>' to set one.", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Deployments_WithoutPodId_UsesActivePod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-deployments-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                0,
                "{\"name\":\"served-model\",\"model\":\"org/model\",\"status\":\"running\",\"ts\":\"2026-05-27T00:00:00Z\"}\n",
                string.Empty)));

        try
        {
            Console.SetOut(stdout);
            var config = ConfigWithSshPod();
            config.ActivePodId = "ssh-pod";
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(["deployments", "--json", configPath], execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Single(root.GetProperty("deployments").EnumerateArray());
            Assert.Equal("served-model", root.GetProperty("deployments")[0].GetProperty("name").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Deployments_WithConfigPodOptions_UsesExplicitPod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-deployments-options-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                0,
                "{\"name\":\"served-model\",\"model\":\"org/model\",\"status\":\"running\",\"ts\":\"2026-05-27T00:00:00Z\"}\n",
                string.Empty)));

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["deployments", "--json", "--config", configPath, "--pod", "ssh-pod"], execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("served-model", root.GetProperty("deployments")[0].GetProperty("name").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Deployments_UnknownPod_ReportsNotFound()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var previousConfigDir = Environment.GetEnvironmentVariable(PiConfigDirEnvVar);
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-deployments-unknown-");
        var stderr = new StringWriter();
        var previousError = Console.Error;

        try
        {
            Environment.CurrentDirectory = tempDir.FullName;
            Console.SetError(stderr);
            SavePiDefaultConfig(tempDir.FullName, ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["deployments", "missing-pod"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Pod not found: missing-pod", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, previousConfigDir);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task VllmStatusHealthStop_WithoutPodId_UseActivePod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var commands = new List<string>();
        var execService = new PodExecService((psi, _) =>
        {
            var command = RemoteCommand(psi);
            commands.Add(command);
            if (command.Contains("status", StringComparison.Ordinal))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "active\nLoaded: loaded\n", string.Empty));
            }

            if (command.Contains("/health", StringComparison.Ordinal))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "ready\n", string.Empty));
            }

            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "stopped\n", string.Empty));
        });

        try
        {
            Console.SetOut(stdout);
            var config = ConfigWithSshPod();
            config.ActivePodId = "ssh-pod";
            new PodsConfigStore().Save(configPath, config);

            var statusExitCode = await PodsCli.RunAsync(["vllm", "status", "--json", configPath, "served model"], execService);
            Assert.Equal(0, statusExitCode);
            using (var statusDocument = JsonDocument.Parse(stdout.ToString()))
            {
                Assert.Equal("ssh-pod", statusDocument.RootElement.GetProperty("pod").GetString());
                Assert.Equal("status", statusDocument.RootElement.GetProperty("operation").GetString());
                Assert.Equal("served-model", statusDocument.RootElement.GetProperty("deployment").GetString());
            }

            stdout.GetStringBuilder().Clear();
            var healthExitCode = await PodsCli.RunAsync(["vllm", "health", "--json", configPath, "served model"], execService);
            Assert.Equal(0, healthExitCode);
            using (var healthDocument = JsonDocument.Parse(stdout.ToString()))
            {
                Assert.Equal("ssh-pod", healthDocument.RootElement.GetProperty("pod").GetString());
                Assert.Equal("health", healthDocument.RootElement.GetProperty("operation").GetString());
                Assert.Equal("served-model", healthDocument.RootElement.GetProperty("deployment").GetString());
                Assert.True(healthDocument.RootElement.GetProperty("ready").GetBoolean());
            }

            stdout.GetStringBuilder().Clear();
            var stopExitCode = await PodsCli.RunAsync(["vllm", "stop", "--json", configPath, "served model"], execService);
            Assert.Equal(0, stopExitCode);
            using (var stopDocument = JsonDocument.Parse(stdout.ToString()))
            {
                Assert.Equal("ssh-pod", stopDocument.RootElement.GetProperty("pod").GetString());
                Assert.Equal("stop", stopDocument.RootElement.GetProperty("operation").GetString());
                Assert.Equal("served-model", stopDocument.RootElement.GetProperty("deployment").GetString());
            }

            Assert.Equal(5, commands.Count);
            Assert.Contains("systemctl --user status 'tau-pod-served-model.service'", commands[0], StringComparison.Ordinal);
            Assert.Contains("cat ~/.tau_pods/served-model.json", commands[1], StringComparison.Ordinal);
            Assert.Contains("curl -fsS --max-time 2 \"http://127.0.0.1:$port/health\"", commands[2], StringComparison.Ordinal);
            Assert.Contains("cat ~/.tau_pods/served-model.json", commands[3], StringComparison.Ordinal);
            Assert.Contains("systemctl --user disable --now 'tau-pod-served-model.service'", commands[4], StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmStatus_WithConfigPodOptions_UsesExplicitPod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-status-options-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(0, "active\nLoaded: loaded\n", string.Empty)));

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "status", "--json", "--config", configPath, "--pod", "ssh-pod", "served model"],
                execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("pod").GetString());
            Assert.Equal("status", root.GetProperty("operation").GetString());
            Assert.Equal("served-model", root.GetProperty("deployment").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ModelList_WithoutConfigPath_RejectsNonSshPod()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var previousConfigDir = Environment.GetEnvironmentVariable(PiConfigDirEnvVar);
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
            SavePiDefaultConfig(tempDir.FullName, ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["model", "list", "http-pod"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("http-pod | ok=False | Model list requires SSH-based pod.", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, previousConfigDir);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task ModelList_WithJsonOutput_IncludesSnapshotMetadata()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-model-list-json-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                0,
                "models--org--model\t1\t/mnt/models/models--org--model/snapshots/rev-main\tnone\n" +
                "models--org--ambiguous\t2\t\tmodel-snapshot-ambiguous\n",
                string.Empty)));

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["model", "list", "--json", configPath, "ssh-pod"], execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());

            var models = root.GetProperty("models");
            Assert.Equal(2, models.GetArrayLength());
            Assert.Equal("org/model", models[0].GetProperty("modelId").GetString());
            Assert.Equal(1, models[0].GetProperty("snapshotCount").GetInt32());
            Assert.Equal("/mnt/models/models--org--model/snapshots/rev-main", models[0].GetProperty("resolvedModelPath").GetString());
            Assert.Equal("none", models[0].GetProperty("snapshotFailureKind").GetString());
            Assert.Equal("org/ambiguous", models[1].GetProperty("modelId").GetString());
            Assert.Equal(2, models[1].GetProperty("snapshotCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, models[1].GetProperty("resolvedModelPath").ValueKind);
            Assert.Equal("model-snapshot-ambiguous", models[1].GetProperty("snapshotFailureKind").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ModelList_WithoutPodId_UsesActivePod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-model-list-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                0,
                "models--org--model\t1\t/mnt/models/models--org--model/snapshots/rev-main\tnone\n",
                string.Empty)));

        try
        {
            Console.SetOut(stdout);
            var config = ConfigWithSshPod();
            config.ActivePodId = "ssh-pod";
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(["model", "list", "--json", configPath], execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("org/model", root.GetProperty("models")[0].GetProperty("modelId").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ModelList_WithConfigPodOptions_UsesExplicitPod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-model-list-options-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                0,
                "models--org--model\t1\t/mnt/models/models--org--model/snapshots/rev-main\tnone\n",
                string.Empty)));

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["model", "list", "--json", "--config", configPath, "--pod", "ssh-pod"], execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("org/model", root.GetProperty("models")[0].GetProperty("modelId").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
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
    public async Task ModelPull_WithSnapshotOption_PassesRevisionToRemoteCommand()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-model-pull-revision-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        string? capturedCommand = null;
        var execService = new PodExecService((psi, _) =>
        {
            capturedCommand = psi.ArgumentList[^1];
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "downloaded\n", string.Empty));
        });

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                [
                    "model",
                    "pull",
                    "--json",
                    "--config",
                    configPath,
                    "--pod",
                    "ssh-pod",
                    "--snapshot",
                    "rev-b",
                    "meta-llama/Llama-3.1-8B"
                ],
                execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("pull", root.GetProperty("operation").GetString());
            Assert.Equal("meta-llama/Llama-3.1-8B", root.GetProperty("modelId").GetString());
            Assert.NotNull(capturedCommand);
            Assert.Contains("huggingface-cli download 'meta-llama/Llama-3.1-8B' --revision 'rev-b'", capturedCommand!, StringComparison.Ordinal);
            Assert.Contains("python3 -m huggingface_hub.commands.huggingface_cli download 'meta-llama/Llama-3.1-8B' --revision 'rev-b'", capturedCommand!, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ModelStatus_WithJsonOutput_IncludesResolvedSnapshot()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-model-status-json-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                0,
                "model_cache_path=/mnt/models/models--org--model\npresent=true\nsnapshot_count=1\nresolved_model_path=/mnt/models/models--org--model/snapshots/rev-main\nfailure_kind=none\n",
                string.Empty)));

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["model", "status", "--json", configPath, "ssh-pod", "org/model"], execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("org/model", root.GetProperty("modelId").GetString());
            Assert.True(root.GetProperty("present").GetBoolean());
            Assert.Equal("/mnt/models/models--org--model", root.GetProperty("modelCachePath").GetString());
            Assert.Equal(1, root.GetProperty("snapshotCount").GetInt32());
            Assert.Equal("/mnt/models/models--org--model/snapshots/rev-main", root.GetProperty("resolvedModelPath").GetString());
            Assert.Equal("none", root.GetProperty("snapshotFailureKind").GetString());
            Assert.Contains("resolved snapshot", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ModelStatus_WithConfigPodOptions_UsesExplicitPod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-model-status-options-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                0,
                "model_cache_path=/mnt/models/models--org--model\npresent=true\nsnapshot_count=1\nresolved_model_path=/mnt/models/models--org--model/snapshots/rev-main\nfailure_kind=none\n",
                string.Empty)));

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["model", "status", "--json", "--config", configPath, "--pod", "ssh-pod", "org/model"], execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.Equal("org/model", root.GetProperty("modelId").GetString());
            Assert.True(root.GetProperty("present").GetBoolean());
            Assert.Equal("/mnt/models/models--org--model/snapshots/rev-main", root.GetProperty("resolvedModelPath").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ModelStatus_WithoutPodId_UsesActivePodAndTreatsArgumentAsModelId()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-model-status-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var execService = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(
                0,
                "model_cache_path=/mnt/models/models--org--model\npresent=true\nsnapshot_count=1\nresolved_model_path=/mnt/models/models--org--model/snapshots/rev-main\nfailure_kind=none\n",
                string.Empty)));

        try
        {
            Console.SetOut(stdout);
            var config = ConfigWithSshPod();
            config.ActivePodId = "ssh-pod";
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(["model", "status", "--json", configPath, "org/model"], execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.Equal("org/model", root.GetProperty("modelId").GetString());
            Assert.True(root.GetProperty("present").GetBoolean());
            Assert.Equal("none", root.GetProperty("snapshotFailureKind").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ModelStatus_UnknownPod_ReportsNotFound()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var previousConfigDir = Environment.GetEnvironmentVariable(PiConfigDirEnvVar);
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-model-status-unknown-");
        var stderr = new StringWriter();
        var previousError = Console.Error;

        try
        {
            Environment.CurrentDirectory = tempDir.FullName;
            Console.SetError(stderr);
            SavePiDefaultConfig(tempDir.FullName, ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["model", "status", "missing-pod", "model"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Pod not found: missing-pod", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, previousConfigDir);
            Environment.CurrentDirectory = previousDirectory;
            tempDir.Delete(recursive: true);
            CurrentDirectoryGate.Release();
        }
    }

    [Fact]
    public async Task ModelList_WithoutPodIdAndNoActive_ReportsNoActivePod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-model-list-no-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stderr = new StringWriter();
        var previousError = Console.Error;

        try
        {
            Console.SetError(stderr);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["model", "list", configPath]);

            Assert.Equal(1, exitCode);
            Assert.Contains("No active pod. Use 'active <pod-id>' to set one.", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
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
    public async Task SetupRegister_WithJsonOutput_CreatesPodAndSetsActiveWithoutRunningSetup()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-setup-register-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await PodsCli.RunAsync(
                [
                    "setup",
                    "--json",
                    "--mount",
                    "mount -t nfs store:/models /mnt/models",
                    "--vllm",
                    "gpt-oss",
                    configPath,
                    "gpu-a",
                    "ssh -p 2200 user@pods.example.internal"
                ]);

            Assert.Equal(0, exitCode);
            Assert.Empty(stderr.ToString());

            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("registered").GetBoolean());
            Assert.False(root.GetProperty("setupExecuted").GetBoolean());
            Assert.True(root.GetProperty("active").GetBoolean());
            Assert.Equal("gpu-a", root.GetProperty("activePodId").GetString());
            Assert.Equal("user@pods.example.internal", root.GetProperty("sshHost").GetString());
            Assert.Equal(2200, root.GetProperty("sshPort").GetInt32());
            Assert.Equal("/mnt/models", root.GetProperty("modelsPath").GetString());
            Assert.Equal("gpt-oss", root.GetProperty("vllmVersion").GetString());
            Assert.Equal(Path.GetFullPath(configPath), root.GetProperty("configPath").GetString());

            var savedConfig = new PodsConfigStore().Load(configPath);
            Assert.Equal("gpu-a", savedConfig.ActivePodId);
            var savedPod = Assert.Single(savedConfig.Pods);
            Assert.Equal("gpu-a", savedPod.Id);
            Assert.Equal("ssh", savedPod.Provider);
            Assert.Equal("unassigned", savedPod.Model);
            Assert.Equal("registered", savedPod.Region);
            Assert.Equal("user@pods.example.internal", savedPod.SshHost);
            Assert.Equal(2200, savedPod.SshPort);
            Assert.Equal("/mnt/models", savedPod.ModelsPath);
            Assert.Equal("gpt-oss", savedPod.VllmVersion);
            Assert.Empty(savedPod.Gpus);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ActiveAndRemove_UpdateActivePodConfig()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-active-remove-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;

        try
        {
            Console.SetOut(stdout);
            var store = new PodsConfigStore();
            var config = new PodsConfig
            {
                ActivePodId = "ssh-pod",
                Pods =
                [
                    ConfigWithSshPod().Pods[0],
                    new PodDefinition
                    {
                        Id = "gpu-b",
                        Provider = "ssh",
                        Model = "llama",
                        Region = "lab",
                        SshHost = "gpu-b.example.internal",
                        SshPort = 2222,
                        ModelsPath = "/mnt/models",
                        Enabled = true
                    }
                ]
            };
            store.Save(configPath, config);

            var activeExitCode = await PodsCli.RunAsync(["active", "--json", configPath, "gpu-b"]);

            Assert.Equal(0, activeExitCode);
            using (var activeDocument = JsonDocument.Parse(stdout.ToString()))
            {
                Assert.Equal("gpu-b", activeDocument.RootElement.GetProperty("activePodId").GetString());
                Assert.Equal(Path.GetFullPath(configPath), activeDocument.RootElement.GetProperty("configPath").GetString());
            }

            Assert.Equal("gpu-b", store.Load(configPath).ActivePodId);

            stdout.GetStringBuilder().Clear();
            var removeExitCode = await PodsCli.RunAsync(["remove", "--json", configPath, "gpu-b"]);

            Assert.Equal(0, removeExitCode);
            using (var removeDocument = JsonDocument.Parse(stdout.ToString()))
            {
                Assert.Equal("gpu-b", removeDocument.RootElement.GetProperty("podId").GetString());
                Assert.Equal(JsonValueKind.Null, removeDocument.RootElement.GetProperty("activePodId").ValueKind);
            }

            var savedConfig = store.Load(configPath);
            Assert.Null(savedConfig.ActivePodId);
            var savedPod = Assert.Single(savedConfig.Pods);
            Assert.Equal("ssh-pod", savedPod.Id);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SetupRegister_RejectsComplexSshOptions()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-setup-register-complex-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stderr = new StringWriter();
        var previousError = Console.Error;

        try
        {
            Console.SetError(stderr);

            var exitCode = await PodsCli.RunAsync(
                [
                    "setup",
                    "--models-path",
                    "/mnt/models",
                    configPath,
                    "gpu-a",
                    "ssh -i key.pem user@pods.example.internal"
                ]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Complex SSH options are not supported", stderr.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(configPath));
        }
        finally
        {
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SetupPlan_WithJsonOutput_PrintsPlanAndDoesNotExposeTokenValues()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-setup-plan-json-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var planner = new PodSetupPlanner(name => name switch
        {
            "HF_TOKEN" => "hf_secret_for_test",
            "PI_API_KEY" => "pi_secret_for_test",
            _ => null
        });

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var config = ConfigWithSshPod();
            config.Pods[0].VllmVersion = "nightly";
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(
                [
                    "setup",
                    "plan",
                    "--json",
                    "--mount",
                    "mount -t nfs store:/models /mnt/models",
                    "--models-path",
                    "/data/hf-cache",
                    "--vllm",
                    "gpt-oss",
                    configPath,
                    "ssh-pod"
                ],
                setupPlanner: planner);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("hf_secret_for_test", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("pi_secret_for_test", stdout.ToString(), StringComparison.Ordinal);

            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.Equal("pods.example.internal", root.GetProperty("sshHost").GetString());
            Assert.Equal(2222, root.GetProperty("sshPort").GetInt32());
            Assert.Equal("/data/hf-cache", root.GetProperty("modelsPath").GetString());
            Assert.Equal("mount -t nfs store:/models /mnt/models", root.GetProperty("mountCommand").GetString());
            Assert.Equal("gpt-oss", root.GetProperty("vllmVersion").GetString());
            Assert.True(root.GetProperty("hfTokenConfigured").GetBoolean());
            Assert.True(root.GetProperty("piApiKeyConfigured").GetBoolean());
            Assert.Equal("/tmp/pod_setup.sh", root.GetProperty("scriptRemotePath").GetString());
            Assert.True(root.GetProperty("planOnly").GetBoolean());
            Assert.NotEmpty(root.GetProperty("steps").EnumerateArray());

            var setupCommand = root.GetProperty("setupCommand").GetString()!;
            Assert.Contains("bash '/tmp/pod_setup.sh'", setupCommand, StringComparison.Ordinal);
            Assert.Contains("--hf-token \"$HF_TOKEN\"", setupCommand, StringComparison.Ordinal);
            Assert.Contains("--vllm-api-key \"$PI_API_KEY\"", setupCommand, StringComparison.Ordinal);
            Assert.Contains("--models-path '/data/hf-cache'", setupCommand, StringComparison.Ordinal);
            Assert.Contains("--vllm 'gpt-oss'", setupCommand, StringComparison.Ordinal);
            Assert.Contains("--mount 'mount -t nfs store:/models /mnt/models'", setupCommand, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SetupPlan_RejectsNonSshPod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-setup-plan-http-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stderr = new StringWriter();
        var previousError = Console.Error;

        try
        {
            Console.SetError(stderr);
            new PodsConfigStore().Save(configPath, ConfigWithHttpPod());

            var exitCode = await PodsCli.RunAsync(["setup", "plan", configPath, "http-pod"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Setup requires SSH-based pod.", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SetupRun_WithJsonOutput_ExecutesPlanAndDoesNotExposeTokenValues()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-setup-run-json-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var scriptPath = Path.Combine(tempDir.FullName, "pod_setup.sh");
        await File.WriteAllTextAsync(scriptPath, "#!/usr/bin/env bash\n");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var calls = new List<(string FileName, string[] Arguments, string? StandardInput)>();
        var setupService = new PodSetupService(
            new PodSetupPlanner(Env),
            (psi, stdin, _) =>
            {
                calls.Add((psi.FileName, psi.ArgumentList.ToArray(), stdin));
                if (psi.FileName.Equals("ssh", StringComparison.OrdinalIgnoreCase) &&
                    psi.ArgumentList[^1].Contains("nvidia-smi", StringComparison.Ordinal))
                {
                    return Task.FromResult(new PodSetupService.ProcessExecutionResult(
                        0,
                        "0, NVIDIA L40S, 46068 MiB\n",
                        string.Empty));
                }

                if (psi.FileName.Equals("ssh", StringComparison.OrdinalIgnoreCase) &&
                    psi.ArgumentList[^1].Contains("pod_setup.sh", StringComparison.Ordinal))
                {
                    Assert.Equal("hf_cli_secret\npi_cli_secret\n", stdin);
                    return Task.FromResult(new PodSetupService.ProcessExecutionResult(
                        0,
                        "setup ok hf_cli_secret pi_cli_secret\n",
                        string.Empty));
                }

                return Task.FromResult(new PodSetupService.ProcessExecutionResult(0, "ok\n", string.Empty));
            },
            Env);

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var config = ConfigWithSshPod();
            config.Pods[0].VllmVersion = "nightly";
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(
                [
                    "setup",
                    "run",
                    "--json",
                    "--script",
                    scriptPath,
                    "--mount",
                    "mount -t nfs store:/models /mnt/models",
                    "--models-path",
                    "/data/hf-cache",
                    "--vllm",
                    "gpt-oss",
                    configPath,
                    "ssh-pod"
                ],
                setupService: setupService);

            var output = stdout.ToString();
            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("hf_cli_secret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("pi_cli_secret", output, StringComparison.Ordinal);
            Assert.Equal(4, calls.Count);
            Assert.Equal("ssh", calls[0].FileName);
            Assert.Equal("echo 'SSH OK'", calls[0].Arguments[^1]);
            Assert.Equal("scp", calls[1].FileName);
            Assert.Equal(scriptPath, calls[1].Arguments[2]);
            Assert.Equal("pods.example.internal:/tmp/pod_setup.sh", calls[1].Arguments[3]);
            Assert.Contains("--hf-token \"$TAU_HF_TOKEN\"", calls[2].Arguments[^1], StringComparison.Ordinal);
            Assert.Contains("--vllm-api-key \"$TAU_PI_API_KEY\"", calls[2].Arguments[^1], StringComparison.Ordinal);
            Assert.DoesNotContain("hf_cli_secret", calls[2].Arguments[^1], StringComparison.Ordinal);
            Assert.DoesNotContain("pi_cli_secret", calls[2].Arguments[^1], StringComparison.Ordinal);

            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(scriptPath, root.GetProperty("scriptPath").GetString());
            Assert.True(root.GetProperty("configUpdated").GetBoolean());
            Assert.Equal(Path.GetFullPath(configPath), root.GetProperty("configPath").GetString());
            Assert.Contains("1 GPU", root.GetProperty("summary").GetString(), StringComparison.Ordinal);

            var plan = root.GetProperty("plan");
            Assert.Equal("gpt-oss", plan.GetProperty("vllmVersion").GetString());
            Assert.Equal("/data/hf-cache", plan.GetProperty("modelsPath").GetString());
            Assert.Contains("--hf-token \"$HF_TOKEN\"", plan.GetProperty("setupCommand").GetString(), StringComparison.Ordinal);

            var steps = root.GetProperty("steps");
            Assert.Equal(4, steps.GetArrayLength());
            Assert.Equal("ssh-test", steps[0].GetProperty("name").GetString());
            Assert.Equal("scp-script", steps[1].GetProperty("name").GetString());
            Assert.Equal("run-setup", steps[2].GetProperty("name").GetString());
            Assert.Equal("detect-gpus", steps[3].GetProperty("name").GetString());
            Assert.Equal("setup ok [REDACTED] [REDACTED]\n", steps[2].GetProperty("stdout").GetString());
            Assert.Contains("--hf-token \"$HF_TOKEN\"", steps[2].GetProperty("command").GetString(), StringComparison.Ordinal);

            var gpus = root.GetProperty("gpus");
            Assert.Single(gpus.EnumerateArray());
            Assert.Equal(0, gpus[0].GetProperty("id").GetInt32());
            Assert.Equal("NVIDIA L40S", gpus[0].GetProperty("name").GetString());
            Assert.Equal("46068 MiB", gpus[0].GetProperty("memory").GetString());

            var savedConfigText = await File.ReadAllTextAsync(configPath);
            Assert.DoesNotContain("hf_cli_secret", savedConfigText, StringComparison.Ordinal);
            Assert.DoesNotContain("pi_cli_secret", savedConfigText, StringComparison.Ordinal);

            var savedConfig = new PodsConfigStore().Load(configPath);
            var savedPod = Assert.Single(savedConfig.Pods);
            Assert.Equal("ssh-pod", savedPod.Id);
            Assert.Equal("llama", savedPod.Model);
            Assert.Equal("lab", savedPod.Region);
            Assert.Equal("pods.example.internal", savedPod.SshHost);
            Assert.Equal(2222, savedPod.SshPort);
            Assert.Equal("/data/hf-cache", savedPod.ModelsPath);
            Assert.Equal("gpt-oss", savedPod.VllmVersion);
            var savedGpu = Assert.Single(savedPod.Gpus);
            Assert.Equal(0, savedGpu.Id);
            Assert.Equal("NVIDIA L40S", savedGpu.Name);
            Assert.Equal("46068 MiB", savedGpu.Memory);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }

        static string? Env(string name) => name switch
        {
            "HF_TOKEN" => "hf_cli_secret",
            "PI_API_KEY" => "pi_cli_secret",
            _ => null
        };
    }

    [Fact]
    public async Task SetupRun_WhenSshFails_DoesNotUpdateConfig()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-setup-run-fail-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var scriptPath = Path.Combine(tempDir.FullName, "pod_setup.sh");
        await File.WriteAllTextAsync(scriptPath, "#!/usr/bin/env bash\n");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var calls = new List<(string FileName, string[] Arguments)>();
        var setupService = new PodSetupService(
            new PodSetupPlanner(Env),
            (psi, _, _) =>
            {
                calls.Add((psi.FileName, psi.ArgumentList.ToArray()));
                return Task.FromResult(new PodSetupService.ProcessExecutionResult(
                    255,
                    string.Empty,
                    "permission denied"));
            },
            Env);

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                [
                    "setup",
                    "run",
                    "--json",
                    "--script",
                    scriptPath,
                    "--models-path",
                    "/data/hf-cache",
                    "--vllm",
                    "gpt-oss",
                    configPath,
                    "ssh-pod"
                ],
                setupService: setupService);

            Assert.Equal(1, exitCode);
            Assert.Single(calls);
            Assert.Equal("ssh", calls[0].FileName);

            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("podId").GetString());
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.False(root.GetProperty("configUpdated").GetBoolean());
            Assert.Equal(Path.GetFullPath(configPath), root.GetProperty("configPath").GetString());
            Assert.Contains("SSH connection test failed", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
            Assert.Empty(root.GetProperty("gpus").EnumerateArray());

            var savedPod = Assert.Single(new PodsConfigStore().Load(configPath).Pods);
            Assert.Equal("/mnt/models", savedPod.ModelsPath);
            Assert.Null(savedPod.VllmVersion);
            Assert.Empty(savedPod.Gpus);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }

        static string? Env(string name) => name switch
        {
            "HF_TOKEN" => "hf_cli_secret",
            "PI_API_KEY" => "pi_cli_secret",
            _ => null
        };
    }

    [Fact]
    public async Task VllmPlan_WithoutConfigPath_UsesDefaultConfigAndPrintsPlan()
    {
        await CurrentDirectoryGate.WaitAsync();
        var previousDirectory = Environment.CurrentDirectory;
        var previousConfigDir = Environment.GetEnvironmentVariable(PiConfigDirEnvVar);
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
            SavePiDefaultConfig(tempDir.FullName, ConfigWithSshPod());

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
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, previousConfigDir);
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
            Assert.Contains("model_cache_path='/mnt/models/models--org--model'", serveCommand, StringComparison.Ordinal);
            Assert.Contains("vllm serve \"$resolved_model_path\"", serveCommand, StringComparison.Ordinal);
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
    public async Task VllmPlan_WithVllmExtraArgs_PassesArgsToServeCommand()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-extra-");
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

            var exitCode = await PodsCli.RunAsync(
                [
                    "vllm",
                    "plan",
                    "--json",
                    configPath,
                    "ssh-pod",
                    "org/model",
                    "served model",
                    "--vllm",
                    "--tensor-parallel-size",
                    "2",
                    "--max-model-len",
                    "32768"
                ]);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
            using var document = JsonDocument.Parse(stdout.ToString());
            var serveCommand = document.RootElement.GetProperty("serveCommand").GetString()!;
            Assert.Contains("model_cache_path='/mnt/models/models--org--model'", serveCommand, StringComparison.Ordinal);
            Assert.Contains("vllm serve \"$resolved_model_path\"", serveCommand, StringComparison.Ordinal);
            Assert.Contains("'--tensor-parallel-size' '2' '--max-model-len' '32768'", serveCommand, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmPlan_WithConfigPodAndNameOptions_UsesExplicitPodAndDoesNotTreatModelSlashAsPath()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-options-");
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

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "plan", "--json", "--config", configPath, "--pod", "ssh-pod", "org/model", "--name", "served model"]);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("pod").GetString());
            Assert.Equal("served-model", root.GetProperty("deployment").GetString());
            Assert.Equal("org/model", root.GetProperty("model").GetString());
            Assert.Equal("/mnt/models/models--org--model", root.GetProperty("modelPath").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmPlan_WithSnapshotOption_PrintsRevisionAwarePlan()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-revision-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "plan", "--json", "--config", configPath, "--pod", "ssh-pod", "--snapshot", "rev-b", "org/model", "--name", "served model"]);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("rev-b", root.GetProperty("revision").GetString());
            Assert.Equal("rev-b", root.GetProperty("metadata").GetProperty("revision").GetString());
            var serveCommand = root.GetProperty("serveCommand").GetString()!;
            Assert.Contains("requested_revision='rev-b'", serveCommand, StringComparison.Ordinal);
            Assert.Contains("snapshots/$requested_revision", serveCommand, StringComparison.Ordinal);
            Assert.Contains("exit 16", serveCommand, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmPlan_WithoutPodOption_UsesActivePodWhenNameIsExplicit()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var config = ConfigWithSshPod();
            config.ActivePodId = "ssh-pod";
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "plan", "--json", "--config", configPath, "org/model", "--name", "served model"]);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("Config not found", stderr.ToString(), StringComparison.Ordinal);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("pod").GetString());
            Assert.Equal("served-model", root.GetProperty("deployment").GetString());
            Assert.Equal("org/model", root.GetProperty("model").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmPlan_WithoutPodOptionAndNoActive_ReportsNoActivePod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-no-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stderr = new StringWriter();
        var previousError = Console.Error;

        try
        {
            Console.SetError(stderr);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "plan", "--json", "--config", configPath, "org/model", "--name", "served model"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("No active pod. Use 'active <pod-id>' to set one.", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmPlan_WithEmptyVllmExtraArgs_PrintsUsage()
    {
        var stderr = new StringWriter();
        var previousError = Console.Error;

        try
        {
            Console.SetError(stderr);

            var exitCode = await PodsCli.RunAsync(["vllm", "plan", "ssh-pod", "org/model", "--vllm"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Usage: vllm plan", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
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

            var exitCode = await PodsCli.RunAsync(["vllm", "plan", "--pod", "ssh-pod"]);

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
        var previousConfigDir = Environment.GetEnvironmentVariable(PiConfigDirEnvVar);
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-unknown-");
        var stderr = new StringWriter();
        var previousError = Console.Error;

        try
        {
            Environment.CurrentDirectory = tempDir.FullName;
            Console.SetError(stderr);
            SavePiDefaultConfig(tempDir.FullName, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(["vllm", "plan", "missing-pod", "org/model"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Pod not found: missing-pod", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(PiConfigDirEnvVar, previousConfigDir);
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
    public async Task VllmPlan_ForKnownModel_PrintsKnownModelContract()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-known-model-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;

        try
        {
            Console.SetOut(stdout);
            var config = ConfigWithSshPod();
            config.Pods[0].Gpus.Add(new PodGpuInfo(0, "NVIDIA H100 80GB HBM3", "81559 MiB"));
            config.Pods[0].Gpus.Add(new PodGpuInfo(1, "NVIDIA H100 80GB HBM3", "81559 MiB"));
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "plan", "--json", "--config", configPath, "--pod", "ssh-pod", "openai/gpt-oss-120b"]);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            var knownModel = root.GetProperty("knownModel");
            Assert.Equal("GPT-OSS-120B", knownModel.GetProperty("name").GetString());
            Assert.Equal(2, knownModel.GetProperty("gpuCount").GetInt32());
            Assert.Contains(
                knownModel.GetProperty("args").EnumerateArray(),
                arg => arg.GetString() == "--tensor-parallel-size");
            Assert.Contains("--tensor-parallel-size", root.GetProperty("serveCommand").GetString(), StringComparison.Ordinal);
            Assert.Equal("GPT-OSS-120B", root.GetProperty("metadata").GetProperty("knownModel").GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("requestedGpuCount").ValueKind);
            Assert.Equal(2, root.GetProperty("selectedGpus").GetArrayLength());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmPlan_WithGpuMemoryAndContextOptions_PrintsPlanningContract()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-options-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;

        try
        {
            Console.SetOut(stdout);
            var config = ConfigWithSshPod();
            config.Pods[0].Gpus.Add(new PodGpuInfo(0, "NVIDIA H100 80GB HBM3", "81559 MiB"));
            config.Pods[0].Gpus.Add(new PodGpuInfo(1, "NVIDIA H100 80GB HBM3", "81559 MiB"));
            config.Pods[0].Gpus.Add(new PodGpuInfo(2, "NVIDIA H100 80GB HBM3", "81559 MiB"));
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(
                [
                    "vllm",
                    "plan",
                    "--json",
                    "--config",
                    configPath,
                    "--pod",
                    "ssh-pod",
                    "openai/gpt-oss-120b",
                    "--gpus",
                    "2",
                    "--memory",
                    "50%",
                    "--context",
                    "32k"
                ]);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal(2, root.GetProperty("requestedGpuCount").GetInt32());
            Assert.Equal(2, root.GetProperty("selectedGpus").GetArrayLength());
            Assert.Equal(0, root.GetProperty("selectedGpus")[0].GetInt32());
            Assert.Equal(1, root.GetProperty("selectedGpus")[1].GetInt32());
            Assert.Equal("50%", root.GetProperty("memory").GetString());
            Assert.Equal(0.5d, root.GetProperty("memoryUtilization").GetDouble());
            Assert.Equal("32k", root.GetProperty("context").GetString());
            Assert.Equal(32768, root.GetProperty("contextTokens").GetInt32());
            Assert.Contains("--tensor-parallel-size", root.GetProperty("serveCommand").GetString(), StringComparison.Ordinal);
            Assert.Contains("--gpu-memory-utilization", root.GetProperty("serveCommand").GetString(), StringComparison.Ordinal);
            Assert.Contains("'0.5'", root.GetProperty("serveCommand").GetString(), StringComparison.Ordinal);
            Assert.Contains("--max-model-len", root.GetProperty("serveCommand").GetString(), StringComparison.Ordinal);
            Assert.Contains("'32768'", root.GetProperty("serveCommand").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("'0.94'", root.GetProperty("serveCommand").GetString(), StringComparison.Ordinal);

            var metadata = root.GetProperty("metadata");
            Assert.Equal(2, metadata.GetProperty("requestedGpuCount").GetInt32());
            Assert.Equal(2, metadata.GetProperty("selectedGpus").GetArrayLength());
            Assert.Equal(0.5d, metadata.GetProperty("memoryUtilization").GetDouble());
            Assert.Equal(32768, metadata.GetProperty("contextTokens").GetInt32());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmPlan_WithVllmExtraArgs_IgnoresConvenienceOptions()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-options-override-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;

        try
        {
            Console.SetOut(stdout);
            var config = ConfigWithSshPod();
            config.Pods[0].Gpus.Add(new PodGpuInfo(0, "NVIDIA B200", "180000 MiB"));
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(
                [
                    "vllm",
                    "plan",
                    "--json",
                    "--config",
                    configPath,
                    "--pod",
                    "ssh-pod",
                    "openai/gpt-oss-20b",
                    "--gpus",
                    "1",
                    "--memory",
                    "50%",
                    "--context",
                    "32k",
                    "--vllm",
                    "--manual-arg"
                ]);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("knownModel").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("requestedGpuCount").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("selectedGpus").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("memory").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("context").ValueKind);
            Assert.Contains("--manual-arg", root.GetProperty("serveCommand").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("CUDA_VISIBLE_DEVICES", root.GetProperty("serveCommand").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("gpu-memory-utilization", root.GetProperty("serveCommand").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("max-model-len", root.GetProperty("serveCommand").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmPlan_UnknownModelWithGpuCountReportsError()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-plan-gpus-error-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var config = ConfigWithSshPod();
            config.Pods[0].Gpus.Add(new PodGpuInfo(0, "NVIDIA H100", "80000 MiB"));
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "plan", "--config", configPath, "--pod", "ssh-pod", "org/model", "--gpus", "1"]);

            Assert.Equal(1, exitCode);
            Assert.Empty(stdout.ToString());
            Assert.Contains("--gpus can only be used with predefined models", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
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
            capturedCommand = RemoteCommand(psi);
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
    public async Task VllmPreflight_WithoutPodOption_UsesActivePod()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-preflight-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        string? capturedCommand = null;
        var execService = new PodExecService((psi, _) =>
        {
            capturedCommand = RemoteCommand(psi);
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/mnt/models/models--org--model", "rev-abc"), ""));
        });

        try
        {
            Console.SetOut(stdout);
            var config = ConfigWithSshPod();
            config.ActivePodId = "ssh-pod";
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "preflight", "--json", "--config", configPath, "org/model", "--name", "served model"],
                execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("pod").GetString());
            Assert.Equal("served-model", root.GetProperty("deployment").GetString());
            Assert.Equal("org/model", root.GetProperty("model").GetString());
            Assert.Equal("/mnt/models/models--org--model", root.GetProperty("modelCachePath").GetString());
            Assert.NotNull(capturedCommand);
            Assert.Contains("models--org--model", capturedCommand!, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmPreflight_WithRevisionOption_PassesRevision()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-preflight-revision-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        string? capturedCommand = null;
        var execService = new PodExecService((psi, _) =>
        {
            capturedCommand = RemoteCommand(psi);
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/mnt/models/models--org--model", "rev-b"), ""));
        });

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "preflight", "--json", "--config", configPath, "--pod", "ssh-pod", "--revision", "rev-b", "org/model", "--name", "served model"],
                execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("rev-b", root.GetProperty("requestedRevision").GetString());
            Assert.Equal("/mnt/models/models--org--model/snapshots/rev-b", root.GetProperty("resolvedModelPath").GetString());
            Assert.NotNull(capturedCommand);
            Assert.Contains("requested_revision='rev-b'", capturedCommand!, StringComparison.Ordinal);
            Assert.Contains("ref_file=\"$cache/refs/$requested_revision\"", capturedCommand!, StringComparison.Ordinal);
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
            var command = RemoteCommand(psi);
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
            Assert.Contains("vllm_available", RemoteCommand(captured[0]), StringComparison.Ordinal);
            Assert.Contains("cat > ~/.tau_pods/llama-8b.service <<'EOF'", RemoteCommand(captured[1]), StringComparison.Ordinal);
            Assert.Contains("curl -fsS --max-time 2 \"http://127.0.0.1:$port/health\"", RemoteCommand(captured[2]), StringComparison.Ordinal);
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
            var command = RemoteCommand(psi);
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
    public async Task VllmDeploy_WithRevisionOption_UsesResolvedSnapshot()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-deploy-revision-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var captured = new List<string>();
        var execService = new PodExecService((psi, _) =>
        {
            var command = RemoteCommand(psi);
            captured.Add(command);
            if (IsPreflightCommand(command))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/mnt/models/models--org--model", "rev-b"), ""));
            }

            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started\n", ""));
        });

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                ["vllm", "deploy", "--json", "--no-health", "--config", configPath, "--pod", "ssh-pod", "--revision", "rev-b", "org/model", "--name", "served model"],
                execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            var plan = root.GetProperty("plan");
            var preflight = root.GetProperty("preflight");
            Assert.Equal("rev-b", plan.GetProperty("revision").GetString());
            Assert.Equal("/mnt/models/models--org--model/snapshots/rev-b", plan.GetProperty("modelPath").GetString());
            Assert.Equal("rev-b", preflight.GetProperty("requestedRevision").GetString());
            Assert.Equal("/mnt/models/models--org--model/snapshots/rev-b", preflight.GetProperty("resolvedModelPath").GetString());
            Assert.Contains("vllm serve '/mnt/models/models--org--model/snapshots/rev-b'", plan.GetProperty("serveCommand").GetString(), StringComparison.Ordinal);
            Assert.Equal(2, captured.Count);
            Assert.Contains("requested_revision='rev-b'", captured[0], StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("--revision")]
    [InlineData("--snapshot")]
    public async Task VllmDeploy_WithPrefetchOption_PullsBeforeDeploying(string revisionOption)
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-deploy-prefetch-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var captured = new List<string>();
        var preflightAttempts = 0;
        var execService = new PodExecService((psi, _) =>
        {
            var command = RemoteCommand(psi);
            captured.Add(command);
            if (command.Contains("vllm_available", StringComparison.Ordinal))
            {
                preflightAttempts++;
                if (preflightAttempts == 1)
                {
                    return Task.FromResult(new PodExecService.ProcessExecutionResult(
                        16,
                        "model_cache_path=/mnt/models/models--org--model\nvllm_available=true\nmodel_cache_present=true\nsnapshot_count=1\nfailure_kind=model-snapshot-revision-missing\n",
                        ""));
                }

                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/mnt/models/models--org--model", "rev-b"), ""));
            }

            if (command.Contains("huggingface-cli download", StringComparison.Ordinal))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "downloaded\n", ""));
            }

            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started\n", ""));
        });

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                [
                    "vllm",
                    "deploy",
                    "--json",
                    "--prefetch",
                    "--no-health",
                    "--config",
                    configPath,
                    "--pod",
                    "ssh-pod",
                    revisionOption,
                    "rev-b",
                    "org/model",
                    "--name",
                    "served model"
                ],
                execService);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal("served-model", root.GetProperty("deployment").GetString());
            Assert.Equal(4, captured.Count);

            var prefetch = root.GetProperty("prefetch");
            Assert.True(prefetch.GetProperty("success").GetBoolean());
            Assert.Equal("pull", prefetch.GetProperty("operation").GetString());
            Assert.Equal("org/model", prefetch.GetProperty("modelId").GetString());
            Assert.Equal("rev-b", prefetch.GetProperty("requestedRevision").GetString());
            Assert.Equal("downloaded\n", prefetch.GetProperty("output").GetString());
            Assert.Equal("model-snapshot-revision-missing", prefetch.GetProperty("triggerFailureKind").GetString());

            var preflight = root.GetProperty("preflight");
            Assert.True(preflight.GetProperty("ok").GetBoolean());
            Assert.Equal("rev-b", preflight.GetProperty("requestedRevision").GetString());
            Assert.Equal("/mnt/models/models--org--model/snapshots/rev-b", preflight.GetProperty("resolvedModelPath").GetString());

            var plan = root.GetProperty("plan");
            Assert.Equal("rev-b", plan.GetProperty("revision").GetString());
            Assert.Contains("vllm serve '/mnt/models/models--org--model/snapshots/rev-b'", plan.GetProperty("serveCommand").GetString(), StringComparison.Ordinal);
            Assert.Contains("cat > ~/.tau_pods/served-model.service <<'EOF'", root.GetProperty("remoteCommand").GetString(), StringComparison.Ordinal);
            Assert.Contains("huggingface-cli download 'org/model' --revision 'rev-b' --cache-dir '/mnt/models'", captured[1], StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmDeploy_WithPrefetchTextOutput_PrintsPrefetchContract()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-deploy-prefetch-text-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var preflightAttempts = 0;
        var execService = new PodExecService((psi, _) =>
        {
            var command = RemoteCommand(psi);
            if (command.Contains("vllm_available", StringComparison.Ordinal))
            {
                preflightAttempts++;
                if (preflightAttempts == 1)
                {
                    return Task.FromResult(new PodExecService.ProcessExecutionResult(
                        16,
                        "model_cache_path=/mnt/models/models--org--model\nvllm_available=true\nmodel_cache_present=true\nsnapshot_count=1\nfailure_kind=model-snapshot-revision-missing\n",
                        ""));
                }

                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, PreflightOk("/mnt/models/models--org--model", "rev-b"), ""));
            }

            if (command.Contains("huggingface-cli download", StringComparison.Ordinal))
            {
                return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "downloaded\n", ""));
            }

            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "started\n", ""));
        });

        try
        {
            Console.SetOut(stdout);
            new PodsConfigStore().Save(configPath, ConfigWithSshPod());

            var exitCode = await PodsCli.RunAsync(
                [
                    "vllm",
                    "deploy",
                    "--prefetch",
                    "--no-health",
                    "--config",
                    configPath,
                    "--pod",
                    "ssh-pod",
                    "--revision",
                    "rev-b",
                    "org/model",
                    "--name",
                    "served model"
                ],
                execService);

            Assert.Equal(0, exitCode);
            var output = stdout.ToString();
            Assert.Contains("[prefetch]", output, StringComparison.Ordinal);
            Assert.Contains("revision=rev-b", output, StringComparison.Ordinal);
            Assert.Contains("trigger=model-snapshot-revision-missing", output, StringComparison.Ordinal);
            Assert.Contains("[prefetch-output]", output, StringComparison.Ordinal);
            Assert.Contains("downloaded", output, StringComparison.Ordinal);
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
            var command = RemoteCommand(psi);
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
            Assert.Equal("none", rollback.GetProperty("failureKind").GetString());
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
            var command = RemoteCommand(psi);
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
            var command = RemoteCommand(psi);
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
    public async Task VllmDeploy_WithVllmExtraArgs_PassesArgsToRemoteServeCommand()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-deploy-extra-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var commands = new List<string>();
        var execService = new PodExecService((psi, _) =>
        {
            var command = RemoteCommand(psi);
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
                [
                    "vllm",
                    "deploy",
                    "--no-health",
                    configPath,
                    "ssh-pod",
                    "org/model",
                    "served model",
                    "--vllm",
                    "--tensor-parallel-size",
                    "2"
                ],
                execService);

            Assert.Equal(0, exitCode);
            Assert.Equal(2, commands.Count);
            Assert.Contains("--tensor-parallel-size", commands[1], StringComparison.Ordinal);
            Assert.Contains("vllm serve '/mnt/models/models--org--model/snapshots/main'", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("'--tensor-parallel-size' '2'", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VllmDeploy_WithoutPodOption_UsesActivePodAndPreservesVllmExtraArgs()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-deploy-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var commands = new List<string>();
        var execService = new PodExecService((psi, _) =>
        {
            var command = RemoteCommand(psi);
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
            var config = ConfigWithSshPod();
            config.ActivePodId = "ssh-pod";
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(
                [
                    "vllm",
                    "deploy",
                    "--json",
                    "--no-health",
                    "--config",
                    configPath,
                    "org/model",
                    "--name",
                    "served model",
                    "--vllm",
                    "--tensor-parallel-size",
                    "2"
                ],
                execService);

            Assert.Equal(0, exitCode);
            Assert.Equal(2, commands.Count);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("pod").GetString());
            Assert.Equal("served-model", root.GetProperty("deployment").GetString());
            var plan = root.GetProperty("plan");
            Assert.Equal("org/model", plan.GetProperty("model").GetString());
            Assert.Contains("'--tensor-parallel-size' '2'", plan.GetProperty("serveCommand").GetString(), StringComparison.Ordinal);
            Assert.Contains("--tensor-parallel-size", commands[1], StringComparison.Ordinal);
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

    [Fact]
    public async Task VllmRollback_WithoutPodId_UsesActivePodAndEmitsJsonFailureKind()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-cli-vllm-rollback-active-");
        var configPath = Path.Combine(tempDir.FullName, "custom-pods.json");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        string? capturedCommand = null;
        var execService = new PodExecService((psi, _) =>
        {
            capturedCommand = RemoteCommand(psi);
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "rolled back tau-pod-served-model\n", string.Empty));
        });

        try
        {
            Console.SetOut(stdout);
            var config = ConfigWithSshPod();
            config.ActivePodId = "ssh-pod";
            new PodsConfigStore().Save(configPath, config);

            var exitCode = await PodsCli.RunAsync(["vllm", "rollback", "--json", configPath, "served model"], execService);

            Assert.Equal(0, exitCode);
            Assert.NotNull(capturedCommand);
            Assert.Contains("systemctl --user disable --now 'tau-pod-served-model.service'", capturedCommand!, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(stdout.ToString());
            var root = document.RootElement;
            Assert.Equal("ssh-pod", root.GetProperty("pod").GetString());
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal("rollback", root.GetProperty("operation").GetString());
            Assert.Equal("served-model", root.GetProperty("deployment").GetString());
            Assert.Equal("none", root.GetProperty("failureKind").GetString());
            Assert.Equal("rolled back tau-pod-served-model\n", root.GetProperty("stdout").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            tempDir.Delete(recursive: true);
        }
    }

    private static PodsConfig ConfigWithHttpPod(string id = "http-pod") => new()
    {
        Pods =
        [
            new PodDefinition
            {
                Id = id,
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
