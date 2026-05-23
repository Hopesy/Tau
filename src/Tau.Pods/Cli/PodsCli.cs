using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tau.Ai.Observability;
using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Cli;

public static class PodsCli
{
    private const string DefaultConfigPath = "tau.pods.json";

    public static async Task<int> RunAsync(
        string[] args,
        PodExecService? execService = null,
        PodVllmCommandPlanner? vllmPlanner = null)
    {
        var store = new PodsConfigStore();
        var validator = new PodsConfigValidator();
        using var logSink = CreateLogSink();
        var probeService = new PodProbeService(logSink: logSink);
        execService ??= new PodExecService();
        var lifecycleService = new PodLifecycleService(execService);
        var modelService = new PodModelService(execService);
        vllmPlanner ??= new PodVllmCommandPlanner();

        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var path = args.Length > 1 ? args[1] : DefaultConfigPath;

        return command switch
        {
            "init" => Init(path, store),
            "list" => List(path, store, validator),
            "validate" => Validate(path, store, validator),
            "status" => Status(path, store, validator),
            "probe" => await ProbeAsync(path, store, validator, probeService).ConfigureAwait(false),
            "exec" => await ExecAsync(args, path, store, validator, execService).ConfigureAwait(false),
            "health" => await HealthAsync(path, store, validator, lifecycleService).ConfigureAwait(false),
            "deploy" => await DeployAsync(args, path, store, validator, lifecycleService).ConfigureAwait(false),
            "stop" => await StopAsync(args, path, store, validator, lifecycleService).ConfigureAwait(false),
            "restart" => await RestartAsync(args, path, store, validator, lifecycleService).ConfigureAwait(false),
            "logs" => await LogsAsync(args, path, store, validator, lifecycleService).ConfigureAwait(false),
            "deployments" => await DeploymentsAsync(args, path, store, validator, lifecycleService).ConfigureAwait(false),
            "model" => await ModelAsync(args, store, validator, modelService).ConfigureAwait(false),
            "vllm" => Vllm(args, store, validator, vllmPlanner),
            _ => Unknown(command)
        };
    }

    private static JsonlTauLogSink? CreateLogSink()
    {
        try
        {
            return JsonlTauLogSink.FromEnvironment();
        }
        catch
        {
            return null;
        }
    }

    private static int Init(string path, PodsConfigStore store)
    {
        store.Save(path, store.CreateSample());
        Console.WriteLine($"Created sample pod config at {Path.GetFullPath(path)}");
        return 0;
    }

    private static int List(string path, PodsConfigStore store, PodsConfigValidator validator)
    {
        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        foreach (var pod in config.Pods)
        {
            Console.WriteLine($"{pod.Id} | provider={pod.Provider} | model={pod.Model} | region={pod.Region} | enabled={pod.Enabled}");
        }

        return 0;
    }

    private static int Validate(string path, PodsConfigStore store, PodsConfigValidator validator)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Config not found: {Path.GetFullPath(path)}");
            return 1;
        }

        var config = store.Load(path);
        var errors = validator.Validate(config);
        if (errors.Count == 0)
        {
            Console.WriteLine("Config is valid.");
            return 0;
        }

        foreach (var error in errors)
        {
            Console.Error.WriteLine(error);
        }

        return 1;
    }

    private static int Status(string path, PodsConfigStore store, PodsConfigValidator validator)
    {
        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var enabled = config.Pods.Count(pod => pod.Enabled);
        var disabled = config.Pods.Count - enabled;
        Console.WriteLine($"pods={config.Pods.Count} enabled={enabled} disabled={disabled}");

        foreach (var pod in config.Pods)
        {
            var transport = !string.IsNullOrWhiteSpace(pod.Endpoint) ? $"endpoint={pod.Endpoint}" : $"ssh={pod.SshHost}:{pod.SshPort ?? 22}";
            Console.WriteLine($"- {pod.Id}: {transport}");
        }

        return 0;
    }

    private static async Task<int> ProbeAsync(string path, PodsConfigStore store, PodsConfigValidator validator, PodProbeService probeService)
    {
        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var results = await probeService.ProbeAsync(config.Pods.Where(pod => pod.Enabled)).ConfigureAwait(false);
        if (results.Count == 0)
        {
            Console.WriteLine("No enabled pods to probe.");
            return 0;
        }

        foreach (var result in results.OrderBy(result => result.PodId, StringComparer.OrdinalIgnoreCase))
        {
            var latency = result.Latency is null ? "n/a" : $"{result.Latency.Value.TotalMilliseconds:F0}ms";
            var location = result.Endpoint ?? (result.Host is null ? string.Empty : $"{result.Host}:{result.Port}");
            Console.WriteLine($"{result.PodId} | ok={result.Success} | transport={result.Transport} | latency={latency} | target={location} | {result.Summary}");
        }

        return results.All(result => result.Success) ? 0 : 1;
    }

    private static async Task<int> ExecAsync(string[] args, string path, PodsConfigStore store, PodsConfigValidator validator, PodExecService execService)
    {
        if (!TryParseTargetCommand(args, minValueCount: 1, "Usage: exec [path] <pod-id> <command>", out var parsed))
        {
            return 1;
        }

        path = parsed.ConfigPath;
        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var podId = parsed.PodId;
        var command = string.Join(' ', parsed.Values);
        var pod = config.Pods.FirstOrDefault(p => p.Id.Equals(podId, StringComparison.OrdinalIgnoreCase));
        if (pod is null)
        {
            Console.Error.WriteLine($"Pod not found: {podId}");
            return 1;
        }

        var result = await execService.ExecuteAsync(pod, command).ConfigureAwait(false);
        Console.WriteLine($"{result.PodId} | ok={result.Success} | transport={result.Transport} | target={result.Target} | exit={result.ExitCode} | {result.Summary}");
        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            Console.WriteLine("[stdout]");
            Console.WriteLine(result.StdOut.TrimEnd());
        }
        if (!string.IsNullOrWhiteSpace(result.StdErr))
        {
            Console.WriteLine("[stderr]");
            Console.WriteLine(result.StdErr.TrimEnd());
        }

        return result.Success ? 0 : 1;
    }

    private static async Task<int> HealthAsync(string path, PodsConfigStore store, PodsConfigValidator validator, PodLifecycleService lifecycleService)
    {
        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var enabled = config.Pods.Where(pod => pod.Enabled).ToList();
        if (enabled.Count == 0)
        {
            Console.WriteLine("No enabled pods.");
            return 0;
        }

        var tasks = enabled.Select(pod => lifecycleService.HealthAsync(pod));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var result in results.OrderBy(r => r.PodId, StringComparer.OrdinalIgnoreCase))
        {
            var latency = result.Latency is null ? "" : $" | latency={result.Latency.Value.TotalMilliseconds:F0}ms";
            Console.WriteLine($"{result.PodId} | healthy={result.Healthy} | transport={result.Transport}{latency} | {result.Summary}");
        }

        return results.All(r => r.Healthy) ? 0 : 1;
    }

    private static async Task<int> DeployAsync(string[] args, string path, PodsConfigStore store, PodsConfigValidator validator, PodLifecycleService lifecycleService)
    {
        if (!TryParseTargetCommand(args, minValueCount: 1, "Usage: deploy [path] <pod-id> <model-id> [name]", out var parsed))
        {
            return 1;
        }

        path = parsed.ConfigPath;
        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var podId = parsed.PodId;
        var modelId = parsed.Values[0];
        var name = parsed.Values.Count > 1 ? parsed.Values[1] : null;
        var pod = config.Pods.FirstOrDefault(p => p.Id.Equals(podId, StringComparison.OrdinalIgnoreCase));
        if (pod is null)
        {
            Console.Error.WriteLine($"Pod not found: {podId}");
            return 1;
        }

        var result = await lifecycleService.DeployAsync(pod, modelId, name).ConfigureAwait(false);
        Console.WriteLine($"{result.PodId} | ok={result.Success} | {result.Summary}");
        return result.Success ? 0 : 1;
    }

    private static async Task<int> StopAsync(string[] args, string path, PodsConfigStore store, PodsConfigValidator validator, PodLifecycleService lifecycleService)
    {
        if (!TryParseTargetCommand(args, minValueCount: 1, "Usage: stop [path] <pod-id> <deployment-name>", out var parsed))
        {
            return 1;
        }

        path = parsed.ConfigPath;
        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var podId = parsed.PodId;
        var deploymentName = parsed.Values[0];
        var pod = config.Pods.FirstOrDefault(p => p.Id.Equals(podId, StringComparison.OrdinalIgnoreCase));
        if (pod is null)
        {
            Console.Error.WriteLine($"Pod not found: {podId}");
            return 1;
        }

        var result = await lifecycleService.StopAsync(pod, deploymentName).ConfigureAwait(false);
        Console.WriteLine($"{result.PodId} | ok={result.Success} | {result.Summary}");
        return result.Success ? 0 : 1;
    }

    private static async Task<int> RestartAsync(string[] args, string path, PodsConfigStore store, PodsConfigValidator validator, PodLifecycleService lifecycleService)
    {
        if (!TryParseTargetCommand(args, minValueCount: 1, "Usage: restart [path] <pod-id> <deployment-name>", out var parsed))
        {
            return 1;
        }

        path = parsed.ConfigPath;
        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var podId = parsed.PodId;
        var deploymentName = parsed.Values[0];
        var pod = config.Pods.FirstOrDefault(p => p.Id.Equals(podId, StringComparison.OrdinalIgnoreCase));
        if (pod is null)
        {
            Console.Error.WriteLine($"Pod not found: {podId}");
            return 1;
        }

        var result = await lifecycleService.RestartAsync(pod, deploymentName).ConfigureAwait(false);
        Console.WriteLine($"{result.PodId} | ok={result.Success} | {result.Summary}");
        return result.Success ? 0 : 1;
    }

    private static async Task<int> LogsAsync(string[] args, string path, PodsConfigStore store, PodsConfigValidator validator, PodLifecycleService lifecycleService)
    {
        if (!TryParseTargetCommand(args, minValueCount: 1, "Usage: logs [path] <pod-id> <deployment-name> [tail]", out var parsed))
        {
            return 1;
        }

        path = parsed.ConfigPath;
        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var podId = parsed.PodId;
        var deploymentName = parsed.Values[0];
        var tail = 100;
        if (parsed.Values.Count > 1)
        {
            if (!int.TryParse(parsed.Values[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out tail) || tail <= 0)
            {
                Console.Error.WriteLine($"Invalid tail value: {parsed.Values[1]}");
                return 1;
            }
        }

        var pod = config.Pods.FirstOrDefault(p => p.Id.Equals(podId, StringComparison.OrdinalIgnoreCase));
        if (pod is null)
        {
            Console.Error.WriteLine($"Pod not found: {podId}");
            return 1;
        }

        var result = await lifecycleService.LogsAsync(pod, deploymentName, tail).ConfigureAwait(false);
        Console.WriteLine($"{result.PodId} | ok={result.Success} | {result.Summary}");
        if (!string.IsNullOrEmpty(result.Output))
        {
            Console.WriteLine();
            Console.Write(result.Output);
            if (!result.Output.EndsWith('\n'))
            {
                Console.WriteLine();
            }
        }

        return result.Success ? 0 : 1;
    }

    private static async Task<int> DeploymentsAsync(string[] args, string path, PodsConfigStore store, PodsConfigValidator validator, PodLifecycleService lifecycleService)
    {
        var configPath = DefaultConfigPath;
        string? podId = null;

        if (args.Length >= 3 && LooksLikeConfigPath(args[1]))
        {
            configPath = args[1];
            podId = args[2];
        }
        else if (args.Length >= 2)
        {
            podId = args[1];
        }
        else
        {
            Console.Error.WriteLine("Usage: deployments [path] <pod-id>");
            return 1;
        }

        path = configPath;
        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var pod = config.Pods.FirstOrDefault(p => p.Id.Equals(podId, StringComparison.OrdinalIgnoreCase));
        if (pod is null)
        {
            Console.Error.WriteLine($"Pod not found: {podId}");
            return 1;
        }

        var result = await lifecycleService.ListDeploymentsAsync(pod).ConfigureAwait(false);
        Console.WriteLine($"{result.PodId} | ok={result.Success} | {result.Summary}");
        foreach (var deployment in result.Deployments)
        {
            var model = deployment.Model ?? "-";
            var status = deployment.Status ?? "-";
            var ts = deployment.Timestamp ?? "-";
            Console.WriteLine($"- {deployment.Name} | model={model} | status={status} | ts={ts}");
        }

        return result.Success ? 0 : 1;
    }

    private static async Task<int> ModelAsync(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodModelService modelService)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: model <list|pull|remove|status> [path] <pod-id> [model-id]");
            return 1;
        }

        var subcommand = args[1].ToLowerInvariant();
        return subcommand switch
        {
            "list" => await ModelListAsync(args, store, validator, modelService).ConfigureAwait(false),
            "pull" => await ModelPullAsync(args, store, validator, modelService).ConfigureAwait(false),
            "remove" => await ModelRemoveAsync(args, store, validator, modelService).ConfigureAwait(false),
            "status" => await ModelStatusAsync(args, store, validator, modelService).ConfigureAwait(false),
            _ => UnknownModelSubcommand(subcommand)
        };
    }

    private static int Vllm(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodVllmCommandPlanner planner)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: vllm <plan> [--json] [path] <pod-id> <model-id> [deployment-name]");
            return 1;
        }

        var subcommand = args[1].ToLowerInvariant();
        return subcommand switch
        {
            "plan" => VllmPlan(args, store, validator, planner),
            _ => UnknownVllmSubcommand(subcommand)
        };
    }

    private static int VllmPlan(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodVllmCommandPlanner planner)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 2, out var positionalArgs);
        if (!TryParseModelCommand(positionalArgs, minValueCount: 1, "Usage: vllm plan [--json] [path] <pod-id> <model-id> [deployment-name]", out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var modelId = parsed.Values[0];
        var deploymentName = parsed.Values.Count > 1 ? parsed.Values[1] : null;
        var plan = planner.PlanServe(pod, new PodVllmServeOptions(modelId, deploymentName));

        if (jsonOutput)
        {
            PrintVllmPlanJson(pod, plan);
            return 0;
        }

        PrintVllmPlanText(pod, plan);
        return 0;
    }

    private static void PrintVllmPlanText(PodDefinition pod, PodVllmServePlan plan)
    {
        Console.WriteLine($"pod={pod.Id}");
        Console.WriteLine($"deployment={plan.DeploymentName}");
        Console.WriteLine($"model={plan.ModelId}");
        Console.WriteLine($"modelPath={plan.ModelPath}");
        Console.WriteLine($"port={plan.Port}");
        Console.WriteLine($"servedModel={plan.ServedModelName}");
        Console.WriteLine($"unit={plan.UnitName}");
        Console.WriteLine("[serve-command]");
        Console.WriteLine(plan.ServeCommand);
        Console.WriteLine("[systemd-unit]");
        Console.WriteLine(plan.SystemdUnit);
        Console.WriteLine("[metadata-json]");
        Console.WriteLine(plan.MetadataJson);
        Console.WriteLine("[remote-command]");
        Console.WriteLine(plan.RemoteCommand);
    }

    private static void PrintVllmPlanJson(PodDefinition pod, PodVllmServePlan plan)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("pod", pod.Id);
            writer.WriteString("deployment", plan.DeploymentName);
            writer.WriteString("model", plan.ModelId);
            writer.WriteString("modelPath", plan.ModelPath);
            writer.WriteNumber("port", plan.Port);
            writer.WriteString("servedModel", plan.ServedModelName);
            writer.WriteString("unit", plan.UnitName);
            writer.WriteString("serveCommand", plan.ServeCommand);
            writer.WriteString("systemdUnit", plan.SystemdUnit);
            writer.WritePropertyName("metadata");
            using (var metadata = JsonDocument.Parse(plan.MetadataJson))
            {
                metadata.RootElement.WriteTo(writer);
            }
            writer.WriteString("metadataJson", plan.MetadataJson);
            writer.WriteString("remoteCommand", plan.RemoteCommand);
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static async Task<int> ModelListAsync(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodModelService modelService)
    {
        if (!TryParseModelCommand(args, minValueCount: 0, "Usage: model list [path] <pod-id>", out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var result = await modelService.ListAsync(pod).ConfigureAwait(false);
        Console.WriteLine($"{result.PodId} | ok={result.Success} | {result.Summary}");
        foreach (var model in result.Models)
        {
            Console.WriteLine($"- {model.ModelId} | cache={model.CacheDirectory}");
        }

        return result.Success ? 0 : 1;
    }

    private static async Task<int> ModelPullAsync(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodModelService modelService)
    {
        if (!TryParseModelCommand(args, minValueCount: 1, "Usage: model pull [path] <pod-id> <model-id>", out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var modelId = parsed.Values[0];
        var result = await modelService.PullAsync(pod, modelId).ConfigureAwait(false);
        Console.WriteLine($"{result.PodId} | ok={result.Success} | operation={result.Operation} | model={result.ModelId} | {result.Summary}");
        return result.Success ? 0 : 1;
    }

    private static async Task<int> ModelRemoveAsync(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodModelService modelService)
    {
        if (!TryParseModelCommand(args, minValueCount: 1, "Usage: model remove [path] <pod-id> <model-id>", out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var modelId = parsed.Values[0];
        var result = await modelService.RemoveAsync(pod, modelId).ConfigureAwait(false);
        Console.WriteLine($"{result.PodId} | ok={result.Success} | operation={result.Operation} | model={result.ModelId} | {result.Summary}");
        return result.Success ? 0 : 1;
    }

    private static async Task<int> ModelStatusAsync(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodModelService modelService)
    {
        if (!TryParseModelCommand(args, minValueCount: 1, "Usage: model status [path] <pod-id> <model-id>", out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var modelId = parsed.Values[0];
        var result = await modelService.StatusAsync(pod, modelId).ConfigureAwait(false);
        Console.WriteLine($"{result.PodId} | ok={result.Success} | present={result.Present} | model={result.ModelId} | {result.Summary}");
        return result.Success ? 0 : 1;
    }

    private static PodsConfig? LoadOrReport(string path, PodsConfigStore store, PodsConfigValidator validator, out int exitCode)
    {
        exitCode = 0;
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Config not found: {Path.GetFullPath(path)}");
            exitCode = 1;
            return null;
        }

        var config = store.Load(path);
        var errors = validator.Validate(config);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Console.Error.WriteLine(error);
            }
            exitCode = 1;
            return null;
        }

        return config;
    }

    private static PodDefinition? LoadPodOrReport(
        string path,
        string podId,
        PodsConfigStore store,
        PodsConfigValidator validator,
        out int exitCode)
    {
        var config = LoadOrReport(path, store, validator, out exitCode);
        if (config is null) return null;

        var pod = config.Pods.FirstOrDefault(p => p.Id.Equals(podId, StringComparison.OrdinalIgnoreCase));
        if (pod is null)
        {
            Console.Error.WriteLine($"Pod not found: {podId}");
            exitCode = 1;
            return null;
        }

        return pod;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 1;
    }

    private static int UnknownModelSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"Unknown model subcommand: {subcommand}");
        Console.Error.WriteLine("Usage: model <list|pull|remove|status> [path] <pod-id> [model-id]");
        return 1;
    }

    private static int UnknownVllmSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"Unknown vllm subcommand: {subcommand}");
        Console.Error.WriteLine("Usage: vllm <plan> [--json] [path] <pod-id> <model-id> [deployment-name]");
        return 1;
    }

    private static bool IsHelp(string arg) => arg is "help" or "--help" or "-h";

    private static bool TryConsumeFlag(string[] args, string flag, int startIndex, out string[] positionalArgs)
    {
        var found = false;
        var values = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (i >= startIndex && args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                continue;
            }

            values.Add(args[i]);
        }

        positionalArgs = values.ToArray();
        return found;
    }

    private static bool TryParseTargetCommand(
        string[] args,
        int minValueCount,
        string usage,
        out TargetCommandArguments parsed)
    {
        parsed = new TargetCommandArguments(DefaultConfigPath, string.Empty, []);
        if (args.Length < 2 + minValueCount)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        var targetIndex = 1;
        var configPath = DefaultConfigPath;
        if (args.Length >= 3 + minValueCount && LooksLikeConfigPath(args[1]))
        {
            configPath = args[1];
            targetIndex = 2;
        }

        var values = args.Skip(targetIndex + 1).ToArray();
        if (values.Length < minValueCount)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        parsed = new TargetCommandArguments(configPath, args[targetIndex], values);
        return true;
    }

    private static bool TryParseModelCommand(
        string[] args,
        int minValueCount,
        string usage,
        out TargetCommandArguments parsed)
    {
        parsed = new TargetCommandArguments(DefaultConfigPath, string.Empty, []);
        if (args.Length < 3 + minValueCount)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        var targetIndex = 2;
        var configPath = DefaultConfigPath;
        if (args.Length >= 4 + minValueCount && LooksLikeConfigPath(args[2]))
        {
            configPath = args[2];
            targetIndex = 3;
        }

        var values = args.Skip(targetIndex + 1).ToArray();
        if (values.Length < minValueCount)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        parsed = new TargetCommandArguments(configPath, args[targetIndex], values);
        return true;
    }

    private static bool LooksLikeConfigPath(string value) =>
        File.Exists(value) ||
        value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase) ||
        value.Contains('/') ||
        value.Contains('\\');

    private static void PrintHelp()
    {
        Console.WriteLine("Tau.Pods commands:");
        Console.WriteLine("  init [path]                    Create a sample tau.pods.json");
        Console.WriteLine("  list [path]                    List configured pods");
        Console.WriteLine("  validate [path]                Validate pod config");
        Console.WriteLine("  status [path]                  Print enabled/disabled and transport summary");
        Console.WriteLine("  probe [path]                   Probe enabled pod endpoints or ssh/tcp targets");
        Console.WriteLine("  health [path]                  Check health of all enabled pods");
        Console.WriteLine("  exec [path] <id> <command>     Execute a remote command on an ssh pod");
        Console.WriteLine("  deploy [path] <id> <model>     Deploy a model to a pod");
        Console.WriteLine("  stop [path] <id> <name>        Stop a deployment on a pod");
        Console.WriteLine("  restart [path] <id> <name>     Restart a deployment on a pod");
        Console.WriteLine("  logs [path] <id> <name> [tail] Fetch deployment logs from a pod");
        Console.WriteLine("  deployments [path] <id>        List deployments on a pod");
        Console.WriteLine("  model list [path] <id>         List cached models on an ssh pod");
        Console.WriteLine("  model pull [path] <id> <model> Pull a Hugging Face model on an ssh pod");
        Console.WriteLine("  model remove [path] <id> <model> Remove a cached model from an ssh pod");
        Console.WriteLine("  model status [path] <id> <model> Check whether a model is cached");
        Console.WriteLine("  vllm plan [--json] [path] <id> <model> [name] Print a plan-only vLLM serve command");
    }

    private sealed record TargetCommandArguments(string ConfigPath, string PodId, IReadOnlyList<string> Values);
}
