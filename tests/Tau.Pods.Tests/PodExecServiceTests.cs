using System.ComponentModel;
using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Tests;

public class PodExecServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WithEndpointPod_ReturnsUnsupported()
    {
        var processStarted = false;
        var service = new PodExecService((_, _) =>
        {
            processStarted = true;
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, string.Empty, string.Empty));
        });
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
        Assert.Equal(PodExecFailureKinds.UnsupportedTransport, result.FailureKind);
        Assert.Contains("do not support remote exec yet", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(processStarted);
    }

    [Fact]
    public async Task ExecuteAsync_WithSshPod_UsesArgumentListInOrder()
    {
        var service = new PodExecService((psi, _) =>
        {
            Assert.Equal("ssh", psi.FileName, ignoreCase: true);
            Assert.Equal(string.Empty, psi.Arguments);
            Assert.Equal(
                new[]
                {
                    "-p",
                    "2222",
                    "-o",
                    "BatchMode=yes",
                    "-o",
                    "ConnectTimeout=5",
                    "pods.example.internal",
                    "echo hello"
                },
                psi.ArgumentList);
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "hello\n", string.Empty));
        });

        var pod = new PodDefinition
        {
            Id = "ssh-pod",
            Provider = "ssh",
            Model = "deepseek-r1",
            Region = "lab",
            SshHost = "pods.example.internal",
            SshPort = 2222
        };

        var result = await service.ExecuteAsync(pod, "echo hello");

        Assert.True(result.Success);
        Assert.Equal("ssh", result.Transport);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello\n", result.StdOut);
        Assert.Equal(PodExecFailureKinds.None, result.FailureKind);
        Assert.Contains("ssh exec ok", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WithSshPod_ReturnsFailureOnNonZeroExit()
    {
        var service = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(255, string.Empty, "permission denied\n")));

        var result = await service.ExecuteAsync(SshPod(), "whoami");

        Assert.False(result.Success);
        Assert.Equal("ssh", result.Transport);
        Assert.Equal(255, result.ExitCode);
        Assert.Equal("permission denied\n", result.StdErr);
        Assert.Equal(PodExecFailureKinds.SshAuthFailed, result.FailureKind);
        Assert.Contains("ssh exec failed (255)", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WithSshPod_ClassifiesGenericRemoteCommandFailure()
    {
        var service = new PodExecService((_, _) =>
            Task.FromResult(new PodExecService.ProcessExecutionResult(42, string.Empty, "systemd failed\n")));

        var result = await service.ExecuteAsync(SshPod(), "systemctl --user start tau-pod.service");

        Assert.False(result.Success);
        Assert.Equal(42, result.ExitCode);
        Assert.Equal(PodExecFailureKinds.SshExecFailed, result.FailureKind);
        Assert.Contains("ssh exec failed (42)", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WithSshPod_ReturnsStructuredFailureWhenProcessStartThrows()
    {
        var service = new PodExecService((_, _) =>
            throw new Win32Exception(2, "ssh executable not found"));

        var result = await service.ExecuteAsync(SshPod(), "uptime");

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("ssh process start failed", result.Summary);
        Assert.Equal(PodExecFailureKinds.SshProcessStartFailed, result.FailureKind);
        Assert.Contains("Win32Exception", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("ssh executable not found", result.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("uptime", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WithSshPod_ReturnsStructuredFailureWhenProcessRunnerThrows()
    {
        var service = new PodExecService((_, _) =>
            throw new IOException("pipe closed unexpectedly"));

        var result = await service.ExecuteAsync(SshPod(), "uptime");

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("ssh process runner failed", result.Summary);
        Assert.Equal(PodExecFailureKinds.SshProcessRunnerFailed, result.FailureKind);
        Assert.Contains("IOException", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("pipe closed unexpectedly", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WithSshPod_ReturnsStructuredFailureWhenRunnerReportsStartFailure()
    {
        var service = new PodExecService((_, _) =>
            Task.FromResult(PodExecService.ProcessExecutionResult.StartFailed("ssh process start failed: Win32Exception: not found")));

        var result = await service.ExecuteAsync(SshPod(), "uptime");

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("ssh process start failed", result.Summary);
        Assert.Equal(PodExecFailureKinds.SshProcessStartFailed, result.FailureKind);
        Assert.Contains("Win32Exception", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WithSshPod_ReturnsStructuredFailureWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new PodExecService((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "ok\n", string.Empty));
        });

        var result = await service.ExecuteAsync(SshPod(), "uptime", cts.Token);

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("ssh exec cancelled", result.Summary);
        Assert.Equal(PodExecFailureKinds.SshExecCancelled, result.FailureKind);
        Assert.Contains("cancelled", result.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WithSshPod_PassesComplexCommandAsSingleRemoteArgument()
    {
        const string command = "printf \"%s\" \"hello world\"; echo done";
        var service = new PodExecService((psi, _) =>
        {
            Assert.Equal(8, psi.ArgumentList.Count);
            Assert.Equal(command, psi.ArgumentList[7]);
            Assert.Contains("\"hello world\"", psi.ArgumentList[7], StringComparison.Ordinal);
            Assert.Contains("; echo done", psi.ArgumentList[7], StringComparison.Ordinal);
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, "done\n", string.Empty));
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

        var result = await service.ExecuteAsync(pod, command);

        Assert.True(result.Success);
        Assert.Equal(command, result.Command);
        Assert.Equal("pods.example.internal:22", result.Target);
    }

    [Fact]
    public async Task OpenCommandAsync_WithSshPod_InheritsStdIoAndPassesRemoteCommand()
    {
        const string command = "tail -f ~/.vllm_logs/served.log";
        var service = new PodExecService((psi, _) =>
        {
            Assert.Equal("ssh", psi.FileName, ignoreCase: true);
            Assert.Equal(
                new[]
                {
                    "-p",
                    "2222",
                    "pods.example.internal",
                    command
                },
                psi.ArgumentList);
            Assert.False(psi.RedirectStandardOutput);
            Assert.False(psi.RedirectStandardError);
            Assert.False(psi.CreateNoWindow);
            Assert.Equal("1", psi.Environment["FORCE_COLOR"]);
            return Task.FromResult(new PodExecService.ProcessExecutionResult(0, string.Empty, string.Empty));
        });

        var result = await service.OpenCommandAsync(SshPod(), command, keepAlive: true);

        Assert.True(result.Success);
        Assert.Equal(command, result.Command);
        Assert.Equal("ssh command stream closed", result.Summary);
        Assert.Equal(PodExecFailureKinds.None, result.FailureKind);
    }

    private static PodDefinition SshPod() => new()
    {
        Id = "ssh-pod",
        Provider = "ssh",
        Model = "deepseek-r1",
        Region = "lab",
        SshHost = "pods.example.internal",
        SshPort = 2222
    };
}
