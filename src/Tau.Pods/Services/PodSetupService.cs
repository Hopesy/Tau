using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodSetupService
{
    private const int ProcessFailureExitCode = -1;

    private readonly PodSetupPlanner planner;
    private readonly Func<ProcessStartInfo, string?, CancellationToken, Task<ProcessExecutionResult>> executeProcessAsync;
    private readonly Func<string, string?> getEnvironmentVariable;

    public PodSetupService(
        PodSetupPlanner? planner = null,
        Func<ProcessStartInfo, string?, CancellationToken, Task<ProcessExecutionResult>>? executeProcessAsync = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        this.getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        this.planner = planner ?? new PodSetupPlanner(this.getEnvironmentVariable);
        this.executeProcessAsync = executeProcessAsync ?? ExecuteProcessAsync;
    }

    public async Task<PodSetupRunResult> RunAsync(
        PodDefinition pod,
        PodSetupRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pod);
        ArgumentNullException.ThrowIfNull(options);

        PodSetupPlan plan;
        try
        {
            plan = planner.Plan(pod, new PodSetupPlanOptions(options.MountCommand, options.ModelsPath, options.VllmVersion));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return FailureWithoutSteps(pod.Id, ex.Message);
        }

        var scriptPath = ResolveScriptPath(options.ScriptPath);
        if (scriptPath is null)
        {
            return new PodSetupRunResult(
                pod.Id,
                false,
                "Setup script not found. Pass --script <path> or set TAU_PODS_SETUP_SCRIPT.",
                plan,
                null,
                [],
                []);
        }

        var hfToken = getEnvironmentVariable("HF_TOKEN");
        if (string.IsNullOrWhiteSpace(hfToken))
        {
            return new PodSetupRunResult(
                pod.Id,
                false,
                "HF_TOKEN environment variable is required.",
                plan,
                scriptPath,
                [],
                []);
        }

        var piApiKey = getEnvironmentVariable("PI_API_KEY");
        if (string.IsNullOrWhiteSpace(piApiKey))
        {
            return new PodSetupRunResult(
                pod.Id,
                false,
                "PI_API_KEY environment variable is required.",
                plan,
                scriptPath,
                [],
                []);
        }

        var steps = new List<PodSetupExecutionStep>();

        var sshTest = await RunStepAsync(
            "ssh-test",
            BuildSshProcessStartInfo(plan, "echo 'SSH OK'"),
            null,
            "ssh echo 'SSH OK'",
            cancellationToken).ConfigureAwait(false);
        steps.Add(sshTest);
        if (!sshTest.Success)
        {
            return BuildFailureResult(pod.Id, plan, scriptPath, steps, "SSH connection test failed.");
        }

        var copyScript = await RunStepAsync(
            "scp-script",
            BuildScpProcessStartInfo(plan, scriptPath),
            null,
            $"scp {scriptPath} {plan.SshHost}:{plan.ScriptRemotePath}",
            cancellationToken).ConfigureAwait(false);
        steps.Add(copyScript);
        if (!copyScript.Success)
        {
            return BuildFailureResult(pod.Id, plan, scriptPath, steps, "Setup script copy failed.");
        }

        var setupCommand = BuildSensitiveSetupCommand(plan);
        var runSetup = await RunStepAsync(
            "run-setup",
            BuildSshProcessStartInfo(plan, setupCommand),
            BuildSetupStdin(hfToken, piApiKey),
            plan.SetupCommand,
            cancellationToken).ConfigureAwait(false);
        steps.Add(runSetup);
        if (!runSetup.Success)
        {
            return BuildFailureResult(pod.Id, plan, scriptPath, steps, "Remote setup script failed.");
        }

        var detectGpus = await RunStepAsync(
            "detect-gpus",
            BuildSshProcessStartInfo(plan, "nvidia-smi --query-gpu=index,name,memory.total --format=csv,noheader"),
            null,
            "ssh nvidia-smi --query-gpu=index,name,memory.total --format=csv,noheader",
            cancellationToken).ConfigureAwait(false);
        steps.Add(detectGpus);

        var gpus = detectGpus.Success ? ParseGpuInfo(detectGpus.StdOut) : [];
        return new PodSetupRunResult(
            pod.Id,
            true,
            $"Setup completed for '{pod.Id}' with {gpus.Count} GPU(s) detected.",
            plan,
            scriptPath,
            steps,
            gpus);
    }

    private static PodSetupRunResult FailureWithoutSteps(string podId, string summary) =>
        new(
            podId,
            false,
            summary,
            new PodSetupPlan(
                podId,
                string.Empty,
                22,
                PodSetupPlanner.DefaultModelsPath,
                null,
                PodSetupPlanner.DefaultVllmVersion,
                false,
                false,
                PodSetupPlanner.ScriptRemotePath,
                string.Empty,
                []),
            null,
            [],
            []);

    private static PodSetupRunResult BuildFailureResult(
        string podId,
        PodSetupPlan plan,
        string scriptPath,
        IReadOnlyList<PodSetupExecutionStep> steps,
        string summary) =>
        new(podId, false, summary, plan, scriptPath, steps, []);

    private async Task<PodSetupExecutionStep> RunStepAsync(
        string name,
        ProcessStartInfo startInfo,
        string? standardInput,
        string displayCommand,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await executeProcessAsync(startInfo, standardInput, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            var success = result.Started && !result.Cancelled && result.ExitCode == 0;
            return new PodSetupExecutionStep(
                name,
                success,
                BuildStepSummary(name, result, success),
                displayCommand,
                result.ExitCode,
                RedactSensitiveValues(result.StdOut),
                RedactSensitiveValues(result.StdErr),
                watch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return new PodSetupExecutionStep(
                name,
                false,
                $"{name} cancelled",
                displayCommand,
                ProcessFailureExitCode,
                string.Empty,
                $"{name} cancelled before completion",
                watch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            watch.Stop();
            return new PodSetupExecutionStep(
                name,
                false,
                $"{name} runner failed",
                displayCommand,
                ProcessFailureExitCode,
                string.Empty,
                FormatProcessError($"{name} runner failed", ex),
                watch.Elapsed);
        }
    }

    private static ProcessStartInfo BuildSshProcessStartInfo(PodSetupPlan plan, string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(plan.SshPort.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ConnectTimeout=5");
        psi.ArgumentList.Add(plan.SshHost);
        psi.ArgumentList.Add(command);
        return psi;
    }

    private static ProcessStartInfo BuildScpProcessStartInfo(PodSetupPlan plan, string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "scp",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-P");
        psi.ArgumentList.Add(plan.SshPort.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add($"{plan.SshHost}:{plan.ScriptRemotePath}");
        return psi;
    }

    private static string BuildSensitiveSetupCommand(PodSetupPlan plan) =>
        "read -r TAU_HF_TOKEN; " +
        "read -r TAU_PI_API_KEY; " +
        plan.SetupCommand
            .Replace("\"$HF_TOKEN\"", "\"$TAU_HF_TOKEN\"", StringComparison.Ordinal)
            .Replace("\"$PI_API_KEY\"", "\"$TAU_PI_API_KEY\"", StringComparison.Ordinal);

    private static string BuildSetupStdin(string hfToken, string piApiKey) =>
        hfToken.ReplaceLineEndings(string.Empty) + "\n" +
        piApiKey.ReplaceLineEndings(string.Empty) + "\n";

    private static async Task<ProcessExecutionResult> ExecuteProcessAsync(
        ProcessStartInfo startInfo,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        Process? startedProcess;
        try
        {
            startedProcess = Process.Start(startInfo);
        }
        catch (Exception ex) when (IsProcessStartException(ex))
        {
            return ProcessExecutionResult.StartFailed(FormatProcessError("process start failed", ex));
        }

        using var process = startedProcess;
        if (process is null)
        {
            return ProcessExecutionResult.StartFailed("process start failed: Process.Start returned null");
        }

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            return ProcessExecutionResult.CancelledFailure();
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessExecutionResult(process.ExitCode, stdout, stderr);
    }

    private static string? ResolveScriptPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        var envPath = Environment.GetEnvironmentVariable("TAU_PODS_SETUP_SCRIPT");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return Path.GetFullPath(envPath);
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "pod_setup.sh"),
            Path.Combine(AppContext.BaseDirectory, "scripts", "pod_setup.sh"),
            Path.Combine(Environment.CurrentDirectory, "pod_setup.sh"),
            Path.Combine(Environment.CurrentDirectory, "scripts", "pod_setup.sh"),
            Path.Combine(Environment.CurrentDirectory, "..", "pi-mono-main", "packages", "pods", "scripts", "pod_setup.sh")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private string RedactSensitiveValues(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = value;
        foreach (var variableName in new[] { "HF_TOKEN", "PI_API_KEY" })
        {
            var secret = getEnvironmentVariable(variableName);
            if (!string.IsNullOrEmpty(secret))
            {
                redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }
        }

        return redacted;
    }

    private static IReadOnlyList<PodGpuInfo> ParseGpuInfo(string stdout)
    {
        var gpus = new List<PodGpuInfo>();
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',', 3).Select(static part => part.Trim()).ToArray();
            if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            var name = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : "Unknown";
            var memory = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : "Unknown";
            gpus.Add(new PodGpuInfo(id, name, memory));
        }

        return gpus;
    }

    private static string BuildStepSummary(string name, ProcessExecutionResult result, bool success)
    {
        if (result.Cancelled)
        {
            return $"{name} cancelled";
        }

        if (!result.Started)
        {
            return $"{name} process start failed";
        }

        return success ? $"{name} ok" : $"{name} failed ({result.ExitCode})";
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
        }
    }

    private static bool IsProcessStartException(Exception ex) =>
        ex is Win32Exception or InvalidOperationException or DirectoryNotFoundException or UnauthorizedAccessException;

    private static string FormatProcessError(string prefix, Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        return string.IsNullOrWhiteSpace(message)
            ? $"{prefix}: {ex.GetType().Name}"
            : $"{prefix}: {ex.GetType().Name}: {message}";
    }

    public sealed record ProcessExecutionResult(
        int ExitCode,
        string StdOut,
        string StdErr,
        bool Started = true,
        bool Cancelled = false)
    {
        public static ProcessExecutionResult StartFailed(string stderr) =>
            new(ProcessFailureExitCode, string.Empty, stderr, Started: false);

        public static ProcessExecutionResult CancelledFailure() =>
            new(ProcessFailureExitCode, string.Empty, "process cancelled before completion", Cancelled: true);
    }
}
