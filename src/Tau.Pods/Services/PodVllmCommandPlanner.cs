using System.Globalization;
using System.Text;
using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodVllmCommandPlanner
{
    private const string DefaultModelsPath = "$HOME/.cache/huggingface/hub";
    private readonly PodKnownModelRegistry _knownModels;

    public PodVllmCommandPlanner(PodKnownModelRegistry? knownModels = null)
    {
        _knownModels = knownModels ?? new PodKnownModelRegistry();
    }

    public PodVllmServePlan PlanServe(PodDefinition pod, PodVllmServeOptions options)
    {
        ArgumentNullException.ThrowIfNull(pod);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelId);

        var deploymentName = NormalizeDeploymentName(options.DeploymentName ?? options.ModelId);
        var port = options.Port <= 0 ? 8000 : options.Port;
        var servedModelName = string.IsNullOrWhiteSpace(options.ServedModelName)
            ? deploymentName
            : options.ServedModelName.Trim();
        var revision = NormalizeRevision(options.Revision);
        var hasExplicitVllmArgs = options.ExtraArgs is { Count: > 0 };
        var requestedGpuCount = hasExplicitVllmArgs ? null : options.RequestedGpuCount;
        var memoryOverride = hasExplicitVllmArgs ? null : NormalizeOptionValue(options.Memory);
        var contextOverride = hasExplicitVllmArgs ? null : NormalizeOptionValue(options.Context);
        var knownConfig = hasExplicitVllmArgs
            ? null
            : ResolveKnownModelConfig(pod, options.ModelId, requestedGpuCount);
        var selectedGpuIds = ResolveSelectedGpuIds(pod, knownConfig, hasExplicitVllmArgs);
        var autoEnvironment = BuildGpuEnvironment(selectedGpuIds);
        var environment = MergeEnvironment(autoEnvironment, knownConfig?.Environment, options.Environment);
        double? memoryUtilization = null;
        int? contextTokens = null;
        var extraArgs = hasExplicitVllmArgs
            ? options.ExtraArgs
            : ApplyConvenienceOverrides(
                knownConfig?.Args,
                memoryOverride,
                contextOverride,
                out memoryUtilization,
                out contextTokens);
        var modelCachePath = BuildModelCachePath(pod, options.ModelId);
        var hasResolvedModelPath = !string.IsNullOrWhiteSpace(options.ResolvedModelPath);
        var usesSnapshotDiscovery = !hasResolvedModelPath;
        var modelPath = hasResolvedModelPath ? options.ResolvedModelPath!.Trim() : modelCachePath;
        var unitName = $"tau-pod-{deploymentName}.service";
        var serveCommand = hasResolvedModelPath
            ? BuildServeCommand(modelPath, port, servedModelName, environment, extraArgs)
            : BuildSnapshotDiscoveryServeCommand(modelCachePath, port, servedModelName, environment, extraArgs, revision);
        var logPath = $"~/.vllm_logs/{deploymentName}.log";
        var runnerScriptPath = $"~/.tau_pods/model_run_{deploymentName}.sh";
        var wrapperScriptPath = $"~/.tau_pods/model_wrapper_{deploymentName}.sh";
        var runnerScript = BuildModelRunScript(options.ModelId.Trim(), deploymentName, port, extraArgs, serveCommand);
        var wrapperScript = BuildModelWrapperScript(deploymentName);
        var unit = BuildSystemdUnit(unitName, wrapperScriptPath, deploymentName);
        var metadata = BuildMetadataJson(
            options.ModelId.Trim(),
            deploymentName,
            modelPath,
            usesSnapshotDiscovery,
            port,
            servedModelName,
            unitName,
            logPath,
            runnerScriptPath,
            wrapperScriptPath,
            usesPseudoTtyWrapper: true,
            revision,
            knownConfig,
            requestedGpuCount,
            selectedGpuIds,
            memoryOverride,
            memoryUtilization,
            contextOverride,
            contextTokens);
        var remoteCommand =
            $"mkdir -p ~/.tau_pods ~/.vllm_logs && " +
            $"cat > {runnerScriptPath} <<'EOF'\n{runnerScript}\nEOF\n" +
            $"chmod +x {runnerScriptPath}\n" +
            $"cat > {wrapperScriptPath} <<'EOF'\n{wrapperScript}\nEOF\n" +
            $"chmod +x {wrapperScriptPath}\n" +
            $"cat > ~/.tau_pods/{deploymentName}.service <<'EOF'\n{unit}\nEOF\n" +
            $"cat > ~/.tau_pods/{deploymentName}.json <<'EOF'\n{metadata}\nEOF\n" +
            $"echo {ShellSingleQuote($"planned {deploymentName}")}";

        return new PodVllmServePlan(
            deploymentName,
            options.ModelId.Trim(),
            modelPath,
            port,
            servedModelName,
            unitName,
            serveCommand,
            unit,
            metadata,
            remoteCommand,
            UsesSnapshotDiscovery: usesSnapshotDiscovery,
            RunnerScriptPath: runnerScriptPath,
            WrapperScriptPath: wrapperScriptPath,
            RunnerScript: runnerScript,
            WrapperScript: wrapperScript,
            UsesPseudoTtyWrapper: true,
            Revision: revision,
            KnownModelName: knownConfig?.Name,
            KnownModelGpuCount: knownConfig?.GpuCount,
            KnownModelArgs: knownConfig?.Args,
            KnownModelEnvironment: knownConfig?.Environment,
            KnownModelNotes: knownConfig?.Notes,
            RequestedGpuCount: requestedGpuCount,
            SelectedGpuIds: selectedGpuIds,
            MemoryOverride: memoryOverride,
            MemoryUtilization: memoryUtilization,
            ContextOverride: contextOverride,
            ContextTokens: contextTokens,
            LogPath: logPath);
    }

    public string BuildModelCachePath(PodDefinition pod, string modelId)
    {
        ArgumentNullException.ThrowIfNull(pod);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        return BuildModelPath(GetModelsPath(pod), modelId);
    }

    private static string BuildServeCommand(
        string modelPath,
        int port,
        string servedModelName,
        IReadOnlyDictionary<string, string>? environment,
        IReadOnlyList<string>? extraArgs)
    {
        return BuildEnvironmentPrefix(environment) +
            BuildVllmServeCommand(ShellSingleQuote(modelPath), port, servedModelName, extraArgs);
    }

    private static string BuildSnapshotDiscoveryServeCommand(
        string modelCachePath,
        int port,
        string servedModelName,
        IReadOnlyDictionary<string, string>? environment,
        IReadOnlyList<string>? extraArgs,
        string? revision)
    {
        return BuildSnapshotDiscoveryCommand(modelCachePath, revision) + " " +
            BuildEnvironmentPrefix(environment) +
            BuildVllmServeCommand("\"$resolved_model_path\"", port, servedModelName, extraArgs);
    }

    private static string BuildModelRunScript(
        string modelId,
        string deploymentName,
        int port,
        IReadOnlyList<string>? extraArgs,
        string serveCommand)
    {
        var vllmArgs = BuildVllmArgsText(extraArgs);
        return
            "#!/usr/bin/env bash\n" +
            "# Tau Pods model runner. Keeps upstream model_run.sh markers while running the Tau-planned vLLM command.\n" +
            "set -euo pipefail\n\n" +
            $"MODEL_ID={ShellSingleQuote(modelId)}\n" +
            $"NAME={ShellSingleQuote(deploymentName)}\n" +
            $"PORT={port.ToString(CultureInfo.InvariantCulture)}\n" +
            $"VLLM_ARGS={ShellSingleQuote(vllmArgs)}\n" +
            $"VLLM_CMD={ShellSingleQuote(serveCommand)}\n\n" +
            "cleanup() {\n" +
            "    local exit_code=$?\n" +
            "    echo \"Model runner exiting with code $exit_code\"\n" +
            "    pkill -P $$ 2>/dev/null || true\n" +
            "    exit $exit_code\n" +
            "}\n" +
            "trap cleanup EXIT TERM INT\n\n" +
            "export FORCE_COLOR=1\n" +
            "export PYTHONUNBUFFERED=1\n" +
            "export TERM=xterm-256color\n" +
            "export RICH_FORCE_TERMINAL=1\n" +
            "export CLICOLOR_FORCE=1\n\n" +
            "if [ -f /root/venv/bin/activate ]; then\n" +
            "    source /root/venv/bin/activate\n" +
            "fi\n\n" +
            "echo \"=========================================\"\n" +
            "echo \"Model Run: $NAME\"\n" +
            "echo \"Model ID: $MODEL_ID\"\n" +
            "echo \"Port: $PORT\"\n" +
            "if [ -n \"$VLLM_ARGS\" ]; then\n" +
            "    echo \"vLLM Args: $VLLM_ARGS\"\n" +
            "fi\n" +
            "echo \"=========================================\"\n" +
            "echo \"\"\n" +
            "echo \"Downloading model (will skip if cached)...\"\n" +
            "HF_HUB_ENABLE_HF_TRANSFER=1 hf download \"$MODEL_ID\"\n\n" +
            "if [ $? -ne 0 ]; then\n" +
            "    echo \"ERROR: Failed to download model\" >&2\n" +
            "    exit 1\n" +
            "fi\n\n" +
            "echo \"\"\n" +
            "echo \"Model download complete\"\n" +
            "echo \"\"\n" +
            "echo \"Starting vLLM server...\"\n" +
            "echo \"Command: $VLLM_CMD\"\n" +
            "echo \"=========================================\"\n" +
            "echo \"\"\n" +
            "echo \"Starting vLLM process...\"\n" +
            "bash -c \"$VLLM_CMD\" &\n" +
            "VLLM_PID=$!\n\n" +
            "echo \"Monitoring vLLM process (PID: $VLLM_PID)...\"\n" +
            "wait $VLLM_PID\n" +
            "VLLM_EXIT_CODE=$?\n\n" +
            "if [ $VLLM_EXIT_CODE -ne 0 ]; then\n" +
            "    echo \"ERROR: vLLM exited with code $VLLM_EXIT_CODE\" >&2\n" +
            "    kill -TERM $$ 2>/dev/null || true\n" +
            "    exit $VLLM_EXIT_CODE\n" +
            "fi\n\n" +
            "echo \"vLLM exited normally\"\n" +
            "exit 0\n";
    }

    private static string BuildModelWrapperScript(string deploymentName) =>
        "#!/usr/bin/env bash\n" +
        $"script -q -f -c \"$HOME/.tau_pods/model_run_{deploymentName}.sh\" \"$HOME/.vllm_logs/{deploymentName}.log\"\n" +
        "exit_code=$?\n" +
        $"echo \"Script exited with code $exit_code\" >> \"$HOME/.vllm_logs/{deploymentName}.log\"\n" +
        "exit $exit_code\n";

    private static string BuildVllmArgsText(IReadOnlyList<string>? extraArgs)
    {
        if (extraArgs is null)
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            extraArgs
                .Where(static arg => !string.IsNullOrWhiteSpace(arg))
                .Select(static arg => arg.Trim()));
    }

    private static string BuildEnvironmentPrefix(IReadOnlyDictionary<string, string>? environment)
    {
        var builder = new StringBuilder();
        if (environment is not null)
        {
            foreach (var pair in environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                builder
                    .Append(NormalizeEnvironmentKey(pair.Key))
                    .Append('=')
                    .Append(ShellSingleQuote(pair.Value ?? string.Empty))
                    .Append(' ');
            }
        }

        return builder.ToString();
    }

    private PodKnownModelConfig? ResolveKnownModelConfig(
        PodDefinition pod,
        string modelId,
        int? requestedGpuCount)
    {
        if (_knownModels.IsKnownModel(modelId))
        {
            if (requestedGpuCount is { } requested)
            {
                if (requested > pod.Gpus.Count)
                {
                    throw new InvalidOperationException($"Error: Requested {requested} GPUs but pod only has {pod.Gpus.Count}.");
                }

                var requestedConfig = _knownModels.GetConfig(modelId, pod.Gpus, requested);
                if (requestedConfig is null)
                {
                    throw new InvalidOperationException(
                        $"Model '{_knownModels.GetModelName(modelId)}' does not have a configuration for {requested} GPU(s). Available configurations: {FormatAvailableKnownModelConfigs(modelId, pod.Gpus)}.");
                }

                return requestedConfig;
            }

            var bestConfig = _knownModels.GetBestConfig(modelId, pod.Gpus);
            if (bestConfig is null)
            {
                throw new InvalidOperationException($"Model '{_knownModels.GetModelName(modelId)}' not compatible with this pod's GPUs.");
            }

            return bestConfig;
        }

        if (requestedGpuCount is not null)
        {
            throw new InvalidOperationException("Error: --gpus can only be used with predefined models. For custom models, use --vllm with tensor-parallel-size or similar arguments.");
        }

        return null;
    }

    private string FormatAvailableKnownModelConfigs(string modelId, IReadOnlyList<PodGpuInfo> gpus)
    {
        var values = new List<string>();
        for (var gpuCount = 1; gpuCount <= gpus.Count; gpuCount++)
        {
            if (_knownModels.GetConfig(modelId, gpus, gpuCount) is not null)
            {
                values.Add($"{gpuCount} GPU(s)");
            }
        }

        return values.Count == 0 ? "none" : string.Join(", ", values);
    }

    private static IReadOnlyList<int>? ResolveSelectedGpuIds(
        PodDefinition pod,
        PodKnownModelConfig? knownConfig,
        bool hasExplicitVllmArgs)
    {
        if (hasExplicitVllmArgs)
        {
            return null;
        }

        var gpuCount = knownConfig?.GpuCount ?? (pod.Gpus.Count > 0 ? 1 : 0);
        return SelectGpuIds(pod, gpuCount);
    }

    private static IReadOnlyList<int> SelectGpuIds(PodDefinition pod, int count)
    {
        if (count <= 0 || pod.Gpus.Count == 0)
        {
            return [];
        }

        if (count > pod.Gpus.Count)
        {
            throw new InvalidOperationException($"Error: Requested {count} GPUs but pod only has {pod.Gpus.Count}.");
        }

        if (count == pod.Gpus.Count)
        {
            return pod.Gpus.Select(static gpu => gpu.Id).ToArray();
        }

        var gpuUsage = new Dictionary<int, int>();
        foreach (var gpu in pod.Gpus)
        {
            gpuUsage[gpu.Id] = 0;
        }

        foreach (var model in pod.Models.Values)
        {
            foreach (var gpuId in model.Gpu)
            {
                gpuUsage[gpuId] = gpuUsage.TryGetValue(gpuId, out var countForGpu)
                    ? countForGpu + 1
                    : 1;
            }
        }

        return gpuUsage
            .OrderBy(static pair => pair.Value)
            .Select(static pair => pair.Key)
            .Take(count)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string>? BuildGpuEnvironment(IReadOnlyList<int>? selectedGpuIds)
    {
        if (selectedGpuIds is not { Count: 1 })
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CUDA_VISIBLE_DEVICES"] = selectedGpuIds[0].ToString(CultureInfo.InvariantCulture)
        };
    }

    private static IReadOnlyDictionary<string, string>? MergeEnvironment(
        params IReadOnlyDictionary<string, string>?[] dictionaries)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dictionary in dictionaries)
        {
            if (dictionary is null)
            {
                continue;
            }

            foreach (var pair in dictionary)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return merged.Count == 0 ? null : merged;
    }

    private static IReadOnlyList<string>? ApplyConvenienceOverrides(
        IReadOnlyList<string>? baseArgs,
        string? memoryOverride,
        string? contextOverride,
        out double? memoryUtilization,
        out int? contextTokens)
    {
        memoryUtilization = null;
        contextTokens = null;
        var args = baseArgs is null
            ? new List<string>()
            : baseArgs.Where(static arg => !string.IsNullOrWhiteSpace(arg)).Select(static arg => arg.Trim()).ToList();

        if (memoryOverride is not null)
        {
            memoryUtilization = ParseMemoryUtilization(memoryOverride);
            RemoveVllmOption(args, "--gpu-memory-utilization");
            args.Add("--gpu-memory-utilization");
            args.Add(FormatDouble(memoryUtilization.Value));
        }

        if (contextOverride is not null)
        {
            contextTokens = ParseContextTokens(contextOverride);
            RemoveVllmOption(args, "--max-model-len");
            args.Add("--max-model-len");
            args.Add(contextTokens.Value.ToString(CultureInfo.InvariantCulture));
        }

        return args.Count == 0 ? null : args.ToArray();
    }

    private static void RemoveVllmOption(List<string> args, string optionName)
    {
        for (var i = args.Count - 1; i >= 0; i--)
        {
            var arg = args[i];
            if (arg.Equals(optionName, StringComparison.Ordinal) ||
                arg.StartsWith(optionName + "=", StringComparison.Ordinal))
            {
                var removeValue = arg.Equals(optionName, StringComparison.Ordinal) &&
                    i + 1 < args.Count &&
                    !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                args.RemoveAt(i);
                if (removeValue)
                {
                    args.RemoveAt(i);
                }
            }
        }
    }

    private static double ParseMemoryUtilization(string value)
    {
        var normalized = value.Trim();
        if (normalized.EndsWith('%'))
        {
            normalized = normalized[..^1];
        }

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) ||
            percent <= 0)
        {
            throw new InvalidOperationException("Invalid --memory value.");
        }

        return percent / 100.0d;
    }

    private static int ParseContextTokens(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        var mapped = normalized switch
        {
            "4k" => 4096,
            "8k" => 8192,
            "16k" => 16384,
            "32k" => 32768,
            "64k" => 65536,
            "128k" => 131072,
            _ => 0
        };
        if (mapped > 0)
        {
            return mapped;
        }

        if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens) ||
            tokens <= 0)
        {
            throw new InvalidOperationException("Invalid --context value.");
        }

        return tokens;
    }

    private static string? NormalizeOptionValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatDouble(double value) =>
        value.ToString("0.###############", CultureInfo.InvariantCulture);

    private static string BuildVllmServeCommand(
        string modelArgument,
        int port,
        string servedModelName,
        IReadOnlyList<string>? extraArgs)
    {
        var builder = new StringBuilder();
        builder
            .Append("vllm serve ")
            .Append(modelArgument)
            .Append(" --host 0.0.0.0 --port ")
            .Append(port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(" --api-key \"$PI_API_KEY\"")
            .Append(" --served-model-name ")
            .Append(ShellSingleQuote(servedModelName));

        if (extraArgs is not null)
        {
            foreach (var arg in extraArgs)
            {
                if (string.IsNullOrWhiteSpace(arg))
                {
                    continue;
                }

                builder.Append(' ').Append(ShellSingleQuote(arg.Trim()));
            }
        }

        return builder.ToString();
    }

    private static string BuildSnapshotDiscoveryCommand(string modelCachePath, string? revision)
    {
        var builder = new StringBuilder()
            .Append("model_cache_path=").Append(ShellPathValue(modelCachePath.TrimEnd('/'))).Append("; ")
            .Append("resolved_model_path=\"$model_cache_path\"; ")
            .Append("snapshots=\"$model_cache_path/snapshots\"; ");

        if (!string.IsNullOrWhiteSpace(revision))
        {
            var normalizedRevision = NormalizeRevision(revision);
            builder
                .Append("requested_revision=")
                .Append(ShellSingleQuote(normalizedRevision!))
                .Append("; ")
                .Append("if [ -d \"$snapshots\" ]; then ")
                .Append("ref_file=\"$model_cache_path/refs/$requested_revision\"; ")
                .Append("if [ -f \"$ref_file\" ]; then ")
                .Append("ref=$(head -n 1 \"$ref_file\" | tr -d '\\r\\n'); ")
                .Append("if [ -n \"$ref\" ] && [ -d \"$snapshots/$ref\" ]; then resolved_model_path=\"$snapshots/$ref\"; fi; ")
                .Append("fi; ")
                .Append("if [ \"$resolved_model_path\" = \"$model_cache_path\" ] && [ -d \"$snapshots/$requested_revision\" ]; then resolved_model_path=\"$snapshots/$requested_revision\"; fi; ")
                .Append("fi; ")
                .Append("if [ \"$resolved_model_path\" = \"$model_cache_path\" ]; then echo \"Tau Pods vLLM requested_revision=$requested_revision not found\" >&2; exit 16; fi; ");
        }
        else
        {
            builder
                .Append("ref_file=\"$model_cache_path/refs/main\"; ")
                .Append("if [ -d \"$snapshots\" ]; then ")
                .Append("if [ -f \"$ref_file\" ]; then ")
                .Append("ref=$(head -n 1 \"$ref_file\" | tr -d '\\r\\n'); ")
                .Append("if [ -n \"$ref\" ] && [ -d \"$snapshots/$ref\" ]; then resolved_model_path=\"$snapshots/$ref\"; fi; ")
                .Append("fi; ")
                .Append("if [ \"$resolved_model_path\" = \"$model_cache_path\" ]; then ")
                .Append("snapshot_count=$(find \"$snapshots\" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | wc -l | tr -d ' '); ")
                .Append("if [ \"$snapshot_count\" = \"1\" ]; then resolved_model_path=$(find \"$snapshots\" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | head -n 1); fi; ")
                .Append("fi; ")
                .Append("fi; ");
        }

        builder.Append("echo \"Tau Pods vLLM resolved_model_path=$resolved_model_path\" >&2;");
        return builder.ToString();
    }

    private static string BuildSystemdUnit(string unitName, string wrapperScriptPath, string deploymentName) =>
        "[Unit]\n" +
        $"Description=Tau Pods vLLM runner {unitName}\n" +
        "After=network-online.target\n\n" +
        "[Service]\n" +
        "Type=simple\n" +
        "ExecStartPre=/usr/bin/env mkdir -p %h/.vllm_logs\n" +
        $"ExecStart=/usr/bin/env bash -lc {ShellSingleQuote("exec " + wrapperScriptPath + " >/dev/null 2>&1")}\n" +
        "StandardOutput=null\n" +
        "StandardError=null\n" +
        "Restart=on-failure\n" +
        "RestartSec=5\n\n" +
        "[Install]\n" +
        "WantedBy=default.target";

    private static string BuildMetadataJson(
        string modelId,
        string deploymentName,
        string modelPath,
        bool usesSnapshotDiscovery,
        int port,
        string servedModelName,
        string unitName,
        string logPath,
        string runnerScriptPath,
        string wrapperScriptPath,
        bool usesPseudoTtyWrapper,
        string? revision,
        PodKnownModelConfig? knownConfig,
        int? requestedGpuCount,
        IReadOnlyList<int>? selectedGpuIds,
        string? memoryOverride,
        double? memoryUtilization,
        string? contextOverride,
        int? contextTokens)
    {
        var builder = new StringBuilder()
            .Append("{\"model\":\"").Append(EscapeJsonString(modelId))
            .Append("\",\"name\":\"").Append(EscapeJsonString(deploymentName))
            .Append("\",\"status\":\"planned-vllm\"")
            .Append(",\"modelPath\":\"").Append(EscapeJsonString(modelPath))
            .Append("\",\"usesSnapshotDiscovery\":").Append(usesSnapshotDiscovery ? "true" : "false")
            .Append(",\"port\":").Append(port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(",\"servedModelName\":\"").Append(EscapeJsonString(servedModelName))
            .Append("\",\"unit\":\"").Append(EscapeJsonString(unitName))
            .Append("\",\"logPath\":\"").Append(EscapeJsonString(logPath))
            .Append("\",\"runnerScriptPath\":\"").Append(EscapeJsonString(runnerScriptPath))
            .Append("\",\"wrapperScriptPath\":\"").Append(EscapeJsonString(wrapperScriptPath))
            .Append("\",\"usesPseudoTtyWrapper\":").Append(usesPseudoTtyWrapper ? "true" : "false")
            .Append(revision is null ? string.Empty : ",\"revision\":\"" + EscapeJsonString(revision) + "\"");

        if (requestedGpuCount is not null)
        {
            builder.Append(",\"requestedGpuCount\":").Append(requestedGpuCount.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (selectedGpuIds is not null)
        {
            builder.Append(",\"selectedGpus\":").Append(JsonIntArray(selectedGpuIds));
        }

        if (memoryOverride is not null)
        {
            builder.Append(",\"memory\":\"").Append(EscapeJsonString(memoryOverride)).Append("\"");
        }

        if (memoryUtilization is not null)
        {
            builder.Append(",\"memoryUtilization\":").Append(FormatDouble(memoryUtilization.Value));
        }

        if (contextOverride is not null)
        {
            builder.Append(",\"context\":\"").Append(EscapeJsonString(contextOverride)).Append("\"");
        }

        if (contextTokens is not null)
        {
            builder.Append(",\"contextTokens\":").Append(contextTokens.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (knownConfig is not null)
        {
            builder
                .Append(",\"knownModel\":{\"name\":\"").Append(EscapeJsonString(knownConfig.Name))
                .Append("\",\"gpuCount\":").Append(knownConfig.GpuCount.ToString(CultureInfo.InvariantCulture))
                .Append(",\"args\":").Append(JsonStringArray(knownConfig.Args));
            if (knownConfig.Environment is not null)
            {
                builder.Append(",\"env\":").Append(JsonStringObject(knownConfig.Environment));
            }
            if (!string.IsNullOrWhiteSpace(knownConfig.Notes))
            {
                builder.Append(",\"notes\":\"").Append(EscapeJsonString(knownConfig.Notes)).Append("\"");
            }

            builder.Append('}');
        }

        return builder
            .Append(",\"ts\":\"").Append(EscapeJsonString(DateTimeOffset.UtcNow.ToString("O"))).Append("\"}")
            .ToString();
    }

    private static string JsonStringArray(IReadOnlyList<string> values)
    {
        var builder = new StringBuilder("[");
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(EscapeJsonString(values[i])).Append('"');
        }

        return builder.Append(']').ToString();
    }

    private static string JsonIntArray(IReadOnlyList<int> values)
    {
        var builder = new StringBuilder("[");
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(values[i].ToString(CultureInfo.InvariantCulture));
        }

        return builder.Append(']').ToString();
    }

    private static string JsonStringObject(IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder("{");
        var first = true;
        foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder
                .Append('"').Append(EscapeJsonString(pair.Key)).Append("\":\"")
                .Append(EscapeJsonString(pair.Value)).Append('"');
        }

        return builder.Append('}').ToString();
    }

    private static string GetModelsPath(PodDefinition pod) =>
        string.IsNullOrWhiteSpace(pod.ModelsPath) ? DefaultModelsPath : pod.ModelsPath.Trim();

    private static string BuildModelPath(string modelsPath, string modelId) =>
        $"{modelsPath.TrimEnd('/')}/{NormalizeModelCacheDirectoryName(modelId)}";

    private static string NormalizeModelCacheDirectoryName(string modelId)
    {
        var trimmed = modelId.Trim();
        var builder = new StringBuilder("models--".Length + trimmed.Length);
        builder.Append("models--");
        foreach (var ch in trimmed)
        {
            if (ch == '/')
            {
                builder.Append("--");
            }
            else if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('-');
            }
        }

        return builder.ToString().TrimEnd('-');
    }

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

    private static string NormalizeEnvironmentKey(string value)
    {
        var trimmed = value.Trim();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        var normalized = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "ENV";
        }

        return char.IsAsciiLetter(normalized[0]) || normalized[0] == '_'
            ? normalized
            : $"ENV_{normalized}";
    }

    private static string EscapeJsonString(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string ShellSingleQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string ShellPathValue(string path)
    {
        const string homePrefix = "$HOME/";
        if (!path.StartsWith(homePrefix, StringComparison.Ordinal))
        {
            return ShellSingleQuote(path);
        }

        return "\"$HOME/" + ShellDoubleQuoteContent(path[homePrefix.Length..]) + "\"";
    }

    private static string ShellDoubleQuoteContent(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);
}
