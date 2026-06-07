using System.Text;
using System.Text.Json;
using Tau.Ai.Observability;
using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodVllmOrchestrationService
{
    private const int DefaultHealthAttempts = 1;
    private const int DefaultHealthBackoffMilliseconds = 0;

    private readonly PodExecService _execService;
    private readonly PodVllmCommandPlanner _planner;
    private readonly PodModelService _modelService;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly ITauLogSink _logSink;

    public PodVllmOrchestrationService(
        PodExecService? execService = null,
        PodVllmCommandPlanner? planner = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        ITauLogSink? logSink = null,
        PodModelService? modelService = null)
    {
        _execService = execService ?? new PodExecService();
        _planner = planner ?? new PodVllmCommandPlanner();
        _delayAsync = delayAsync ?? Task.Delay;
        _logSink = logSink ?? NullTauLogSink.Instance;
        _modelService = modelService ?? new PodModelService(_execService, _logSink);
    }

    public async Task<PodVllmOperationResult> DeployAsync(
        PodDefinition pod,
        PodVllmServeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pod);
        ArgumentNullException.ThrowIfNull(options);

        var deploymentName = NormalizeDeploymentName(options.DeploymentName ?? options.ModelId);
        var modelId = options.ModelId.Trim();
        var revision = NormalizeRevision(options.Revision);
        LogVllmStart(
            "deploy",
            pod.Id,
            deploymentName,
            modelId,
            new Dictionary<string, string?>
            {
                ["waitForHealth"] = BoolString(options.WaitForHealth),
                ["healthAttempts"] = Invariant(NormalizeHealthAttempts(options.HealthAttempts)),
                ["healthBackoffMs"] = Invariant(NormalizeHealthBackoff(options.HealthBackoffMilliseconds)),
                ["requestedRevision"] = revision,
                ["prefetchRequested"] = BoolString(options.Prefetch)
            });

        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var result = new PodVllmOperationResult(
                pod.Id,
                false,
                "deploy",
                deploymentName,
                "vLLM deploy requires SSH-based pod.",
                string.Empty,
                -1,
                string.Empty,
                string.Empty);
            LogVllmEnd(
                "deploy",
                result.PodId,
                result.DeploymentName,
                result.Success,
                result.ExitCode,
                result.Summary,
                modelId,
                new Dictionary<string, string?>
                {
                    ["failureKind"] = "unsupported-transport",
                    ["hasRollback"] = "false"
                });
            return result;
        }

        var preflight = await PreflightAsync(pod, options, cancellationToken).ConfigureAwait(false);
        PodModelOperationResult? prefetch = null;
        string? prefetchTriggerFailureKind = null;
        if (!preflight.Success)
        {
            if (options.Prefetch && IsPrefetchableFailureKind(preflight.FailureKind))
            {
                prefetchTriggerFailureKind = preflight.FailureKind;
                prefetch = await _modelService.PullAsync(pod, modelId, revision, cancellationToken).ConfigureAwait(false);
                if (prefetch.Success)
                {
                    preflight = await PreflightAsync(pod, options, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var result = new PodVllmOperationResult(
                        pod.Id,
                        false,
                        "deploy",
                        preflight.DeploymentName,
                        $"vLLM deploy prefetch failed ({preflight.FailureKind}): {preflight.Summary}; {prefetch.Summary}",
                        preflight.Command,
                        preflight.ExitCode,
                        preflight.StdOut,
                        preflight.StdErr,
                        Preflight: preflight,
                        Prefetch: prefetch,
                        PrefetchTriggerFailureKind: prefetchTriggerFailureKind,
                        FailureKind: preflight.FailureKind);
                    LogVllmEnd(
                        "deploy",
                        result.PodId,
                        result.DeploymentName,
                        result.Success,
                        result.ExitCode,
                        result.Summary,
                        modelId,
                        new Dictionary<string, string?>
                        {
                            ["failureKind"] = preflight.FailureKind,
                            ["hasRollback"] = "false",
                            ["prefetchRequested"] = BoolString(options.Prefetch),
                            ["prefetchAttempted"] = "true",
                            ["prefetchSuccess"] = "false",
                            ["preflightSuccess"] = "false"
                        });
                    return result;
                }
            }

            if (!preflight.Success)
            {
                var result = new PodVllmOperationResult(
                    pod.Id,
                    false,
                    "deploy",
                    preflight.DeploymentName,
                    options.Prefetch && prefetch is not null
                        ? $"vLLM deploy preflight failed after prefetch ({preflight.FailureKind}): {preflight.Summary}"
                        : $"vLLM deploy preflight failed ({preflight.FailureKind}): {preflight.Summary}",
                    preflight.Command,
                    preflight.ExitCode,
                    preflight.StdOut,
                    preflight.StdErr,
                    Preflight: preflight,
                    Prefetch: prefetch,
                    PrefetchTriggerFailureKind: prefetchTriggerFailureKind,
                    FailureKind: preflight.FailureKind);
                LogVllmEnd(
                    "deploy",
                    result.PodId,
                    result.DeploymentName,
                    result.Success,
                    result.ExitCode,
                    result.Summary,
                    modelId,
                    new Dictionary<string, string?>
                    {
                        ["failureKind"] = preflight.FailureKind,
                        ["hasRollback"] = "false",
                        ["prefetchRequested"] = BoolString(options.Prefetch),
                        ["prefetchAttempted"] = BoolString(prefetch is not null),
                        ["prefetchSuccess"] = prefetch is null ? null : BoolString(prefetch.Success),
                        ["preflightSuccess"] = "false"
                    });
                return result;
            }
        }

        var plan = _planner.PlanServe(pod, options with { ResolvedModelPath = preflight.ResolvedModelPath });
        var command = BuildDeployCommand(plan);
        var exec = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        PodVllmHealthResult? health = null;
        PodVllmRollbackResult? rollback = null;
        var success = exec.Success;
        var summary = exec.Success
            ? $"vLLM deployment '{plan.DeploymentName}' started on {pod.Id}."
            : $"vLLM deploy failed: {exec.Summary}";

        if (exec.Success && options.WaitForHealth)
        {
            health = await HealthAsync(
                pod,
                plan.DeploymentName,
                NormalizeHealthAttempts(options.HealthAttempts),
                NormalizeHealthBackoff(options.HealthBackoffMilliseconds),
                cancellationToken).ConfigureAwait(false);
            if (health.Ready)
            {
                summary = $"vLLM deployment '{plan.DeploymentName}' started and is ready on {pod.Id} after {health.Attempts} health attempt(s).";
            }
            else
            {
                rollback = await RollbackAsync(pod, plan.DeploymentName, cancellationToken).ConfigureAwait(false);
                success = false;
                summary = $"vLLM deploy health check failed ({health.FailureKind}): {health.Summary}; rollback {(rollback.Success ? "completed" : "failed")}.";
            }
        }
        else if (!exec.Success)
        {
            rollback = await RollbackAsync(pod, plan.DeploymentName, cancellationToken).ConfigureAwait(false);
        }

        var failureKind = GetDeployFailureKind(success, exec, health);
        var finalResult = new PodVllmOperationResult(
            pod.Id,
            success,
            "deploy",
            plan.DeploymentName,
            summary,
            command,
            exec.ExitCode,
            exec.StdOut,
            exec.StdErr,
            plan,
            health,
            rollback,
            preflight,
            prefetch,
            prefetchTriggerFailureKind,
            ProcessId: ExtractProcessId(exec.StdOut),
            FailureKind: failureKind);
        LogVllmEnd(
            "deploy",
            finalResult.PodId,
            finalResult.DeploymentName,
            finalResult.Success,
            finalResult.ExitCode,
            finalResult.Summary,
            modelId,
            new Dictionary<string, string?>
            {
                ["failureKind"] = failureKind,
                ["hasRollback"] = BoolString(rollback is not null),
                ["prefetchRequested"] = BoolString(options.Prefetch),
                ["prefetchAttempted"] = BoolString(finalResult.Prefetch is not null),
                ["prefetchSuccess"] = finalResult.Prefetch is null ? null : BoolString(finalResult.Prefetch.Success),
                ["healthReady"] = health is null ? null : BoolString(health.Ready),
                ["healthAttempts"] = health is null ? null : Invariant(health.Attempts),
                ["maxHealthAttempts"] = health is null ? null : Invariant(health.MaxAttempts)
            });
        return finalResult;
    }

    public async Task<PodVllmPreflightResult> PreflightAsync(
        PodDefinition pod,
        PodVllmServeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pod);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelId);

        var deploymentName = NormalizeDeploymentName(options.DeploymentName ?? options.ModelId);
        var modelId = options.ModelId.Trim();
        var modelCachePath = _planner.BuildModelCachePath(pod, modelId);
        var revision = NormalizeRevision(options.Revision);
        LogVllmStart(
            "preflight",
            pod.Id,
            deploymentName,
            modelId,
            new Dictionary<string, string?>
            {
                ["requestedRevision"] = revision
            });
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var result = new PodVllmPreflightResult(
                pod.Id,
                false,
                deploymentName,
                modelId,
                modelCachePath,
                null,
                false,
                0,
                false,
                "unsupported-transport",
                "vLLM preflight requires SSH-based pod.",
                string.Empty,
                -1,
                string.Empty,
                string.Empty,
                revision);
            LogPreflightEnd(result);
            return result;
        }

        var command = BuildPreflightCommand(modelCachePath, revision);
        var exec = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        var values = ParseKeyValueOutput(exec.StdOut);
        var failureKind = GetValue(values, "failure_kind");
        var resolvedModelPath = GetValue(values, "resolved_model_path");
        var cachePresent = GetBool(values, "model_cache_present");
        var snapshotCount = GetInt(values, "snapshot_count");
        var vllmAvailable = GetBool(values, "vllm_available");

        if (!exec.Success && string.IsNullOrWhiteSpace(failureKind))
        {
            failureKind = PodExecFailureKinds.FromResult(exec);
        }

        var success = exec.Success &&
            cachePresent &&
            snapshotCount > 0 &&
            vllmAvailable &&
            !string.IsNullOrWhiteSpace(resolvedModelPath);
        if (success)
        {
            failureKind = "none";
        }
        else if (string.IsNullOrWhiteSpace(failureKind) || failureKind == "none")
        {
            failureKind = "invalid-preflight-output";
        }

        var finalResult = new PodVllmPreflightResult(
            pod.Id,
            success,
            deploymentName,
            modelId,
            GetValue(values, "model_cache_path") ?? modelCachePath,
            string.IsNullOrWhiteSpace(resolvedModelPath) ? null : resolvedModelPath,
            cachePresent,
            snapshotCount,
            vllmAvailable,
            failureKind,
            BuildPreflightSummary(pod.Id, deploymentName, modelId, revision, resolvedModelPath, failureKind, snapshotCount),
            command,
            exec.ExitCode,
            exec.StdOut,
            exec.StdErr,
            revision);
        LogPreflightEnd(finalResult);
        return finalResult;
    }

    public async Task<PodVllmStatusResult> StatusAsync(
        PodDefinition pod,
        string deploymentName,
        CancellationToken cancellationToken = default,
        bool includeMetadata = false)
    {
        ArgumentNullException.ThrowIfNull(pod);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        var name = NormalizeDeploymentName(deploymentName);
        LogVllmStart(
            "status",
            pod.Id,
            name,
            extraFields: new Dictionary<string, string?>
            {
                ["includeMetadata"] = BoolString(includeMetadata)
            });
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var result = new PodVllmStatusResult(
                pod.Id,
                false,
                name,
                "vLLM status requires SSH-based pod.",
                string.Empty,
                -1,
                string.Empty,
                string.Empty);
            LogVllmEnd(
                "status",
                result.PodId,
                result.DeploymentName,
                result.Success,
                result.ExitCode,
                result.Summary,
                extraFields: new Dictionary<string, string?>
                {
                    ["failureKind"] = "unsupported-transport",
                    ["state"] = result.State,
                    ["ready"] = BoolString(result.Ready),
                    ["unhealthy"] = BoolString(result.Unhealthy)
                });
            return result;
        }

        var command = BuildStatusCommand(name);
        var exec = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        var state = ParseState(exec.StdOut, exec.StdErr);
        var metadataJson = includeMetadata
            ? await ReadMetadataJsonAsync(pod, name, cancellationToken).ConfigureAwait(false)
            : null;

        var finalResult = new PodVllmStatusResult(
            pod.Id,
            exec.Success,
            name,
            exec.Success
                ? $"vLLM deployment '{name}' status fetched on {pod.Id}."
                : $"vLLM status failed: {exec.Summary}",
            command,
            exec.ExitCode,
            exec.StdOut,
            exec.StdErr,
            state.State,
            state.Ready,
            state.Unhealthy,
            metadataJson);
        LogVllmEnd(
            "status",
            finalResult.PodId,
            finalResult.DeploymentName,
            finalResult.Success,
            finalResult.ExitCode,
            finalResult.Summary,
            extraFields: new Dictionary<string, string?>
            {
                ["state"] = finalResult.State,
                ["ready"] = BoolString(finalResult.Ready),
                ["unhealthy"] = BoolString(finalResult.Unhealthy),
                ["includeMetadata"] = BoolString(includeMetadata),
                ["metadataFound"] = BoolString(metadataJson is not null)
            });
        return finalResult;
    }

    public async Task<PodVllmHealthResult> HealthAsync(
        PodDefinition pod,
        string deploymentName,
        int maxAttempts = DefaultHealthAttempts,
        int backoffMilliseconds = DefaultHealthBackoffMilliseconds,
        CancellationToken cancellationToken = default,
        bool includeMetadata = false)
    {
        ArgumentNullException.ThrowIfNull(pod);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        var name = NormalizeDeploymentName(deploymentName);
        maxAttempts = NormalizeHealthAttempts(maxAttempts);
        backoffMilliseconds = NormalizeHealthBackoff(backoffMilliseconds);
        LogVllmStart(
            "health",
            pod.Id,
            name,
            extraFields: new Dictionary<string, string?>
            {
                ["maxAttempts"] = Invariant(maxAttempts),
                ["backoffMs"] = Invariant(backoffMilliseconds),
                ["includeMetadata"] = BoolString(includeMetadata)
            });
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var result = new PodVllmHealthResult(
                pod.Id,
                false,
                name,
                false,
                "unknown",
                false,
                "unsupported-transport",
                0,
                maxAttempts,
                "vLLM health requires SSH-based pod.",
                string.Empty,
                -1,
                string.Empty,
                string.Empty);
            LogHealthEnd(result, includeMetadata, metadataFound: false);
            return result;
        }

        var command = BuildHealthCommand(name);
        PodExecResult? exec = null;
        var state = new VllmState("unknown", Ready: false, Unhealthy: false, FailureKind: "unknown");
        var attempts = 0;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            attempts = attempt;
            exec = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
            state = ParseState(exec.StdOut, exec.StdErr);
            if (state.Ready || IsTerminalHealthFailure(state, exec))
            {
                break;
            }

            if (attempt < maxAttempts && backoffMilliseconds > 0)
            {
                await _delayAsync(TimeSpan.FromMilliseconds(backoffMilliseconds), cancellationToken).ConfigureAwait(false);
            }
        }

        exec ??= new PodExecResult(
            pod.Id,
            false,
            "ssh",
            command,
            $"{pod.SshHost}:{pod.SshPort ?? 22}",
            -1,
            string.Empty,
            string.Empty,
            TimeSpan.Zero,
            "ssh exec not attempted",
            PodExecFailureKinds.SshExecNotAttempted);

        var failureKind = GetHealthFailureKind(state, exec);
        var metadataJson = includeMetadata
            ? await ReadMetadataJsonAsync(pod, name, cancellationToken).ConfigureAwait(false)
            : null;

        var finalResult = new PodVllmHealthResult(
            pod.Id,
            exec.Success && state.Ready,
            name,
            state.Ready,
            state.State,
            state.Unhealthy,
            failureKind,
            attempts,
            maxAttempts,
            BuildHealthSummary(pod.Id, name, state, attempts, maxAttempts),
            command,
            exec.ExitCode,
            exec.StdOut,
            exec.StdErr,
            metadataJson);
        LogHealthEnd(finalResult, includeMetadata, metadataJson is not null);
        return finalResult;
    }

    public async Task<PodVllmOperationResult> StopAsync(
        PodDefinition pod,
        string deploymentName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pod);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        var name = NormalizeDeploymentName(deploymentName);
        LogVllmStart("stop", pod.Id, name);
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var result = new PodVllmOperationResult(
                pod.Id,
                false,
                "stop",
                name,
                "vLLM stop requires SSH-based pod.",
                string.Empty,
                -1,
                string.Empty,
                string.Empty);
            LogVllmEnd(
                "stop",
                result.PodId,
                result.DeploymentName,
                result.Success,
                result.ExitCode,
                result.Summary,
                extraFields: new Dictionary<string, string?>
                {
                    ["failureKind"] = "unsupported-transport"
                });
            return result;
        }

        var command = BuildStopCommand(name);
        var exec = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        var failureKind = exec.Success ? PodExecFailureKinds.None : PodExecFailureKinds.FromResult(exec);
        var finalResult = new PodVllmOperationResult(
            pod.Id,
            exec.Success,
            "stop",
            name,
            exec.Success
                ? $"vLLM deployment '{name}' stopped on {pod.Id}."
                : $"vLLM stop failed: {exec.Summary}",
            command,
            exec.ExitCode,
            exec.StdOut,
            exec.StdErr,
            FailureKind: failureKind);
        LogVllmEnd(
            "stop",
            finalResult.PodId,
            finalResult.DeploymentName,
            finalResult.Success,
            finalResult.ExitCode,
            finalResult.Summary);
        return finalResult;
    }

    public async Task<PodVllmRollbackResult> RollbackAsync(
        PodDefinition pod,
        string deploymentName,
        CancellationToken cancellationToken = default)
    {
        var name = NormalizeDeploymentName(deploymentName);
        LogVllmStart("rollback", pod.Id, name);
        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            var unsupportedResult = new PodVllmRollbackResult(
                pod.Id,
                false,
                name,
                "vLLM rollback requires SSH-based pod.",
                string.Empty,
                -1,
                string.Empty,
                string.Empty,
                PodExecFailureKinds.UnsupportedTransport);
            LogVllmEnd(
                "rollback",
                unsupportedResult.PodId,
                unsupportedResult.DeploymentName,
                unsupportedResult.Success,
                unsupportedResult.ExitCode,
                unsupportedResult.Summary,
                extraFields: new Dictionary<string, string?>
                {
                    ["failureKind"] = unsupportedResult.FailureKind
                });
            return unsupportedResult;
        }

        var command = BuildRollbackCommand(name);
        var exec = await _execService.ExecuteAsync(pod, command, cancellationToken).ConfigureAwait(false);
        var failureKind = exec.Success ? PodExecFailureKinds.None : PodExecFailureKinds.FromResult(exec);
        var rollbackResult = new PodVllmRollbackResult(
            pod.Id,
            exec.Success,
            name,
            exec.Success
                ? $"vLLM deployment '{name}' rolled back on {pod.Id}."
                : $"vLLM rollback failed: {exec.Summary}",
            command,
            exec.ExitCode,
            exec.StdOut,
            exec.StdErr,
            failureKind);
        LogVllmEnd(
            "rollback",
            rollbackResult.PodId,
            rollbackResult.DeploymentName,
            rollbackResult.Success,
            rollbackResult.ExitCode,
            rollbackResult.Summary,
            extraFields: new Dictionary<string, string?>
            {
                ["failureKind"] = rollbackResult.FailureKind
            });
        return rollbackResult;
    }

    private void LogPreflightEnd(PodVllmPreflightResult result)
    {
        LogVllmEnd(
            "preflight",
            result.PodId,
            result.DeploymentName,
            result.Success,
            result.ExitCode,
            result.Summary,
            result.ModelId,
            new Dictionary<string, string?>
            {
                ["failureKind"] = result.FailureKind,
                ["modelCachePresent"] = BoolString(result.ModelCachePresent),
                ["snapshotCount"] = Invariant(result.SnapshotCount),
                ["vllmAvailable"] = BoolString(result.VllmAvailable),
                ["resolvedModel"] = BoolString(!string.IsNullOrWhiteSpace(result.ResolvedModelPath)),
                ["requestedRevision"] = result.RequestedRevision
            });
    }

    private void LogHealthEnd(PodVllmHealthResult result, bool includeMetadata, bool metadataFound)
    {
        LogVllmEnd(
            "health",
            result.PodId,
            result.DeploymentName,
            result.Success,
            result.ExitCode,
            result.Summary,
            extraFields: new Dictionary<string, string?>
            {
                ["ready"] = BoolString(result.Ready),
                ["state"] = result.State,
                ["unhealthy"] = BoolString(result.Unhealthy),
                ["failureKind"] = result.FailureKind,
                ["attempts"] = Invariant(result.Attempts),
                ["maxAttempts"] = Invariant(result.MaxAttempts),
                ["includeMetadata"] = BoolString(includeMetadata),
                ["metadataFound"] = BoolString(metadataFound)
            });
    }

    private void LogVllmStart(
        string operation,
        string podId,
        string deploymentName,
        string? modelId = null,
        IReadOnlyDictionary<string, string?>? extraFields = null)
    {
        var fields = BuildVllmFields(operation, podId, deploymentName, modelId, extraFields);
        _logSink.Log(new TauLogEvent("pod", $"vllm.{operation}.start", DateTimeOffset.UtcNow, fields));
    }

    private void LogVllmEnd(
        string operation,
        string podId,
        string deploymentName,
        bool success,
        int exitCode,
        string summary,
        string? modelId = null,
        IReadOnlyDictionary<string, string?>? extraFields = null)
    {
        var fields = BuildVllmFields(operation, podId, deploymentName, modelId, extraFields);
        fields["success"] = BoolString(success);
        fields["exitCode"] = Invariant(exitCode);
        fields["summary"] = summary;
        _logSink.Log(new TauLogEvent("pod", $"vllm.{operation}.end", DateTimeOffset.UtcNow, fields));
    }

    private static Dictionary<string, string?> BuildVllmFields(
        string operation,
        string podId,
        string deploymentName,
        string? modelId,
        IReadOnlyDictionary<string, string?>? extraFields)
    {
        var fields = new Dictionary<string, string?>
        {
            ["podId"] = podId,
            ["operation"] = operation,
            ["deploymentName"] = deploymentName
        };
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            fields["modelId"] = modelId;
        }

        if (extraFields is not null)
        {
            foreach (var item in extraFields)
            {
                fields[item.Key] = item.Value;
            }
        }

        return fields;
    }

    private static string BoolString(bool value) => value ? "true" : "false";

    private static string Invariant(int value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string GetDeployFailureKind(
        bool success,
        PodExecResult exec,
        PodVllmHealthResult? health)
    {
        if (success)
        {
            return PodExecFailureKinds.None;
        }

        if (health is not null)
        {
            return health.FailureKind;
        }

        var failureKind = PodExecFailureKinds.FromResult(exec);
        return failureKind == PodExecFailureKinds.None ? "deploy-failed" : failureKind;
    }

    private static string BuildDeployCommand(PodVllmServePlan plan)
    {
        var unitBaseName = StripServiceSuffix(plan.UnitName);
        var logPath = string.IsNullOrWhiteSpace(plan.LogPath)
            ? $"~/.vllm_logs/{plan.DeploymentName}.log"
            : plan.LogPath;
        var pidPath = $"~/.tau_pods/{plan.DeploymentName}.pid";
        var fallbackCommand =
            $"nohup /usr/bin/env bash -lc {ShellSingleQuote(plan.ServeCommand)} > {logPath} 2>&1 < /dev/null & " +
            $"echo $! > {pidPath}; pid=$(cat {pidPath}); echo \"pid=$pid\"";
        var systemdCommand =
            $"mkdir -p ~/.config/systemd/user && " +
            $"cp ~/.tau_pods/{plan.DeploymentName}.service ~/.config/systemd/user/{plan.UnitName} && " +
            "systemctl --user daemon-reload && " +
            $"systemctl --user enable --now {ShellSingleQuote(plan.UnitName)}";
        var systemdPidCommand =
            $"pid=$(systemctl --user show {ShellSingleQuote(plan.UnitName)} --property=MainPID --value 2>/dev/null || true); " +
            "if [ -n \"$pid\" ] && [ \"$pid\" != \"0\" ]; then echo \"pid=$pid\"; fi";

        return
            $"mkdir -p ~/.tau_pods ~/.vllm_logs && " +
            $"cat > ~/.tau_pods/{plan.DeploymentName}.service <<'EOF'\n{plan.SystemdUnit}\nEOF\n" +
            $"cat > ~/.tau_pods/{plan.DeploymentName}.json <<'EOF'\n{plan.MetadataJson}\nEOF\n" +
            $"if command -v systemctl >/dev/null 2>&1; then " +
            $"if {systemdCommand}; then echo {ShellSingleQuote($"started {unitBaseName}")}; {systemdPidCommand}; " +
            $"else {fallbackCommand} && echo {ShellSingleQuote($"started {plan.DeploymentName}")}; fi; " +
            $"else {fallbackCommand} && echo {ShellSingleQuote($"started {plan.DeploymentName}")}; fi";
    }

    private static int? ExtractProcessId(string stdout)
    {
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("pid=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(
                trimmed["pid=".Length..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var pid) && pid > 0)
            {
                return pid;
            }
        }

        return null;
    }

    private static string BuildPreflightCommand(string modelCachePath, string? revision)
    {
        var cachePath = ShellPath(modelCachePath.TrimEnd('/'));
        var builder = new StringBuilder()
            .Append("cache=").Append(cachePath).Append("; ")
            .Append("snapshots=\"$cache/snapshots\"; ");

        var normalizedRevision = NormalizeRevision(revision);
        if (normalizedRevision is null)
        {
            builder.Append("ref_file=\"$cache/refs/main\"; ");
        }
        else
        {
            builder.Append("requested_revision=").Append(ShellSingleQuote(normalizedRevision)).Append("; ");
        }

        builder
            .Append("echo \"model_cache_path=$cache\"; ");

        if (normalizedRevision is not null)
        {
            builder.Append("echo \"requested_revision=$requested_revision\"; ");
        }

        builder
            .Append("if command -v vllm >/dev/null 2>&1; then echo vllm_available=true; else echo vllm_available=false; echo failure_kind=vllm-missing; exit 14; fi; ")
            .Append("if [ ! -d \"$cache\" ]; then echo model_cache_present=false; echo snapshot_count=0; echo failure_kind=model-cache-missing; exit 10; fi; ")
            .Append("echo model_cache_present=true; ")
            .Append("if [ ! -d \"$snapshots\" ]; then echo snapshot_count=0; echo failure_kind=model-snapshots-missing; exit 11; fi; ")
            .Append("snapshot_count=$(find \"$snapshots\" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | wc -l | tr -d ' '); ")
            .Append("echo \"snapshot_count=$snapshot_count\"; ")
            .Append("if [ \"$snapshot_count\" -eq 0 ]; then echo failure_kind=model-snapshot-missing; exit 12; fi; ");

        if (normalizedRevision is null)
        {
            builder
                .Append("if [ -f \"$ref_file\" ]; then ")
                .Append("ref=$(head -n 1 \"$ref_file\" | tr -d '\\r\\n'); ")
                .Append("if [ -n \"$ref\" ] && [ -d \"$snapshots/$ref\" ]; then echo \"resolved_model_path=$snapshots/$ref\"; echo failure_kind=none; exit 0; fi; ")
                .Append("echo failure_kind=model-snapshot-ref-missing; exit 13; fi; ")
                .Append("if [ \"$snapshot_count\" -eq 1 ]; then resolved=$(find \"$snapshots\" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | head -n 1); echo \"resolved_model_path=$resolved\"; echo failure_kind=none; exit 0; fi; ")
                .Append("echo failure_kind=model-snapshot-ambiguous; exit 15");
        }
        else
        {
            builder
                .Append("ref_file=\"$cache/refs/$requested_revision\"; ")
                .Append("if [ -f \"$ref_file\" ]; then ")
                .Append("ref=$(head -n 1 \"$ref_file\" | tr -d '\\r\\n'); ")
                .Append("if [ -n \"$ref\" ] && [ -d \"$snapshots/$ref\" ]; then echo \"resolved_model_path=$snapshots/$ref\"; echo failure_kind=none; exit 0; fi; fi; ")
                .Append("if [ -d \"$snapshots/$requested_revision\" ]; then echo \"resolved_model_path=$snapshots/$requested_revision\"; echo failure_kind=none; exit 0; fi; ")
                .Append("echo failure_kind=model-snapshot-revision-missing; exit 16");
        }

        return builder.ToString();
    }

    private static string BuildStatusCommand(string deploymentName)
    {
        var unitName = $"tau-pod-{deploymentName}.service";
        var unitBaseName = StripServiceSuffix(unitName);
        var vllmLogPath = $"~/.vllm_logs/{deploymentName}.log";
        var tauLogPath = $"~/.tau_pods/{deploymentName}.log";
        var failurePattern = ShellSingleQuote(HealthFailurePattern());
        var startupCompletePattern = ShellSingleQuote(HealthStartupCompletePattern());
        return
            BuildPortProbePrefix(deploymentName) +
            "if curl -fsS --max-time 2 \"http://127.0.0.1:$port/health\" >/dev/null 2>&1; then echo ready; " +
            $"elif test -f {vllmLogPath} && tail -n 80 {vllmLogPath} 2>/dev/null | grep -Eiq {failurePattern}; then echo unhealthy; " +
            $"elif test -f {vllmLogPath} && tail -n 80 {vllmLogPath} 2>/dev/null | grep -Fq {startupCompletePattern}; then echo ready; " +
            $"elif test -f {tauLogPath} && tail -n 40 {tauLogPath} 2>/dev/null | grep -Eiq {failurePattern}; then echo unhealthy; " +
            $"elif command -v systemctl >/dev/null 2>&1 && systemctl --user list-unit-files {ShellSingleQuote(unitName)} >/dev/null 2>&1; then " +
            $"state=$(systemctl --user is-active {ShellSingleQuote(unitName)} 2>/dev/null || true); " +
            "if [ \"$state\" = active ]; then echo starting; elif [ \"$state\" = failed ]; then echo unhealthy; else echo \"${state:-starting}\"; fi; " +
            $"elif test -f ~/.tau_pods/{deploymentName}.pid; then " +
            $"pid=$(cat ~/.tau_pods/{deploymentName}.pid); " +
            "if kill -0 \"$pid\" >/dev/null 2>&1; then echo starting; else echo dead; fi; " +
            $"elif test -f ~/.tau_pods/{deploymentName}.json; then echo starting; " +
            $"else echo {ShellSingleQuote($"not found {unitBaseName}")}; exit 1; fi; " +
            $"if command -v systemctl >/dev/null 2>&1 && systemctl --user list-unit-files {ShellSingleQuote(unitName)} >/dev/null 2>&1; then " +
            $"systemctl --user status {ShellSingleQuote(unitName)} --no-pager -l 2>&1 | tail -n 40; " +
            $"elif test -f {vllmLogPath}; then tail -n 40 {vllmLogPath} 2>/dev/null || true; " +
            $"elif test -f {tauLogPath}; then tail -n 40 {tauLogPath} 2>/dev/null || true; fi";
    }

    private static string BuildHealthCommand(string deploymentName)
    {
        var unitName = $"tau-pod-{deploymentName}.service";
        var unitBaseName = StripServiceSuffix(unitName);
        var vllmLogPath = $"~/.vllm_logs/{deploymentName}.log";
        var tauLogPath = $"~/.tau_pods/{deploymentName}.log";
        var failurePattern = ShellSingleQuote(HealthFailurePattern());
        var startupCompletePattern = ShellSingleQuote(HealthStartupCompletePattern());
        return
            BuildPortProbePrefix(deploymentName) +
            "if curl -fsS --max-time 2 \"http://127.0.0.1:$port/health\" >/dev/null 2>&1; then echo ready; exit 0; fi; " +
            $"if test -f {vllmLogPath} && tail -n 80 {vllmLogPath} 2>/dev/null | grep -Eiq {failurePattern}; then echo unhealthy; exit 2; fi; " +
            $"if test -f {vllmLogPath} && tail -n 80 {vllmLogPath} 2>/dev/null | grep -Fq {startupCompletePattern}; then echo ready; exit 0; fi; " +
            $"if test -f {tauLogPath} && tail -n 40 {tauLogPath} 2>/dev/null | grep -Eiq {failurePattern}; then echo unhealthy; exit 2; fi; " +
            $"if command -v systemctl >/dev/null 2>&1 && systemctl --user list-unit-files {ShellSingleQuote(unitName)} >/dev/null 2>&1; then " +
            $"state=$(systemctl --user is-active {ShellSingleQuote(unitName)} 2>/dev/null || true); " +
            "if [ \"$state\" = failed ]; then echo unhealthy; exit 2; fi; " +
            "if [ \"$state\" = active ]; then echo starting; exit 3; fi; fi; " +
            $"if test -f ~/.tau_pods/{deploymentName}.pid; then " +
            $"pid=$(cat ~/.tau_pods/{deploymentName}.pid); " +
            "if kill -0 \"$pid\" >/dev/null 2>&1; then echo starting; exit 3; else echo dead; exit 4; fi; fi; " +
            $"if test -f ~/.tau_pods/{deploymentName}.json; then echo starting; exit 3; " +
            $"else echo {ShellSingleQuote($"not found {unitBaseName}")}; exit 1; fi";
    }

    private async Task<string?> ReadMetadataJsonAsync(
        PodDefinition pod,
        string deploymentName,
        CancellationToken cancellationToken)
    {
        var exec = await _execService
            .ExecuteAsync(pod, BuildMetadataCommand(deploymentName), cancellationToken)
            .ConfigureAwait(false);

        if (!exec.Success || string.IsNullOrWhiteSpace(exec.StdOut))
        {
            return null;
        }

        return TryNormalizeMetadataJson(exec.StdOut);
    }

    private static string BuildMetadataCommand(string deploymentName) =>
        $"if test -f ~/.tau_pods/{deploymentName}.json; then cat ~/.tau_pods/{deploymentName}.json 2>/dev/null || true; fi";

    private static string? TryNormalizeMetadataJson(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.ValueKind == JsonValueKind.Object ? trimmed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildStopCommand(string deploymentName)
    {
        return BuildCleanupCommand(deploymentName, "stopped");
    }

    private static string BuildRollbackCommand(string deploymentName)
    {
        return BuildCleanupCommand(deploymentName, "rolled back");
    }

    private static string BuildCleanupCommand(string deploymentName, string verb)
    {
        var unitName = $"tau-pod-{deploymentName}.service";
        var unitBaseName = StripServiceSuffix(unitName);
        return
            $"if command -v systemctl >/dev/null 2>&1 && systemctl --user list-unit-files {ShellSingleQuote(unitName)} >/dev/null 2>&1; then " +
            $"systemctl --user disable --now {ShellSingleQuote(unitName)} || true; " +
            $"rm -f ~/.config/systemd/user/{unitName}; " +
            "systemctl --user daemon-reload || true; fi; " +
            $"if test -f ~/.tau_pods/{deploymentName}.pid; then " +
            $"pid=$(cat ~/.tau_pods/{deploymentName}.pid); " +
            "pkill -TERM -P \"$pid\" >/dev/null 2>&1 || true; " +
            "kill \"$pid\" >/dev/null 2>&1 || true; " +
            "rm -f ~/.tau_pods/" + deploymentName + ".pid; fi; " +
            $"rm -f ~/.tau_pods/{deploymentName}.json ~/.tau_pods/{deploymentName}.service; " +
            $"echo {ShellSingleQuote($"{verb} {unitBaseName}")}";
    }

    private static string BuildPortProbePrefix(string deploymentName) =>
        $"port=$(sed -n 's/.*\"port\":\\([0-9][0-9]*\\).*/\\1/p' ~/.tau_pods/{deploymentName}.json 2>/dev/null | head -n 1); " +
        "if [ -z \"$port\" ]; then port=8000; fi; ";

    private static string HealthFailurePattern() =>
        "ERROR|Failed|Cuda error|CUDA out of memory|torch\\.OutOfMemoryError|OutOfMemory|RuntimeError: Engine core initialization failed|Engine core initialization failed|Model runner exiting with code [1-9]|Script exited with code [1-9]|Traceback|died";

    private static string HealthStartupCompletePattern() => "Application startup complete";

    private static string BuildHealthSummary(string podId, string deploymentName, VllmState state, int attempts, int maxAttempts) =>
        state.State switch
        {
            "ready" => $"vLLM deployment '{deploymentName}' is ready on {podId} after {attempts} health attempt(s).",
            "unhealthy" => $"vLLM deployment '{deploymentName}' is unhealthy on {podId} after {attempts} health attempt(s).",
            "dead" => $"vLLM deployment '{deploymentName}' is dead on {podId} after {attempts} health attempt(s).",
            "not-found" => $"vLLM deployment '{deploymentName}' was not found on {podId} after {attempts} health attempt(s).",
            _ => $"vLLM deployment '{deploymentName}' is {state.State} on {podId} after {attempts}/{maxAttempts} health attempt(s)."
        };

    private static string BuildPreflightSummary(
        string podId,
        string deploymentName,
        string modelId,
        string? requestedRevision,
        string? resolvedModelPath,
        string failureKind,
        int snapshotCount) =>
        failureKind == "none"
            ? requestedRevision is null
                ? $"vLLM preflight ok for '{deploymentName}' on {podId}: model '{modelId}' resolved to '{resolvedModelPath}' ({snapshotCount} snapshot(s))."
                : $"vLLM preflight ok for '{deploymentName}' on {podId}: model '{modelId}' revision '{requestedRevision}' resolved to '{resolvedModelPath}' ({snapshotCount} snapshot(s))."
            : $"vLLM preflight failed for '{deploymentName}' on {podId}: {failureKind}. {GetPreflightFailureHint(failureKind)}";

    private static string GetPreflightFailureHint(string failureKind) =>
        failureKind switch
        {
            "vllm-missing" => "Install or activate vLLM on the remote pod before deploying.",
            "model-cache-missing" or "model-snapshots-missing" or "model-snapshot-missing" =>
                "Pull the model first or point modelsPath at the Hugging Face hub cache root.",
            "model-snapshot-ref-missing" =>
                "The refs/main file points to a snapshot directory that does not exist.",
            "model-snapshot-ambiguous" =>
                "Multiple snapshots exist without a valid refs/main target; choose or repair the remote cache revision.",
            "model-snapshot-revision-missing" =>
                "The requested revision does not exist under refs/ or snapshots/ on the remote cache.",
            "unsupported-transport" =>
                "Configure an SSH pod for vLLM orchestration.",
            "ssh-process-start-failed" or "ssh-process-runner-failed" or "ssh-exec-cancelled" or
                "ssh-auth-failed" or "ssh-host-key-failed" or "ssh-host-unresolved" or
                "ssh-connect-timeout" or "ssh-connection-failed" or "ssh-exec-failed" =>
                "Check the SSH transport and remote command output.",
            _ => "Inspect stdout, stderr, and remoteCommand for the remote preflight output."
        };

    private static bool IsPrefetchableFailureKind(string failureKind) =>
        failureKind switch
        {
            "model-cache-missing" => true,
            "model-snapshots-missing" => true,
            "model-snapshot-missing" => true,
            "model-snapshot-ref-missing" => true,
            "model-snapshot-ambiguous" => true,
            "model-snapshot-revision-missing" => true,
            _ => false
        };

    private static IReadOnlyDictionary<string, string> ParseKeyValueOutput(string stdout)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            values[key] = line[(separator + 1)..].Trim();
        }

        return values;
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) &&
        value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) &&
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static VllmState ParseState(string stdout, string stderr)
    {
        var text = string.Join('\n', stdout, stderr).Trim();
        if (ContainsStateToken(text, "ready"))
        {
            return new VllmState("ready", Ready: true, Unhealthy: false, FailureKind: "none");
        }

        if (text.Contains(HealthStartupCompletePattern(), StringComparison.OrdinalIgnoreCase))
        {
            return new VllmState("ready", Ready: true, Unhealthy: false, FailureKind: "none");
        }

        if (ContainsStateToken(text, "unhealthy") ||
            ContainsStateToken(text, "crashed") ||
            ContainsStateToken(text, "failed") ||
            ContainsStartupFailureMarker(text))
        {
            return new VllmState("unhealthy", Ready: false, Unhealthy: true, FailureKind: "startup-failed");
        }

        if (ContainsStateToken(text, "dead"))
        {
            return new VllmState("dead", Ready: false, Unhealthy: true, FailureKind: "process-dead");
        }

        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return new VllmState("not-found", Ready: false, Unhealthy: true, FailureKind: "not-found");
        }

        if (ContainsStateToken(text, "starting") ||
            ContainsStateToken(text, "activating") ||
            ContainsStateToken(text, "active") ||
            ContainsStateToken(text, "running") ||
            ContainsStateToken(text, "planned"))
        {
            return new VllmState("starting", Ready: false, Unhealthy: false, FailureKind: "not-ready");
        }

        return new VllmState("unknown", Ready: false, Unhealthy: false, FailureKind: "unknown");
    }

    private static bool ContainsStartupFailureMarker(string text) =>
        text.Contains("Model runner exiting with code", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Script exited with code", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("torch.OutOfMemoryError", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("CUDA out of memory", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("RuntimeError: Engine core initialization failed", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Engine core initialization failed", StringComparison.OrdinalIgnoreCase);

    private static string GetHealthFailureKind(VllmState state, PodExecResult exec)
    {
        if (state.Ready)
        {
            return PodExecFailureKinds.None;
        }

        var execFailureKind = PodExecFailureKinds.FromResult(exec);
        if (state.FailureKind == "unknown" && execFailureKind != PodExecFailureKinds.None)
        {
            return execFailureKind;
        }

        return IsTerminalTransportFailure(execFailureKind) ? execFailureKind : state.FailureKind;
    }

    private static bool IsTerminalHealthFailure(VllmState state, PodExecResult exec)
    {
        var execFailureKind = PodExecFailureKinds.FromResult(exec);
        return state.Unhealthy ||
            state.FailureKind is "not-found" ||
            exec.ExitCode is 1 or 2 or 4 ||
            (state.FailureKind == "unknown" && exec.ExitCode != 0) ||
            IsTerminalTransportFailure(execFailureKind);
    }

    private static bool IsTerminalTransportFailure(string failureKind) =>
        failureKind is PodExecFailureKinds.SshProcessStartFailed or
            PodExecFailureKinds.SshProcessRunnerFailed or
            PodExecFailureKinds.SshExecCancelled or
            PodExecFailureKinds.SshAuthFailed or
            PodExecFailureKinds.SshHostKeyFailed or
            PodExecFailureKinds.SshHostUnresolved or
            PodExecFailureKinds.SshConnectTimeout or
            PodExecFailureKinds.SshConnectionFailed or
            PodExecFailureKinds.SshExecNotAttempted;

    private static int NormalizeHealthAttempts(int attempts) =>
        Math.Clamp(attempts, 1, 120);

    private static int NormalizeHealthBackoff(int backoffMilliseconds) =>
        Math.Clamp(backoffMilliseconds, 0, 600000);

    private static bool ContainsStateToken(string text, string token)
    {
        var span = text.AsSpan();
        var tokenSpan = token.AsSpan();
        for (var index = 0; index <= span.Length - tokenSpan.Length; index++)
        {
            if (!span[index..(index + tokenSpan.Length)].Equals(tokenSpan, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var before = index == 0 ? '\0' : span[index - 1];
            var afterIndex = index + tokenSpan.Length;
            var after = afterIndex >= span.Length ? '\0' : span[afterIndex];
            if (!IsStateTokenCharacter(before) && !IsStateTokenCharacter(after))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStateTokenCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '_';

    private static string StripServiceSuffix(string unitName) =>
        unitName.EndsWith(".service", StringComparison.Ordinal)
            ? unitName[..^".service".Length]
            : unitName;

    private static string NormalizeDeploymentName(string value)
    {
        var trimmed = value.Trim();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-');
        }

        var normalized = builder.ToString().Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(normalized) ? "deployment" : normalized;
    }

    private static string? NormalizeRevision(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static string ShellSingleQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string ShellPath(string path)
    {
        const string homePrefix = "$HOME/";
        return path.StartsWith(homePrefix, StringComparison.Ordinal)
            ? "\"$HOME/" + ShellDoubleQuoteContent(path[homePrefix.Length..]) + "\""
            : ShellSingleQuote(path);
    }

    private static string ShellDoubleQuoteContent(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

    private sealed record VllmState(string State, bool Ready, bool Unhealthy, string FailureKind);
}
