using System.Diagnostics;
using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Tests;

public class PodSetupServiceTests
{
    [Fact]
    public async Task RunAsync_ExecutesSshScpSetupAndGpuDetectionWithoutExposingSecrets()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-setup-service-");
        var scriptPath = Path.Combine(tempDir.FullName, "pod_setup.sh");
        await File.WriteAllTextAsync(scriptPath, "#!/usr/bin/env bash\n");
        var calls = new List<(string FileName, string[] Arguments, string? StandardInput)>();
        var service = new PodSetupService(
            new PodSetupPlanner(Env),
            (psi, stdin, _) =>
            {
                calls.Add((psi.FileName, psi.ArgumentList.ToArray(), stdin));
                if (psi.FileName.Equals("ssh", StringComparison.OrdinalIgnoreCase) &&
                    psi.ArgumentList[^1].Contains("nvidia-smi", StringComparison.Ordinal))
                {
                    return Task.FromResult(new PodSetupService.ProcessExecutionResult(
                        0,
                        "0, NVIDIA RTX 6000 Ada, 49140 MiB\n1, NVIDIA RTX 6000 Ada, 49140 MiB\n",
                        string.Empty));
                }

                if (psi.FileName.Equals("ssh", StringComparison.OrdinalIgnoreCase) &&
                    psi.ArgumentList[^1].Contains("pod_setup.sh", StringComparison.Ordinal))
                {
                    Assert.Equal("hf_real_secret\npi_real_secret\n", stdin);
                    return Task.FromResult(new PodSetupService.ProcessExecutionResult(
                        0,
                        "setup completed hf_real_secret pi_real_secret\n",
                        string.Empty));
                }

                return Task.FromResult(new PodSetupService.ProcessExecutionResult(0, "ok\n", string.Empty));
            },
            Env);

        try
        {
            var result = await service.RunAsync(
                SshPod(),
                new PodSetupRunOptions(
                    MountCommand: "mount -t nfs store:/models /mnt/models",
                    ModelsPath: "/data/hf-cache",
                    VllmVersion: "gpt-oss",
                    ScriptPath: scriptPath));

            Assert.True(result.Success);
            Assert.Equal("ssh-pod", result.PodId);
            Assert.Equal("gpt-oss", result.Plan.VllmVersion);
            Assert.Equal("/data/hf-cache", result.Plan.ModelsPath);
            Assert.Equal(scriptPath, result.ScriptPath);
            Assert.Equal(4, result.Steps.Count);
            Assert.Equal(["ssh-test", "scp-script", "run-setup", "detect-gpus"], result.Steps.Select(step => step.Name));
            Assert.All(result.Steps, step => Assert.True(step.Success));
            Assert.Equal(2, result.Gpus.Count);
            Assert.Equal("NVIDIA RTX 6000 Ada", result.Gpus[0].Name);
            Assert.Equal("49140 MiB", result.Gpus[0].Memory);

            Assert.Equal("ssh", calls[0].FileName);
            Assert.Equal("echo 'SSH OK'", calls[0].Arguments[^1]);
            Assert.Equal("scp", calls[1].FileName);
            Assert.Equal("-P", calls[1].Arguments[0]);
            Assert.Equal("2222", calls[1].Arguments[1]);
            Assert.Equal(scriptPath, calls[1].Arguments[2]);
            Assert.Equal("pods.example.internal:/tmp/pod_setup.sh", calls[1].Arguments[3]);
            Assert.Equal("ssh", calls[2].FileName);
            Assert.Contains("--hf-token \"$TAU_HF_TOKEN\"", calls[2].Arguments[^1], StringComparison.Ordinal);
            Assert.Contains("--vllm-api-key \"$TAU_PI_API_KEY\"", calls[2].Arguments[^1], StringComparison.Ordinal);
            Assert.DoesNotContain("hf_real_secret", calls[2].Arguments[^1], StringComparison.Ordinal);
            Assert.DoesNotContain("pi_real_secret", calls[2].Arguments[^1], StringComparison.Ordinal);
            Assert.Contains("--hf-token \"$HF_TOKEN\"", result.Steps[2].DisplayCommand, StringComparison.Ordinal);
            Assert.DoesNotContain("hf_real_secret", result.Steps[2].DisplayCommand, StringComparison.Ordinal);
            Assert.DoesNotContain("pi_real_secret", result.Steps[2].DisplayCommand, StringComparison.Ordinal);
            Assert.DoesNotContain("hf_real_secret", result.Steps[2].StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("pi_real_secret", result.Steps[2].StdOut, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", result.Steps[2].StdOut, StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }

        static string? Env(string name) => name switch
        {
            "HF_TOKEN" => "hf_real_secret",
            "PI_API_KEY" => "pi_real_secret",
            _ => null
        };
    }

    [Fact]
    public async Task RunAsync_StopsBeforeSshWhenTokenIsMissing()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-setup-service-missing-token-");
        var scriptPath = Path.Combine(tempDir.FullName, "pod_setup.sh");
        await File.WriteAllTextAsync(scriptPath, "#!/usr/bin/env bash\n");
        var processStarted = false;
        var service = new PodSetupService(
            new PodSetupPlanner(_ => null),
            (_, _, _) =>
            {
                processStarted = true;
                return Task.FromResult(new PodSetupService.ProcessExecutionResult(0, string.Empty, string.Empty));
            },
            _ => null);

        try
        {
            var result = await service.RunAsync(SshPod(), new PodSetupRunOptions(ScriptPath: scriptPath));

            Assert.False(result.Success);
            Assert.Equal("HF_TOKEN environment variable is required.", result.Summary);
            Assert.Empty(result.Steps);
            Assert.False(processStarted);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_StopsWhenSshConnectionFails()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-pods-setup-service-ssh-fail-");
        var scriptPath = Path.Combine(tempDir.FullName, "pod_setup.sh");
        await File.WriteAllTextAsync(scriptPath, "#!/usr/bin/env bash\n");
        var service = new PodSetupService(
            new PodSetupPlanner(Env),
            (_, _, _) => Task.FromResult(new PodSetupService.ProcessExecutionResult(255, string.Empty, "permission denied\n")),
            Env);

        try
        {
            var result = await service.RunAsync(SshPod(), new PodSetupRunOptions(ScriptPath: scriptPath));

            Assert.False(result.Success);
            Assert.Equal("SSH connection test failed.", result.Summary);
            Assert.Single(result.Steps);
            Assert.Equal("ssh-test", result.Steps[0].Name);
            Assert.Equal(255, result.Steps[0].ExitCode);
            Assert.Contains("permission denied", result.Steps[0].StdErr, StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }

        static string? Env(string name) => name switch
        {
            "HF_TOKEN" => "hf",
            "PI_API_KEY" => "pi",
            _ => null
        };
    }

    private static PodDefinition SshPod() => new()
    {
        Id = "ssh-pod",
        Provider = "ssh",
        Model = "llama",
        Region = "lab",
        SshHost = "pods.example.internal",
        SshPort = 2222,
        ModelsPath = "/mnt/models"
    };
}
