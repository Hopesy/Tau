using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodAgentRunner
{
    private const int ProcessFailureExitCode = -1;
    private const string AgentCommandEnvironmentVariable = "TAU_PODS_AGENT_COMMAND";
    private const string ModelsFileEnvironmentVariable = "TAU_MODELS_FILE";
    private const string ProviderEnvironmentVariable = "TAU_PROVIDER";
    private const string ModelEnvironmentVariable = "TAU_MODEL";

    private readonly Func<ProcessStartInfo, CancellationToken, Task<ProcessExecutionResult>> _executeProcessAsync;

    public PodAgentRunner(Func<ProcessStartInfo, CancellationToken, Task<ProcessExecutionResult>>? executeProcessAsync = null)
    {
        _executeProcessAsync = executeProcessAsync ?? ExecuteProcessAsync;
    }

    public async Task<PodAgentRunResult> RunAsync(
        PodAgentPromptPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var providerId = CreateProviderId(plan.PodId);
        using var modelsConfig = TemporaryModelsConfig.Create(plan, providerId);
        var startInfo = BuildStartInfo(plan, providerId, modelsConfig.Path);
        var command = FormatCommand(startInfo);
        var watch = Stopwatch.StartNew();

        try
        {
            var execution = await _executeProcessAsync(startInfo, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            return ToRunResult(plan, providerId, modelsConfig.Path, startInfo, command, watch.Elapsed, execution);
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return new PodAgentRunResult(
                plan.PodId,
                plan.ModelName,
                providerId,
                plan.UpstreamModel,
                Success: false,
                ProcessFailureExitCode,
                "agent runtime cancelled",
                command,
                startInfo.ArgumentList.ToArray(),
                modelsConfig.Path,
                string.Empty,
                "agent runtime cancelled before completion",
                watch.Elapsed,
                Started: true,
                Cancelled: true);
        }
        catch (Exception ex) when (IsProcessStartException(ex))
        {
            watch.Stop();
            return new PodAgentRunResult(
                plan.PodId,
                plan.ModelName,
                providerId,
                plan.UpstreamModel,
                Success: false,
                ProcessFailureExitCode,
                FormatProcessError("agent process start failed", ex),
                command,
                startInfo.ArgumentList.ToArray(),
                modelsConfig.Path,
                string.Empty,
                FormatProcessError("agent process start failed", ex),
                watch.Elapsed,
                Started: false,
                Cancelled: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            watch.Stop();
            return new PodAgentRunResult(
                plan.PodId,
                plan.ModelName,
                providerId,
                plan.UpstreamModel,
                Success: false,
                ProcessFailureExitCode,
                FormatProcessError("agent process runner failed", ex),
                command,
                startInfo.ArgumentList.ToArray(),
                modelsConfig.Path,
                string.Empty,
                FormatProcessError("agent process runner failed", ex),
                watch.Elapsed,
                Started: false,
                Cancelled: false);
        }
    }

    private static PodAgentRunResult ToRunResult(
        PodAgentPromptPlan plan,
        string providerId,
        string modelsConfigPath,
        ProcessStartInfo startInfo,
        string command,
        TimeSpan duration,
        ProcessExecutionResult execution)
    {
        var success = execution.Started && !execution.Cancelled && execution.ExitCode == 0;
        var summary = success
            ? "agent runtime completed"
            : execution.Cancelled
                ? "agent runtime cancelled"
                : execution.Started
                    ? $"agent runtime failed ({execution.ExitCode.ToString(CultureInfo.InvariantCulture)})"
                    : "agent process start failed";

        return new PodAgentRunResult(
            plan.PodId,
            plan.ModelName,
            providerId,
            plan.UpstreamModel,
            success,
            execution.ExitCode,
            summary,
            command,
            startInfo.ArgumentList.ToArray(),
            modelsConfigPath,
            execution.StdOut,
            execution.StdErr,
            duration,
            execution.Started,
            execution.Cancelled);
    }

    private static ProcessStartInfo BuildStartInfo(
        PodAgentPromptPlan plan,
        string providerId,
        string modelsConfigPath)
    {
        var command = ResolveAgentCommand();
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false,
            WorkingDirectory = Environment.CurrentDirectory
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--provider");
        startInfo.ArgumentList.Add(providerId);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(plan.UpstreamModel);
        startInfo.ArgumentList.Add("--system-prompt");
        startInfo.ArgumentList.Add(plan.SystemPrompt);

        if (ShouldAddPrintMode(plan.UserArgs))
        {
            startInfo.ArgumentList.Add("--print");
        }

        foreach (var argument in plan.UserArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment[ModelsFileEnvironmentVariable] = modelsConfigPath;
        startInfo.Environment[ProviderEnvironmentVariable] = providerId;
        startInfo.Environment[ModelEnvironmentVariable] = plan.UpstreamModel;
        return startInfo;
    }

    private static AgentCommand ResolveAgentCommand()
    {
        var configuredCommand = Environment.GetEnvironmentVariable(AgentCommandEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredCommand))
        {
            return new AgentCommand(configuredCommand.Trim(), []);
        }

        var projectPath = FindCodingAgentProjectPath();
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            return new AgentCommand("dotnet", ["run", "--project", projectPath, "--"]);
        }

        return new AgentCommand(OperatingSystem.IsWindows() ? "Tau.CodingAgent.exe" : "Tau.CodingAgent", []);
    }

    private static string? FindCodingAgentProjectPath()
    {
        foreach (var root in GetSearchRoots())
        {
            var current = new DirectoryInfo(root);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, "src", "Tau.CodingAgent", "Tau.CodingAgent.csproj");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            if (!string.IsNullOrWhiteSpace(root) && seen.Add(root))
            {
                yield return root;
            }
        }
    }

    private static bool ShouldAddPrintMode(IReadOnlyList<string> userArgs)
    {
        if (userArgs.Count == 0 ||
            ContainsOption(userArgs, "--print") ||
            ContainsOption(userArgs, "-p") ||
            ContainsModeRpc(userArgs))
        {
            return false;
        }

        return ContainsPromptText(userArgs);
    }

    private static bool ContainsPromptText(IReadOnlyList<string> args)
    {
        var optionsWithValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--api",
            "--api-key",
            "--append-system-prompt",
            "--base-url",
            "--export",
            "--extension",
            "-e",
            "--fork",
            "--model",
            "--models",
            "--prompt-template",
            "--provider",
            "--session",
            "--session-dir",
            "--skill",
            "--system-prompt",
            "--theme",
            "--thinking",
            "--tools"
        };

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("@", StringComparison.Ordinal) && arg.Length > 1)
            {
                return true;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (!arg.Contains('=', StringComparison.Ordinal) && optionsWithValue.Contains(arg) && i + 1 < args.Count)
                {
                    i++;
                }

                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool ContainsOption(IReadOnlyList<string> args, string option) =>
        args.Any(arg => arg.Equals(option, StringComparison.OrdinalIgnoreCase) ||
                        arg.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsModeRpc(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--mode", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Count &&
                args[i + 1].Equals("rpc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (arg.Equals("--mode=rpc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateProviderId(string podId)
    {
        var normalized = new string(podId
            .Trim()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'
                ? char.ToLowerInvariant(ch)
                : '-')
            .ToArray());
        normalized = normalized.Trim('-', '_', '.');
        return string.IsNullOrWhiteSpace(normalized) ? "pod-local" : $"pod-{normalized}";
    }

    private static async Task<ProcessExecutionResult> ExecuteProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        Process? startedProcess;
        try
        {
            startedProcess = Process.Start(startInfo);
        }
        catch (Exception ex) when (IsProcessStartException(ex))
        {
            return ProcessExecutionResult.StartFailed(FormatProcessError("agent process start failed", ex));
        }

        using var process = startedProcess;
        if (process is null)
        {
            return ProcessExecutionResult.StartFailed("agent process start failed: Process.Start returned null");
        }

        var stdoutTask = startInfo.RedirectStandardOutput
            ? process.StandardOutput.ReadToEndAsync(cancellationToken)
            : Task.FromResult(string.Empty);
        var stderrTask = startInfo.RedirectStandardError
            ? process.StandardError.ReadToEndAsync(cancellationToken)
            : Task.FromResult(string.Empty);

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
            // Best effort; cancellation still returns a structured failure.
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

    private static string FormatCommand(ProcessStartInfo startInfo)
    {
        var parts = new List<string> { startInfo.FileName };
        parts.AddRange(startInfo.ArgumentList);
        return string.Join(' ', parts.Select(QuoteArgumentForDisplay));
    }

    private static string QuoteArgumentForDisplay(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"', StringComparison.Ordinal))
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).ReplaceLineEndings("\\n") + "\"";
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
            new(ProcessFailureExitCode, string.Empty, "agent runtime cancelled before completion", Cancelled: true);
    }

    private sealed record AgentCommand(string FileName, IReadOnlyList<string> Arguments);

    private sealed class TemporaryModelsConfig : IDisposable
    {
        private TemporaryModelsConfig(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryModelsConfig Create(PodAgentPromptPlan plan, string providerId)
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tau-pods-agent");
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, $"models-{Guid.NewGuid():N}.json");
            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            WriteModelsConfig(writer, plan, providerId);
            return new TemporaryModelsConfig(path);
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the file contains no literal PI_API_KEY value.
            }
        }

        private static void WriteModelsConfig(Utf8JsonWriter writer, PodAgentPromptPlan plan, string providerId)
        {
            var api = plan.Api.Equals("responses", StringComparison.OrdinalIgnoreCase)
                ? "openai-responses"
                : "openai-compatible";
            var apiKey = plan.ApiKeySource.Equals("PI_API_KEY", StringComparison.OrdinalIgnoreCase)
                ? "PI_API_KEY"
                : "dummy";

            writer.WriteStartObject();
            writer.WriteStartObject("providers");
            writer.WriteStartObject(providerId);
            writer.WriteString("baseUrl", plan.BaseUrl);
            writer.WriteString("api", api);
            writer.WriteString("apiKind", "openai-compatible");
            writer.WriteString("apiKey", apiKey);
            writer.WriteBoolean("authHeader", true);
            writer.WriteStartArray("models");
            writer.WriteStartObject();
            writer.WriteString("id", plan.UpstreamModel);
            writer.WriteString("name", plan.ModelName);
            writer.WriteString("baseUrl", plan.BaseUrl);
            writer.WriteString("api", api);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
    }
}
