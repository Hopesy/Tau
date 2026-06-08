using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Tau.Ai.Observability;
using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Cli;

public static class PodsCli
{
    private const string UpstreamConfigDirectoryEnvVar = "PI_CONFIG_DIR";
    private const string UpstreamDefaultConfigDirectoryName = ".pi";
    private const string UpstreamConfigFileName = "pods.json";
    private const string VllmExtraArgsSentinel = "--vllm";

    public static async Task<int> RunAsync(
        string[] args,
        PodExecService? execService = null,
        PodVllmCommandPlanner? vllmPlanner = null,
        PodVllmOrchestrationService? vllmService = null,
        PodSetupPlanner? setupPlanner = null,
        PodSetupService? setupService = null)
    {
        var store = new PodsConfigStore();
        var validator = new PodsConfigValidator();
        using var logSink = CreateLogSink();
        var probeService = new PodProbeService(logSink: logSink);
        execService ??= new PodExecService();
        var lifecycleService = new PodLifecycleService(execService, logSink: logSink);
        var modelService = new PodModelService(execService, logSink: logSink);
        vllmPlanner ??= new PodVllmCommandPlanner();
        vllmService ??= new PodVllmOrchestrationService(execService, vllmPlanner, logSink: logSink);
        setupPlanner ??= new PodSetupPlanner();
        setupService ??= new PodSetupService(setupPlanner);

        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var path = args.Length > 1 ? args[1] : GetDefaultConfigPath();

        return command switch
        {
            "init" => Init(args, store),
            "pods" => await PodsCommandAsync(args, store, validator, setupPlanner, setupService).ConfigureAwait(false),
            "list" => await UpstreamListAsync(args, path, store, validator, lifecycleService).ConfigureAwait(false),
            "validate" => Validate(args, store, validator),
            "status" => Status(args, store, validator),
            "active" => Active(args, store, validator),
            "remove" => Remove(args, store, validator),
            "probe" => await ProbeAsync(args, store, validator, probeService).ConfigureAwait(false),
            "exec" => await ExecAsync(args, path, store, validator, execService).ConfigureAwait(false),
            "ssh" => await SshAsync(args, path, store, validator, execService).ConfigureAwait(false),
            "shell" => await ShellAsync(args, store, validator, execService).ConfigureAwait(false),
            "agent" => AgentAsync(args, store, validator),
            "health" => await HealthAsync(args, store, validator, lifecycleService).ConfigureAwait(false),
            "deploy" => await DeployAsync(args, path, store, validator, lifecycleService).ConfigureAwait(false),
            "start" => await StartAsync(args, store, validator, vllmPlanner, vllmService).ConfigureAwait(false),
            "stop" => await StopCompatAsync(args, path, store, validator, lifecycleService, vllmService).ConfigureAwait(false),
            "restart" => await RestartAsync(args, path, store, validator, lifecycleService).ConfigureAwait(false),
            "logs" => await LogsAsync(args, store, validator, lifecycleService).ConfigureAwait(false),
            "deployments" => await DeploymentsAsync(args, path, store, validator, lifecycleService).ConfigureAwait(false),
            "setup" => await Setup(args, store, validator, setupPlanner, setupService).ConfigureAwait(false),
            "model" => await ModelAsync(args, store, validator, modelService).ConfigureAwait(false),
            "vllm" => await Vllm(args, store, validator, vllmPlanner, vllmService).ConfigureAwait(false),
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

    private static async Task<int> PodsCommandAsync(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodSetupPlanner setupPlanner,
        PodSetupService setupService)
    {
        if (args.Length > 1 && IsHelp(args[1]))
        {
            PrintPodsHelp();
            return 0;
        }

        if (args.Length == 1 || args[1].StartsWith("-", StringComparison.Ordinal) || LooksLikeConfigPath(args[1]))
        {
            return List(RewriteCommand(args, "list", startIndex: 1), store, validator);
        }

        var subcommand = args[1].ToLowerInvariant();
        return subcommand switch
        {
            "setup" => await Setup(RewriteCommand(args, "setup", startIndex: 2), store, validator, setupPlanner, setupService).ConfigureAwait(false),
            "active" => Active(RewriteCommand(args, "active", startIndex: 2), store, validator),
            "remove" => Remove(RewriteCommand(args, "remove", startIndex: 2), store, validator),
            _ => UnknownPodsSubcommand(subcommand)
        };
    }

    private static async Task<int> StartAsync(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodVllmCommandPlanner planner,
        PodVllmOrchestrationService service)
    {
        if (args.Length < 2 || IsHelp(args[1]))
        {
            PrintKnownModels();
            return 0;
        }

        if (!HasStringOptionBeforeVllmSentinel(args, "--name", startIndex: 2))
        {
            Console.Error.WriteLine("--name is required");
            Console.Error.WriteLine("Usage: start <model-id> --name <deployment-name> [--config path] [--pod pod-id] [--memory percent] [--context size] [--gpus n] [--vllm <args...>]");
            return 1;
        }

        return await VllmDeploy(RewriteCommand(args, "vllm", "deploy", startIndex: 1), store, validator, planner, service).ConfigureAwait(false);
    }

    private static async Task<int> UpstreamListAsync(
        string[] args,
        string path,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodLifecycleService lifecycleService)
    {
        return await DeploymentsAsync(RewriteCommand(args, "deployments", startIndex: 1), path, store, validator, lifecycleService).ConfigureAwait(false);
    }

    private static async Task<int> StopCompatAsync(
        string[] args,
        string path,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodLifecycleService lifecycleService,
        PodVllmOrchestrationService vllmService)
    {
        if (LooksLikeLegacyStopCommand(args))
        {
            return await StopAsync(args, path, store, validator, lifecycleService).ConfigureAwait(false);
        }

        const string usage = "Usage: stop [--json] [--config path] [--pod pod-id] [<deployment-name>]";
        if (!TryParseStopCompatCommand(args, usage, out var parsed))
        {
            return 1;
        }

        if (parsed.Values.Count == 0)
        {
            return await StopAllCompatAsync(parsed, store, validator, lifecycleService, vllmService).ConfigureAwait(false);
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var result = await vllmService.StopAsync(pod, parsed.Values[0]).ConfigureAwait(false);
        RemoveConfiguredDeploymentOnSuccess(store, parsed.ConfigPath, pod.Id, result);
        if (parsed.JsonOutput)
        {
            PrintVllmOperationJson(result);
        }
        else
        {
            PrintVllmOperationText(result);
        }

        return result.Success ? 0 : 1;
    }

    private static async Task<int> StopAllCompatAsync(
        StopCompatCommandArguments parsed,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodLifecycleService lifecycleService,
        PodVllmOrchestrationService vllmService)
    {
        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var deployments = await lifecycleService.ListDeploymentsAsync(pod).ConfigureAwait(false);
        var names = deployments.Deployments
            .Select(deployment => deployment.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        var results = new List<PodVllmOperationResult>(names.Length);
        if (deployments.Success)
        {
            foreach (var name in names)
            {
                results.Add(await vllmService.StopAsync(pod, name).ConfigureAwait(false));
            }
        }

        RemoveSuccessfulConfiguredDeployments(store, parsed.ConfigPath, pod.Id, results);

        var success = deployments.Success && results.All(result => result.Success);
        var summary = !deployments.Success
            ? $"Could not list deployments on {pod.Id}: {deployments.Summary}"
            : names.Length == 0
                ? $"No deployments running on {pod.Id}."
                : success
                    ? $"Stopped {results.Count} deployment(s) on {pod.Id}: {string.Join(", ", results.Select(result => result.DeploymentName))}."
                    : $"Stopped {results.Count(result => result.Success)}/{results.Count} deployment(s) on {pod.Id}.";

        if (parsed.JsonOutput)
        {
            PrintVllmStopAllJson(pod, deployments, results, success, summary);
        }
        else
        {
            PrintVllmStopAllText(pod, deployments, results, success, summary);
        }

        return success ? 0 : 1;
    }

    private static string[] RewriteCommand(string[] args, string command, int startIndex)
    {
        var rewritten = new string[1 + Math.Max(0, args.Length - startIndex)];
        rewritten[0] = command;
        if (args.Length > startIndex)
        {
            Array.Copy(args, startIndex, rewritten, 1, args.Length - startIndex);
        }

        return rewritten;
    }

    private static string[] RewriteCommand(string[] args, string command, string subcommand, int startIndex)
    {
        var rewritten = new string[2 + Math.Max(0, args.Length - startIndex)];
        rewritten[0] = command;
        rewritten[1] = subcommand;
        if (args.Length > startIndex)
        {
            Array.Copy(args, startIndex, rewritten, 2, args.Length - startIndex);
        }

        return rewritten;
    }

    private static bool HasStringOptionBeforeVllmSentinel(string[] args, string option, int startIndex)
    {
        for (var i = startIndex; i < args.Length; i++)
        {
            if (args[i].Equals(VllmExtraArgsSentinel, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static bool LooksLikeLegacyStopCommand(string[] args)
    {
        if (args.Any(arg => arg.Equals("--config", StringComparison.OrdinalIgnoreCase) || arg.Equals("--pod", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        TryConsumeTargetJsonFlag(args, out var positionalArgs);
        var values = positionalArgs.Skip(1).ToArray();
        return values.Length >= 2;
    }

    private static bool TryParseStopCompatCommand(
        string[] args,
        string usage,
        out StopCompatCommandArguments parsed)
    {
        parsed = new StopCompatCommandArguments(GetDefaultConfigPath(), null, [], JsonOutput: false);
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 1, out var jsonArgs);
        var configPath = ConsumeStringOption(jsonArgs, "--config", startIndex: 1, out var podArgs, out var optionError);
        var hasExplicitConfig = configPath is not null;
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            return false;
        }

        var podId = ConsumeStringOption(podArgs, "--pod", startIndex: 1, out var positionalArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            return false;
        }

        configPath ??= GetDefaultConfigPath();
        var valueStart = 1;
        if (!hasExplicitConfig &&
            positionalArgs.Length > valueStart &&
            LooksLikeConfigPath(positionalArgs[valueStart]))
        {
            configPath = positionalArgs[valueStart];
            valueStart++;
        }

        var remaining = positionalArgs.Skip(valueStart).ToArray();
        IReadOnlyList<string> values;
        if (podId is not null)
        {
            if (remaining.Length > 1)
            {
                Console.Error.WriteLine(usage);
                return false;
            }

            values = remaining;
        }
        else
        {
            switch (remaining.Length)
            {
                case 0:
                case 1:
                    values = remaining;
                    break;
                case 2:
                    podId = remaining[0];
                    values = [remaining[1]];
                    break;
                default:
                    Console.Error.WriteLine(usage);
                    return false;
            }
        }

        parsed = new StopCompatCommandArguments(configPath, podId, values, jsonOutput);
        return true;
    }

    private static void PrintKnownModels()
    {
        Console.WriteLine("Known models:");
        foreach (var model in new PodKnownModelRegistry().GetKnownModels())
        {
            Console.WriteLine($"  {model}");
        }
        Console.WriteLine();
        Console.WriteLine("Usage: start <model-id> --name <deployment-name> [--pod pod-id] [--memory percent] [--context size] [--gpus n] [--vllm <args...>]");
    }

    private static PodAgentPromptPlan BuildAgentPromptPlan(
        PodDefinition pod,
        string modelName,
        PodConfiguredModel modelConfig,
        IReadOnlyList<string> userArgs)
    {
        var host = ResolveAgentHost(pod);
        var baseUrl = $"http://{host}:{modelConfig.Port.ToString(CultureInfo.InvariantCulture)}/v1";
        var api = modelConfig.Model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase)
            ? "responses"
            : "completions";
        var apiKey = Environment.GetEnvironmentVariable("PI_API_KEY");
        var apiKeySource = string.IsNullOrWhiteSpace(apiKey) ? "dummy" : "PI_API_KEY";
        var systemPrompt = BuildPodAgentSystemPrompt(Environment.CurrentDirectory);
        var agentArgs = new List<string>
        {
            "--base-url",
            baseUrl,
            "--model",
            modelConfig.Model,
            "--api-key",
            string.IsNullOrWhiteSpace(apiKey) ? "dummy" : apiKey!,
            "--api",
            api,
            "--system-prompt",
            systemPrompt
        };
        agentArgs.AddRange(userArgs);

        return new PodAgentPromptPlan(
            pod.Id,
            modelName,
            modelConfig.Model,
            modelConfig.Port,
            baseUrl,
            api,
            apiKeySource,
            systemPrompt,
            agentArgs);
    }

    private static string ResolveAgentHost(PodDefinition pod)
    {
        if (!string.IsNullOrWhiteSpace(pod.SshCommand))
        {
            var tokens = pod.SshCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var token in tokens)
            {
                var atIndex = token.IndexOf('@', StringComparison.Ordinal);
                if (atIndex >= 0 && atIndex + 1 < token.Length)
                {
                    return token[(atIndex + 1)..].Trim('\'', '"');
                }
            }
        }

        return string.IsNullOrWhiteSpace(pod.SshHost) ? "localhost" : pod.SshHost.Trim();
    }

    private static string BuildPodAgentSystemPrompt(string currentDirectory) =>
        "You help the user understand and navigate the codebase in the current working directory.\n\n" +
        "You can read files, list directories, and execute shell commands via the respective tools.\n\n" +
        "Do not output file contents you read via the read_file tool directly, unless asked to.\n\n" +
        "Do not output markdown tables as part of your responses.\n\n" +
        "Keep your responses concise and relevant to the user's request.\n\n" +
        "File paths you output must include line numbers where possible, e.g. \"src/index.ts:10-20\" for lines 10 to 20 in src/index.ts.\n\n" +
        $"Current working directory: {currentDirectory}";

    private static void PrintAgentPromptPlan(PodAgentPromptPlan plan)
    {
        var redactedArgs = RedactAgentArguments(plan.AgentArgs);
        Console.WriteLine($"pod={plan.PodId} | ok=False | operation=agent | model={plan.ModelName} | upstreamModel={plan.UpstreamModel} | baseUrl={plan.BaseUrl} | api={plan.Api} | apiKeySource={plan.ApiKeySource} | summary=agent prompt execution is not implemented");
        Console.WriteLine("[agent-args]");
        Console.WriteLine(string.Join(' ', redactedArgs.Select(QuoteArgumentForDisplay)));
    }

    private static IReadOnlyList<string> RedactAgentArguments(IReadOnlyList<string> args)
    {
        var redacted = new string[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            redacted[i] = i > 0 && args[i - 1].Equals("--api-key", StringComparison.OrdinalIgnoreCase)
                ? "[redacted]"
                : args[i];
        }

        return redacted;
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

    private static string GetDefaultConfigPath()
    {
        return ResolveDefaultConfigPath(Environment.GetEnvironmentVariable(UpstreamConfigDirectoryEnvVar), GetUserHomeDirectory());
    }

    internal static string ResolveDefaultConfigPath(string? configDir, string homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDir))
        {
            configDir = Path.Combine(homeDirectory, UpstreamDefaultConfigDirectoryName);
        }

        return Path.Combine(configDir, UpstreamConfigFileName);
    }

    private static string GetUserHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        home = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrWhiteSpace(home) ? "." : home;
    }

    private static int Init(string[] args, PodsConfigStore store)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 1, out var positionalArgs);
        if (!TryParseConfigCommand(positionalArgs, "Usage: init [--json] [path]", out var path))
        {
            return 1;
        }

        store.Save(path, store.CreateSample());
        var fullPath = Path.GetFullPath(path);
        if (jsonOutput)
        {
            PrintInitJson(fullPath);
            return 0;
        }

        Console.WriteLine($"Created sample pod config at {fullPath}");
        return 0;
    }

    private static int List(string[] args, PodsConfigStore store, PodsConfigValidator validator)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 1, out var positionalArgs);
        if (!TryParseConfigCommand(positionalArgs, "Usage: list [--json] [path]", out var path))
        {
            return 1;
        }

        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        if (jsonOutput)
        {
            PrintListJson(config);
            return 0;
        }

        foreach (var pod in config.Pods)
        {
            var active = config.ActivePodId is not null && config.ActivePodId.Equals(pod.Id, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"{pod.Id} | active={active} | provider={pod.Provider} | model={pod.Model} | region={pod.Region} | enabled={pod.Enabled}");
        }

        return 0;
    }

    private static int Validate(string[] args, PodsConfigStore store, PodsConfigValidator validator)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 1, out var positionalArgs);
        if (!TryParseConfigCommand(positionalArgs, "Usage: validate [--json] [path]", out var path))
        {
            return 1;
        }

        var config = store.Load(path);
        var errors = validator.Validate(config);
        if (jsonOutput)
        {
            PrintValidateJson(errors.Count == 0, errors);
            return errors.Count == 0 ? 0 : 1;
        }

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

    private static int Status(string[] args, PodsConfigStore store, PodsConfigValidator validator)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 1, out var positionalArgs);
        if (!TryParseConfigCommand(positionalArgs, "Usage: status [--json] [path]", out var path))
        {
            return 1;
        }

        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var enabled = config.Pods.Count(pod => pod.Enabled);
        var disabled = config.Pods.Count - enabled;
        if (jsonOutput)
        {
            PrintStatusJson(config, enabled, disabled);
            return 0;
        }

        Console.WriteLine($"pods={config.Pods.Count} enabled={enabled} disabled={disabled} active={config.ActivePodId ?? "-"}");

        foreach (var pod in config.Pods)
        {
            var transport = !string.IsNullOrWhiteSpace(pod.Endpoint) ? $"endpoint={pod.Endpoint}" : $"ssh={pod.SshHost}:{pod.SshPort ?? 22}";
            Console.WriteLine($"- {pod.Id}: {transport}");
        }

        return 0;
    }

    private static int Active(string[] args, PodsConfigStore store, PodsConfigValidator validator)
    {
        var jsonOutput = TryConsumeTargetJsonFlag(args, out var positionalArgs);
        if (!TryParseTargetCommand(positionalArgs, minValueCount: 0, "Usage: active [--json] [path] <pod-id>", out var parsed))
        {
            return 1;
        }

        var config = LoadOrReport(parsed.ConfigPath, store, validator, out var exitCode);
        if (config is null) return exitCode;

        if (!store.SetActivePod(config, parsed.PodId))
        {
            Console.Error.WriteLine($"Pod not found: {parsed.PodId}");
            return 1;
        }

        store.Save(parsed.ConfigPath, config);
        var fullPath = Path.GetFullPath(parsed.ConfigPath);
        if (jsonOutput)
        {
            PrintActiveJson(config.ActivePodId!, fullPath);
            return 0;
        }

        Console.WriteLine($"Active pod set to {config.ActivePodId} in {fullPath}");
        return 0;
    }

    private static int Remove(string[] args, PodsConfigStore store, PodsConfigValidator validator)
    {
        var jsonOutput = TryConsumeTargetJsonFlag(args, out var positionalArgs);
        if (!TryParseTargetCommand(positionalArgs, minValueCount: 0, "Usage: remove [--json] [path] <pod-id>", out var parsed))
        {
            return 1;
        }

        var config = LoadOrReport(parsed.ConfigPath, store, validator, out var exitCode);
        if (config is null) return exitCode;

        if (!store.RemovePod(config, parsed.PodId))
        {
            Console.Error.WriteLine($"Pod not found: {parsed.PodId}");
            return 1;
        }

        store.Save(parsed.ConfigPath, config);
        var fullPath = Path.GetFullPath(parsed.ConfigPath);
        if (jsonOutput)
        {
            PrintRemoveJson(parsed.PodId, config.ActivePodId, fullPath);
            return 0;
        }

        Console.WriteLine($"Removed pod {parsed.PodId} from {fullPath}");
        if (config.ActivePodId is null)
        {
            Console.WriteLine("Active pod cleared.");
        }

        return 0;
    }

    private static async Task<int> ProbeAsync(string[] args, PodsConfigStore store, PodsConfigValidator validator, PodProbeService probeService)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 1, out var positionalArgs);
        if (!TryParseConfigCommand(positionalArgs, "Usage: probe [--json] [path]", out var path))
        {
            return 1;
        }

        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var results = await probeService.ProbeAsync(config.Pods.Where(pod => pod.Enabled)).ConfigureAwait(false);
        if (jsonOutput)
        {
            PrintProbeJson(results);
            return results.All(result => result.Success) ? 0 : 1;
        }

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
        return await RunRemoteCommandAsync(
            args,
            path,
            store,
            validator,
            execService,
            "Usage: exec [--json] [--config path] [--pod pod-id] <command> or exec [--json] [path] <pod-id> <command>").ConfigureAwait(false);
    }

    private static async Task<int> SshAsync(string[] args, string path, PodsConfigStore store, PodsConfigValidator validator, PodExecService execService)
    {
        return await RunRemoteCommandAsync(
            args,
            path,
            store,
            validator,
            execService,
            "Usage: ssh [--json] [--config path] [--pod pod-id] <command> or ssh [--json] [path] <pod-id> <command>").ConfigureAwait(false);
    }

    private static async Task<int> ShellAsync(string[] args, PodsConfigStore store, PodsConfigValidator validator, PodExecService execService)
    {
        const string usage = "Usage: shell [--json] [--config path] [--pod pod-id] [pod-id]";
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 1, out var shellArgs);
        if (!TryParseShellCommand(shellArgs, usage, out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        if (!jsonOutput)
        {
            Console.WriteLine($"Connecting to pod '{pod.Id}'...");
        }

        var result = await execService.OpenShellAsync(pod).ConfigureAwait(false);
        if (jsonOutput)
        {
            PrintExecJson(result);
            return result.Success ? 0 : 1;
        }

        Console.WriteLine($"{result.PodId} | ok={result.Success} | operation=shell | transport={result.Transport} | target={result.Target} | exit={result.ExitCode} | {result.Summary}");
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

    private static int AgentAsync(string[] args, PodsConfigStore store, PodsConfigValidator validator)
    {
        const string usage = "Usage: agent [--config path] [--pod pod-id] <model-name> [message/options...]";
        if (!TryParseAgentCommand(args, usage, out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        if (!pod.Models.TryGetValue(parsed.ModelName, out var modelConfig))
        {
            Console.Error.WriteLine($"Model '{parsed.ModelName}' not found on pod '{pod.Id}'");
            return 1;
        }

        if (modelConfig.Port <= 0)
        {
            Console.Error.WriteLine($"Model '{parsed.ModelName}' on pod '{pod.Id}' does not have a valid port.");
            return 1;
        }

        var plan = BuildAgentPromptPlan(pod, parsed.ModelName, modelConfig, parsed.UserArgs);
        PrintAgentPromptPlan(plan);
        Console.Error.WriteLine("Agent prompt execution is not implemented in Tau.Pods yet.");
        return 1;
    }

    private static async Task<int> RunRemoteCommandAsync(
        string[] args,
        string path,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodExecService execService,
        string usage)
    {
        var jsonOutput = TryConsumeTargetJsonFlag(args, out var positionalArgs);
        if (!TryParseExecCommand(positionalArgs, usage, out var parsed))
        {
            return 1;
        }

        path = parsed.ConfigPath;
        var pod = LoadPodOrActiveReport(path, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var command = string.Join(' ', parsed.CommandParts);
        var result = await execService.ExecuteAsync(pod, command).ConfigureAwait(false);
        if (jsonOutput)
        {
            PrintExecJson(result);
            return result.Success ? 0 : 1;
        }

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

    private static async Task<int> HealthAsync(string[] args, PodsConfigStore store, PodsConfigValidator validator, PodLifecycleService lifecycleService)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 1, out var positionalArgs);
        if (!TryParseConfigCommand(positionalArgs, "Usage: health [--json] [path]", out var path))
        {
            return 1;
        }

        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var enabled = config.Pods.Where(pod => pod.Enabled).ToList();
        if (jsonOutput)
        {
            var jsonResults = enabled.Count == 0
                ? Array.Empty<PodHealthResult>()
                : await Task.WhenAll(enabled.Select(pod => lifecycleService.HealthAsync(pod))).ConfigureAwait(false);
            PrintHealthJson(jsonResults);
            return jsonResults.All(result => result.Healthy) ? 0 : 1;
        }

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
        var jsonOutput = TryConsumeTargetJsonFlag(args, out var positionalArgs);
        if (!TryParseTargetCommand(positionalArgs, minValueCount: 1, "Usage: deploy [--json] [path] <pod-id> <model-id> [name]", out var parsed))
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
        if (jsonOutput)
        {
            PrintLifecycleOperationJson(result);
            return result.Success ? 0 : 1;
        }

        Console.WriteLine($"{result.PodId} | ok={result.Success} | {result.Summary}");
        return result.Success ? 0 : 1;
    }

    private static async Task<int> StopAsync(string[] args, string path, PodsConfigStore store, PodsConfigValidator validator, PodLifecycleService lifecycleService)
    {
        var jsonOutput = TryConsumeTargetJsonFlag(args, out var positionalArgs);
        if (!TryParseTargetCommand(positionalArgs, minValueCount: 1, "Usage: stop [--json] [path] <pod-id> <deployment-name>", out var parsed))
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
        if (jsonOutput)
        {
            PrintLifecycleOperationJson(result, "stop");
            return result.Success ? 0 : 1;
        }

        Console.WriteLine($"{result.PodId} | ok={result.Success} | {result.Summary}");
        return result.Success ? 0 : 1;
    }

    private static async Task<int> RestartAsync(string[] args, string path, PodsConfigStore store, PodsConfigValidator validator, PodLifecycleService lifecycleService)
    {
        var jsonOutput = TryConsumeTargetJsonFlag(args, out var positionalArgs);
        if (!TryParseTargetCommand(positionalArgs, minValueCount: 1, "Usage: restart [--json] [path] <pod-id> <deployment-name>", out var parsed))
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
        if (jsonOutput)
        {
            PrintLifecycleOperationJson(result, "restart");
            return result.Success ? 0 : 1;
        }

        Console.WriteLine($"{result.PodId} | ok={result.Success} | {result.Summary}");
        return result.Success ? 0 : 1;
    }

    private static async Task<int> LogsAsync(string[] args, PodsConfigStore store, PodsConfigValidator validator, PodLifecycleService lifecycleService)
    {
        const string usage = "Usage: logs [--json] [--follow|-f] [--config path] [--pod pod-id] <deployment-name> [tail] or logs [--json] [--follow|-f] [path] <pod-id> <deployment-name> [tail]";
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 1, out var jsonArgs);
        var follow = TryConsumeFlag(jsonArgs, "--follow", startIndex: 1, out var followArgs);
        if (TryConsumeFlag(followArgs, "-f", startIndex: 1, out var positionalArgs))
        {
            follow = true;
        }

        if (jsonOutput && follow)
        {
            Console.Error.WriteLine("logs --follow cannot be combined with --json because the log stream inherits stdout/stderr.");
            Console.Error.WriteLine(usage);
            return 1;
        }

        if (!TryParseLogsCommand(positionalArgs, usage, out var parsed))
        {
            return 1;
        }

        var config = LoadOrReport(parsed.ConfigPath, store, validator, out var exitCode);
        if (config is null) return exitCode;

        if (!TryResolveLogsTarget(config, parsed, out var podId, out var deploymentName, out var tail, out var tailSpecified))
        {
            return 1;
        }

        if (follow && tailSpecified)
        {
            Console.Error.WriteLine("Tail value is not supported with logs --follow; it streams the upstream tail -f log file.");
            Console.Error.WriteLine(usage);
            return 1;
        }

        var pod = config.Pods.FirstOrDefault(p => p.Id.Equals(podId, StringComparison.OrdinalIgnoreCase));
        if (pod is null)
        {
            Console.Error.WriteLine($"Pod not found: {podId}");
            return 1;
        }

        if (follow)
        {
            Console.WriteLine($"Streaming logs for '{deploymentName}' on pod '{pod.Id}'...");
            Console.WriteLine("Press Ctrl+C to stop");
            Console.WriteLine();

            var streamResult = await lifecycleService.FollowLogsAsync(pod, deploymentName).ConfigureAwait(false);
            if (!streamResult.Success)
            {
                Console.WriteLine($"{streamResult.PodId} | ok=False | {streamResult.Summary}");
                if (!string.IsNullOrWhiteSpace(streamResult.StdErr))
                {
                    Console.WriteLine("[stderr]");
                    Console.WriteLine(streamResult.StdErr.TrimEnd());
                }
            }

            return streamResult.Success ? 0 : 1;
        }

        var result = await lifecycleService.LogsAsync(pod, deploymentName, tail).ConfigureAwait(false);
        if (jsonOutput)
        {
            PrintLogsJson(result, deploymentName, tail);
            return result.Success ? 0 : 1;
        }

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
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 1, out var positionalArgs);
        if (!TryParseTargetCommandWithOptionalPod(positionalArgs, valueStart: 1, minValueCount: 0, "Usage: deployments [--json] [--config path] [--pod pod-id]", out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var result = await lifecycleService.ListDeploymentsAsync(pod).ConfigureAwait(false);
        if (jsonOutput)
        {
            PrintDeploymentsJson(result);
            return result.Success ? 0 : 1;
        }

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
            Console.Error.WriteLine("Usage: model <list|pull|remove|status> [--json] [--config path] [--pod pod-id] [--revision rev] [--snapshot rev] [model-id]");
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

    private static async Task<int> Setup(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodSetupPlanner planner,
        PodSetupService service)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: setup <plan|run> [--json] [path] <pod-id> OR setup [--json] [--mount <command>] [--models-path <path>] [--vllm release|nightly|gpt-oss] [path] <pod-id> \"ssh [-p port] <host>\"");
            return 1;
        }

        var subcommand = args[1].ToLowerInvariant();
        return subcommand switch
        {
            "plan" => SetupPlan(args, store, validator, planner),
            "run" => await SetupRun(args, store, validator, service).ConfigureAwait(false),
            _ => SetupRegister(args, store, validator)
        };
    }

    private static int SetupRegister(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator)
    {
        const string usage = "Usage: setup [--json] [--mount <command>] [--models-path <path>] [--vllm release|nightly|gpt-oss] [path] <pod-id> \"ssh [-p port] <host>\"";

        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 1, out var setupArgs);
        var mountCommand = ConsumeStringOption(setupArgs, "--mount", startIndex: 1, out var modelsPathArgs, out var optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            return 1;
        }

        var modelsPath = ConsumeStringOption(modelsPathArgs, "--models-path", startIndex: 1, out var vllmArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            return 1;
        }

        var vllmVersion = ConsumeStringOption(vllmArgs, "--vllm", startIndex: 1, out var positionalArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            return 1;
        }

        if (!TryParseSetupRegistrationCommand(positionalArgs, usage, out var parsed))
        {
            return 1;
        }

        if (!TryParseSimpleSshTarget(parsed.SshCommand, out var sshTarget, out var sshError))
        {
            Console.Error.WriteLine(sshError);
            return 1;
        }

        string normalizedVllmVersion;
        try
        {
            normalizedVllmVersion = PodSetupPlanner.NormalizeVllmVersion(vllmVersion);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var resolvedModelsPath = string.IsNullOrWhiteSpace(modelsPath)
            ? ExtractModelsPathFromMount(mountCommand) ?? PodSetupPlanner.DefaultModelsPath
            : modelsPath;

        var config = LoadExistingOrCreate(parsed.ConfigPath, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var pod = new PodDefinition
        {
            Id = parsed.PodId,
            Provider = "ssh",
            Model = "unassigned",
            Region = "registered",
            SshCommand = parsed.SshCommand,
            SshHost = sshTarget.Host,
            SshPort = sshTarget.Port,
            ModelsPath = resolvedModelsPath,
            VllmVersion = normalizedVllmVersion,
            Enabled = true,
            Tags = ["registered"]
        };

        store.AddOrUpdatePod(config, pod);
        var errors = validator.Validate(config);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Console.Error.WriteLine(error);
            }

            return 1;
        }

        store.Save(parsed.ConfigPath, config);
        var fullPath = Path.GetFullPath(parsed.ConfigPath);
        if (jsonOutput)
        {
            PrintSetupRegistrationJson(pod, config.ActivePodId, fullPath);
            return 0;
        }

        var active = config.ActivePodId is not null && config.ActivePodId.Equals(pod.Id, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"{pod.Id} | registered=True | active={active} | ssh={pod.SshHost}:{pod.SshPort} | modelsPath={pod.ModelsPath} | vllm={pod.VllmVersion} | configPath={fullPath}");
        Console.WriteLine("setupExecuted=False");
        return 0;
    }

    private static int SetupPlan(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodSetupPlanner planner)
    {
        const string usage = "Usage: setup plan [--json] [--mount <command>] [--models-path <path>] [--vllm release|nightly|gpt-oss] [path] <pod-id>";

        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 2, out var setupArgs);
        var mountCommand = ConsumeStringOption(setupArgs, "--mount", startIndex: 2, out var modelsPathArgs, out var optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            return 1;
        }

        var modelsPath = ConsumeStringOption(modelsPathArgs, "--models-path", startIndex: 2, out var vllmArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            return 1;
        }

        var vllmVersion = ConsumeStringOption(vllmArgs, "--vllm", startIndex: 2, out var positionalArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            return 1;
        }

        if (!TryParseModelCommand(positionalArgs, minValueCount: 0, usage, out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        PodSetupPlan plan;
        try
        {
            plan = planner.Plan(pod, new PodSetupPlanOptions(mountCommand, modelsPath, vllmVersion));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        if (jsonOutput)
        {
            PrintSetupPlanJson(plan);
            return 0;
        }

        PrintSetupPlanText(plan);
        return 0;
    }

    private static async Task<int> SetupRun(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodSetupService service)
    {
        const string usage = "Usage: setup run [--json] [--script <path>] [--mount <command>] [--models-path <path>] [--vllm release|nightly|gpt-oss] [path] <pod-id>";

        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 2, out var setupArgs);
        var scriptPath = ConsumeStringOption(setupArgs, "--script", startIndex: 2, out var mountArgs, out var optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            return 1;
        }

        var mountCommand = ConsumeStringOption(mountArgs, "--mount", startIndex: 2, out var modelsPathArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            return 1;
        }

        var modelsPath = ConsumeStringOption(modelsPathArgs, "--models-path", startIndex: 2, out var vllmArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            return 1;
        }

        var vllmVersion = ConsumeStringOption(vllmArgs, "--vllm", startIndex: 2, out var positionalArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            return 1;
        }

        if (!TryParseModelCommand(positionalArgs, minValueCount: 0, usage, out var parsed))
        {
            return 1;
        }

        var config = LoadOrReport(parsed.ConfigPath, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var pod = config.Pods.FirstOrDefault(p => p.Id.Equals(parsed.PodId, StringComparison.OrdinalIgnoreCase));
        if (pod is null)
        {
            Console.Error.WriteLine($"Pod not found: {parsed.PodId}");
            return 1;
        }

        var result = await service.RunAsync(
            pod,
            new PodSetupRunOptions(mountCommand, modelsPath, vllmVersion, scriptPath)).ConfigureAwait(false);

        var configUpdated = false;
        var fullConfigPath = Path.GetFullPath(parsed.ConfigPath);
        if (result.Success)
        {
            store.ApplySetupResult(config, parsed.PodId, result);
            store.Save(parsed.ConfigPath, config);
            configUpdated = true;
        }

        if (jsonOutput)
        {
            PrintSetupRunJson(result, configUpdated, fullConfigPath);
        }
        else
        {
            PrintSetupRunText(result, configUpdated, fullConfigPath);
        }

        return result.Success ? 0 : 1;
    }

    private static async Task<int> Vllm(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodVllmCommandPlanner planner,
        PodVllmOrchestrationService service)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: vllm <plan|preflight|deploy|status|health|stop|rollback> [--json] [--config path] [--pod pod-id] <model-id|deployment-name> [--name deployment-name] [--revision rev] [--snapshot rev] [--gpus n] [--memory percent] [--context size] [--prefetch] [--vllm <args...>]");
            return 1;
        }

        var subcommand = args[1].ToLowerInvariant();
        return subcommand switch
        {
            "plan" => VllmPlan(args, store, validator, planner),
            "preflight" => await VllmPreflight(args, store, validator, service).ConfigureAwait(false),
            "deploy" => await VllmDeploy(args, store, validator, planner, service).ConfigureAwait(false),
            "status" => await VllmStatus(args, store, validator, service).ConfigureAwait(false),
            "health" => await VllmHealth(args, store, validator, service).ConfigureAwait(false),
            "stop" => await VllmStop(args, store, validator, service).ConfigureAwait(false),
            "rollback" => await VllmRollback(args, store, validator, service).ConfigureAwait(false),
            _ => UnknownVllmSubcommand(subcommand)
        };
    }

    private static int VllmPlan(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodVllmCommandPlanner planner)
    {
        const string usage = "Usage: vllm plan [--json] [--config path] [--pod pod-id] <model-id> [--name deployment-name] [--revision rev] [--snapshot rev] [--gpus n] [--memory percent] [--context size] [--vllm <args...>]";
        if (!TrySplitVllmExtraArgs(args, usage, out var cliArgs, out var extraArgs))
        {
            return 1;
        }

        var jsonOutput = TryConsumeFlag(cliArgs, "--json", startIndex: 2, out var positionalArgs);
        if (!TryParseVllmServeCommand(positionalArgs, usage, out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var options = new PodVllmServeOptions(
            parsed.ModelId,
            parsed.DeploymentName,
            ExtraArgs: extraArgs,
            Revision: parsed.Revision,
            RequestedGpuCount: parsed.RequestedGpuCount,
            Memory: parsed.Memory,
            Context: parsed.Context);
        if (!TryPlanVllmServe(planner, pod, options, out var plan))
        {
            return 1;
        }

        if (jsonOutput)
        {
            PrintVllmPlanJson(pod, plan!);
            return 0;
        }

        PrintVllmPlanText(pod, plan!);
        return 0;
    }

    private static async Task<int> VllmPreflight(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodVllmOrchestrationService service)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 2, out var positionalArgs);
        if (!TryParseVllmServeCommand(positionalArgs, "Usage: vllm preflight [--json] [--config path] [--pod pod-id] <model-id> [--name deployment-name] [--revision rev] [--snapshot rev]", out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var result = await service.PreflightAsync(pod, new PodVllmServeOptions(parsed.ModelId, parsed.DeploymentName, Revision: parsed.Revision)).ConfigureAwait(false);
        if (jsonOutput)
        {
            PrintVllmPreflightJson(result);
        }
        else
        {
            PrintVllmPreflightText(result);
        }

        return result.Success ? 0 : 1;
    }

    private static async Task<int> VllmDeploy(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodVllmCommandPlanner planner,
        PodVllmOrchestrationService service)
    {
        const string usage = "Usage: vllm deploy [--json] [--no-health] [--prefetch] [--health-attempts n] [--health-backoff-ms n] [--config path] [--pod pod-id] <model-id> [--name deployment-name] [--revision rev] [--snapshot rev] [--gpus n] [--memory percent] [--context size] [--vllm <args...>]";
        if (!TrySplitVllmExtraArgs(args, usage, out var cliArgs, out var extraArgs))
        {
            return 1;
        }

        var jsonOutput = TryConsumeFlag(cliArgs, "--json", startIndex: 2, out var deployArgs);
        var prefetch = TryConsumeFlag(deployArgs, "--prefetch", startIndex: 2, out var prefetchArgs);
        var skipHealth = TryConsumeFlag(prefetchArgs, "--no-health", startIndex: 2, out var healthArgs);
        var healthAttempts = ConsumeIntOption(healthArgs, "--health-attempts", startIndex: 2, defaultValue: 12, out var backoffArgs, out var optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            return 1;
        }

        var healthBackoffMs = ConsumeIntOption(backoffArgs, "--health-backoff-ms", startIndex: 2, defaultValue: 5000, out var positionalArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            return 1;
        }

        if (!TryParseVllmServeCommand(positionalArgs, usage, out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var options = new PodVllmServeOptions(
            parsed.ModelId,
            parsed.DeploymentName,
            ExtraArgs: extraArgs,
            Revision: parsed.Revision,
            WaitForHealth: !skipHealth,
            HealthAttempts: healthAttempts,
            HealthBackoffMilliseconds: healthBackoffMs,
            Prefetch: prefetch,
            RequestedGpuCount: parsed.RequestedGpuCount,
            Memory: parsed.Memory,
            Context: parsed.Context);
        if (!TryPlanVllmServe(planner, pod, options, out var plan))
        {
            return 1;
        }
        if (plan is null)
        {
            return 1;
        }

        if (pod.Models.ContainsKey(plan.DeploymentName))
        {
            var duplicate = CreateDuplicateDeploymentResult(pod.Id, plan.DeploymentName);
            if (jsonOutput)
            {
                PrintVllmOperationJson(duplicate);
            }
            else
            {
                Console.Error.WriteLine(duplicate.Summary);
            }

            return 1;
        }

        var result = await service.DeployAsync(pod, options).ConfigureAwait(false);
        SaveConfiguredDeploymentOnSuccess(store, parsed.ConfigPath, pod.Id, result);
        if (jsonOutput)
        {
            PrintVllmOperationJson(result);
        }
        else
        {
            PrintVllmOperationText(result);
        }

        return result.Success ? 0 : 1;
    }

    private static async Task<int> VllmHealth(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodVllmOrchestrationService service)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 2, out var healthArgs);
        var healthAttempts = ConsumeIntOption(healthArgs, "--health-attempts", startIndex: 2, defaultValue: 1, out var backoffArgs, out var optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            return 1;
        }

        var healthBackoffMs = ConsumeIntOption(backoffArgs, "--health-backoff-ms", startIndex: 2, defaultValue: 0, out var positionalArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            return 1;
        }

        if (!TryParseTargetCommandWithOptionalPod(positionalArgs, valueStart: 2, minValueCount: 1, "Usage: vllm health [--json] [--health-attempts n] [--health-backoff-ms n] [--config path] [--pod pod-id] <deployment-name>", out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var result = await service.HealthAsync(
            pod,
            parsed.Values[0],
            healthAttempts,
            healthBackoffMs,
            includeMetadata: jsonOutput).ConfigureAwait(false);
        if (jsonOutput)
        {
            PrintVllmHealthJson(result);
        }
        else
        {
            PrintVllmHealthText(result);
        }

        return result.Success ? 0 : 1;
    }

    private static async Task<int> VllmStatus(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodVllmOrchestrationService service)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 2, out var positionalArgs);
        if (!TryParseTargetCommandWithOptionalPod(positionalArgs, valueStart: 2, minValueCount: 1, "Usage: vllm status [--json] [--config path] [--pod pod-id] <deployment-name>", out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var result = await service.StatusAsync(pod, parsed.Values[0], includeMetadata: jsonOutput).ConfigureAwait(false);
        if (jsonOutput)
        {
            PrintVllmStatusJson(result);
        }
        else
        {
            PrintVllmStatusText(result);
        }

        return result.Success ? 0 : 1;
    }

    private static async Task<int> VllmStop(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodVllmOrchestrationService service)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 2, out var positionalArgs);
        if (!TryParseTargetCommandWithOptionalPod(positionalArgs, valueStart: 2, minValueCount: 1, "Usage: vllm stop [--json] [--config path] [--pod pod-id] <deployment-name>", out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var result = await service.StopAsync(pod, parsed.Values[0]).ConfigureAwait(false);
        RemoveConfiguredDeploymentOnSuccess(store, parsed.ConfigPath, pod.Id, result);
        if (jsonOutput)
        {
            PrintVllmOperationJson(result);
        }
        else
        {
            PrintVllmOperationText(result);
        }

        return result.Success ? 0 : 1;
    }

    private static void SaveConfiguredDeploymentOnSuccess(
        PodsConfigStore store,
        string configPath,
        string podId,
        PodVllmOperationResult result)
    {
        if (!result.Success || result.Plan is null)
        {
            return;
        }

        var config = store.Load(configPath);
        store.ApplyVllmDeploymentResult(config, podId, result);
        store.Save(configPath, config);
    }

    private static PodVllmOperationResult CreateDuplicateDeploymentResult(string podId, string deploymentName) =>
        new(
            podId,
            false,
            "deploy",
            deploymentName,
            $"Model '{deploymentName}' already exists on pod '{podId}'.",
            string.Empty,
            -1,
            string.Empty,
            string.Empty,
            FailureKind: "model-already-exists");

    private static void RemoveConfiguredDeploymentOnSuccess(
        PodsConfigStore store,
        string configPath,
        string podId,
        PodVllmOperationResult result)
    {
        if (!result.Success)
        {
            return;
        }

        var config = store.Load(configPath);
        if (store.RemoveConfiguredModel(config, podId, result.DeploymentName))
        {
            store.Save(configPath, config);
        }
    }

    private static void RemoveSuccessfulConfiguredDeployments(
        PodsConfigStore store,
        string configPath,
        string podId,
        IReadOnlyList<PodVllmOperationResult> results)
    {
        if (results.Count == 0 || !results.Any(static result => result.Success))
        {
            return;
        }

        var config = store.Load(configPath);
        var changed = false;
        foreach (var result in results.Where(static result => result.Success))
        {
            changed |= store.RemoveConfiguredModel(config, podId, result.DeploymentName);
        }

        if (changed)
        {
            store.Save(configPath, config);
        }
    }

    private static async Task<int> VllmRollback(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodVllmOrchestrationService service)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 2, out var positionalArgs);
        if (!TryParseTargetCommandWithOptionalPod(positionalArgs, valueStart: 2, minValueCount: 1, "Usage: vllm rollback [--json] [--config path] [--pod pod-id] <deployment-name>", out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var result = await service.RollbackAsync(pod, parsed.Values[0], CancellationToken.None).ConfigureAwait(false);
        if (jsonOutput)
        {
            PrintVllmRollbackJson(result);
        }
        else
        {
            PrintVllmRollbackText(result);
        }

        return result.Success ? 0 : 1;
    }

    private static void PrintVllmPlanText(PodDefinition pod, PodVllmServePlan plan)
    {
        Console.WriteLine($"pod={pod.Id}");
        Console.WriteLine($"deployment={plan.DeploymentName}");
        Console.WriteLine($"model={plan.ModelId}");
        if (!string.IsNullOrWhiteSpace(plan.Revision))
        {
            Console.WriteLine($"revision={plan.Revision}");
        }
        Console.WriteLine($"modelPath={plan.ModelPath}");
        Console.WriteLine($"port={plan.Port}");
        Console.WriteLine($"servedModel={plan.ServedModelName}");
        if (plan.RequestedGpuCount is not null)
        {
            Console.WriteLine($"requestedGpuCount={plan.RequestedGpuCount}");
        }
        if (plan.SelectedGpuIds is not null)
        {
            Console.WriteLine($"selectedGpus={(plan.SelectedGpuIds.Count == 0 ? "none" : string.Join(",", plan.SelectedGpuIds))}");
        }
        if (!string.IsNullOrWhiteSpace(plan.MemoryOverride))
        {
            Console.WriteLine($"memory={plan.MemoryOverride}");
            Console.WriteLine($"memoryUtilization={plan.MemoryUtilization?.ToString("0.###############", CultureInfo.InvariantCulture)}");
        }
        if (!string.IsNullOrWhiteSpace(plan.ContextOverride))
        {
            Console.WriteLine($"context={plan.ContextOverride}");
            Console.WriteLine($"contextTokens={plan.ContextTokens}");
        }
        if (!string.IsNullOrWhiteSpace(plan.KnownModelName))
        {
            Console.WriteLine($"knownModel={plan.KnownModelName}");
            Console.WriteLine($"knownModelGpuCount={plan.KnownModelGpuCount}");
            if (!string.IsNullOrWhiteSpace(plan.KnownModelNotes))
            {
                Console.WriteLine($"knownModelNotes={plan.KnownModelNotes}");
            }
        }
        Console.WriteLine($"unit={plan.UnitName}");
        Console.WriteLine($"logPath={plan.LogPath}");
        Console.WriteLine($"runnerScriptPath={plan.RunnerScriptPath}");
        Console.WriteLine($"wrapperScriptPath={plan.WrapperScriptPath}");
        Console.WriteLine($"usesPseudoTtyWrapper={plan.UsesPseudoTtyWrapper}");
        Console.WriteLine("[serve-command]");
        Console.WriteLine(plan.ServeCommand);
        Console.WriteLine("[systemd-unit]");
        Console.WriteLine(plan.SystemdUnit);
        Console.WriteLine("[metadata-json]");
        Console.WriteLine(plan.MetadataJson);
        Console.WriteLine("[remote-command]");
        Console.WriteLine(plan.RemoteCommand);
    }

    private static void PrintSetupPlanText(PodSetupPlan plan)
    {
        Console.WriteLine($"{plan.PodId} | setup-plan | vllm={plan.VllmVersion} | modelsPath={plan.ModelsPath} | hfTokenConfigured={plan.HfTokenConfigured} | piApiKeyConfigured={plan.PiApiKeyConfigured}");
        Console.WriteLine($"ssh={plan.SshHost}:{plan.SshPort}");
        Console.WriteLine($"scriptRemotePath={plan.ScriptRemotePath}");
        if (!string.IsNullOrWhiteSpace(plan.MountCommand))
        {
            Console.WriteLine($"mount={plan.MountCommand}");
        }

        Console.WriteLine("[steps]");
        foreach (var step in plan.Steps)
        {
            Console.WriteLine($"- {step}");
        }

        Console.WriteLine("[setup-command]");
        Console.WriteLine(plan.SetupCommand);
    }

    private static void PrintSetupPlanJson(PodSetupPlan plan)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            WriteSetupPlanObject(writer, plan);
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintSetupRunText(PodSetupRunResult result, bool configUpdated, string configPath)
    {
        Console.WriteLine($"{result.PodId} | ok={result.Success} | operation=setup | vllm={result.Plan.VllmVersion} | gpuCount={result.Gpus.Count} | configUpdated={configUpdated} | configPath={configPath} | {result.Summary}");
        Console.WriteLine($"script={result.ScriptPath ?? "-"}");
        Console.WriteLine("[setup-command]");
        Console.WriteLine(result.Plan.SetupCommand);
        Console.WriteLine("[steps]");
        foreach (var step in result.Steps)
        {
            Console.WriteLine($"- {step.Name} | ok={step.Success} | exit={step.ExitCode} | {step.Summary}");
            Console.WriteLine($"  command={step.DisplayCommand}");
            if (!string.IsNullOrWhiteSpace(step.StdOut))
            {
                Console.WriteLine("  [stdout]");
                Console.WriteLine(IndentBlock(step.StdOut.TrimEnd(), "  "));
            }
            if (!string.IsNullOrWhiteSpace(step.StdErr))
            {
                Console.WriteLine("  [stderr]");
                Console.WriteLine(IndentBlock(step.StdErr.TrimEnd(), "  "));
            }
        }

        if (result.Gpus.Count > 0)
        {
            Console.WriteLine("[gpus]");
            foreach (var gpu in result.Gpus)
            {
                Console.WriteLine($"- {gpu.Id} | {gpu.Name} | {gpu.Memory}");
            }
        }
    }

    private static void PrintSetupRunJson(PodSetupRunResult result, bool configUpdated, string configPath)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("podId", result.PodId);
            writer.WriteBoolean("success", result.Success);
            writer.WriteString("summary", result.Summary);
            writer.WriteString("scriptPath", result.ScriptPath);
            writer.WriteBoolean("configUpdated", configUpdated);
            writer.WriteString("configPath", configPath);
            writer.WritePropertyName("plan");
            WriteSetupPlanObject(writer, result.Plan);
            writer.WritePropertyName("steps");
            writer.WriteStartArray();
            foreach (var step in result.Steps)
            {
                WriteSetupExecutionStepObject(writer, step);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("gpus");
            writer.WriteStartArray();
            foreach (var gpu in result.Gpus)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", gpu.Id);
                writer.WriteString("name", gpu.Name);
                writer.WriteString("memory", gpu.Memory);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void WriteSetupPlanObject(Utf8JsonWriter writer, PodSetupPlan plan)
    {
        writer.WriteStartObject();
        writer.WriteString("podId", plan.PodId);
        writer.WriteString("sshHost", plan.SshHost);
        writer.WriteNumber("sshPort", plan.SshPort);
        writer.WriteString("modelsPath", plan.ModelsPath);
        writer.WriteString("mountCommand", plan.MountCommand);
        writer.WriteString("vllmVersion", plan.VllmVersion);
        writer.WriteBoolean("hfTokenConfigured", plan.HfTokenConfigured);
        writer.WriteBoolean("piApiKeyConfigured", plan.PiApiKeyConfigured);
        writer.WriteString("scriptRemotePath", plan.ScriptRemotePath);
        writer.WriteString("setupCommand", plan.SetupCommand);
        writer.WriteBoolean("planOnly", plan.IsPlanOnly);
        writer.WritePropertyName("steps");
        writer.WriteStartArray();
        foreach (var step in plan.Steps)
        {
            writer.WriteStringValue(step);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSetupExecutionStepObject(Utf8JsonWriter writer, PodSetupExecutionStep step)
    {
        writer.WriteStartObject();
        writer.WriteString("name", step.Name);
        writer.WriteBoolean("success", step.Success);
        writer.WriteString("summary", step.Summary);
        writer.WriteString("command", step.DisplayCommand);
        writer.WriteNumber("exitCode", step.ExitCode);
        writer.WriteString("stdout", step.StdOut);
        writer.WriteString("stderr", step.StdErr);
        writer.WriteNumber("durationMs", step.Duration.TotalMilliseconds);
        writer.WriteEndObject();
    }

    private static string IndentBlock(string value, string indent)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return indent + value.ReplaceLineEndings(Environment.NewLine + indent);
    }

    private static void PrintStatusJson(PodsConfig config, int enabledCount, int disabledCount)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("podCount", config.Pods.Count);
            writer.WriteNumber("enabledCount", enabledCount);
            writer.WriteNumber("disabledCount", disabledCount);
            writer.WriteString("activePodId", config.ActivePodId);
            writer.WritePropertyName("pods");
            writer.WriteStartArray();
            foreach (var pod in config.Pods)
            {
                WriteStatusPodObject(writer, pod, IsActivePod(config, pod));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintProbeJson(IReadOnlyList<PodProbeResult> results)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var result in results.OrderBy(result => result.PodId, StringComparer.OrdinalIgnoreCase))
            {
                WriteProbeResultObject(writer, result);
            }

            writer.WriteEndArray();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintHealthJson(IReadOnlyList<PodHealthResult> results)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var result in results.OrderBy(result => result.PodId, StringComparer.OrdinalIgnoreCase))
            {
                WriteHealthResultObject(writer, result);
            }

            writer.WriteEndArray();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintExecJson(PodExecResult result)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            WriteExecResultObject(writer, result);
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintLifecycleOperationJson(PodDeployResult result)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            WriteLifecycleOperationObject(writer, result.PodId, result.Success, result.Summary, "deploy", result.DeploymentName, result.ModelId, result.Execution);
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintLifecycleOperationJson(PodStopResult result, string operation)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            WriteLifecycleOperationObject(writer, result.PodId, result.Success, result.Summary, operation, result.DeploymentName, null, result.Execution);
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void WriteExecResultObject(Utf8JsonWriter writer, PodExecResult result)
    {
        writer.WriteStartObject();
        writer.WriteString("podId", result.PodId);
        writer.WriteBoolean("success", result.Success);
        writer.WriteString("summary", result.Summary);
        WriteExecutionObjectFields(writer, result);
        writer.WriteEndObject();
    }

    private static void WriteLifecycleOperationObject(
        Utf8JsonWriter writer,
        string podId,
        bool success,
        string summary,
        string operation,
        string? deploymentName,
        string? modelId,
        PodExecResult? execution)
    {
        writer.WriteStartObject();
        writer.WriteString("podId", podId);
        writer.WriteBoolean("success", success);
        writer.WriteString("summary", summary);
        writer.WriteString("operation", operation);
        writer.WriteString("deploymentName", deploymentName);
        writer.WriteString("modelId", modelId);
        WriteExecutionObjectFields(writer, execution);
        writer.WriteEndObject();
    }

    private static void WriteExecutionObjectFields(Utf8JsonWriter writer, PodExecResult? result)
    {
        writer.WriteString("transport", result?.Transport);
        writer.WriteString("target", result?.Target);
        writer.WriteString("command", result?.Command);
        writer.WriteString("failureKind", result is null ? null : PodExecFailureKinds.FromResult(result));
        WriteNullableNumber(writer, "exitCode", result?.ExitCode);
        writer.WriteString("stdout", result?.StdOut);
        writer.WriteString("stderr", result?.StdErr);
        WriteNullableDouble(writer, "durationMs", result is null ? null : result.Duration.TotalMilliseconds);
    }

    private static void WriteStatusPodObject(Utf8JsonWriter writer, PodDefinition pod, bool active)
    {
        writer.WriteStartObject();
        writer.WriteString("podId", pod.Id);
        writer.WriteBoolean("active", active);
        writer.WriteBoolean("enabled", pod.Enabled);
        writer.WriteString("provider", pod.Provider);
        writer.WriteString("model", pod.Model);
        writer.WriteString("region", pod.Region);
        writer.WriteString("transport", GetPodTransport(pod));
        writer.WriteString("endpoint", string.IsNullOrWhiteSpace(pod.Endpoint) ? null : pod.Endpoint);
        writer.WriteString("host", string.IsNullOrWhiteSpace(pod.SshHost) ? null : pod.SshHost);
        WriteNullableNumber(writer, "port", string.IsNullOrWhiteSpace(pod.SshHost) ? null : pod.SshPort ?? 22);
        writer.WriteString("modelsPath", pod.ModelsPath);
        writer.WriteString("vllmVersion", pod.VllmVersion);
        writer.WritePropertyName("tags");
        writer.WriteStartArray();
        foreach (var tag in pod.Tags)
        {
            writer.WriteStringValue(tag);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteProbeResultObject(Utf8JsonWriter writer, PodProbeResult result)
    {
        writer.WriteStartObject();
        writer.WriteString("podId", result.PodId);
        writer.WriteBoolean("success", result.Success);
        writer.WriteString("transport", result.Transport);
        WriteNullableDouble(writer, "latency", result.Latency?.TotalMilliseconds);
        writer.WriteString("endpoint", result.Endpoint);
        writer.WriteString("host", result.Host);
        WriteNullableNumber(writer, "port", result.Port);
        writer.WriteString("summary", result.Summary);
        WriteNullableNumber(writer, "statusCode", result.StatusCode);
        writer.WriteEndObject();
    }

    private static void WriteHealthResultObject(Utf8JsonWriter writer, PodHealthResult result)
    {
        writer.WriteStartObject();
        writer.WriteString("podId", result.PodId);
        writer.WriteBoolean("healthy", result.Healthy);
        writer.WriteString("transport", result.Transport);
        WriteNullableDouble(writer, "latency", result.Latency?.TotalMilliseconds);
        writer.WriteString("summary", result.Summary);
        writer.WriteEndObject();
    }

    private static void PrintLogsJson(PodLogsResult result, string deploymentName, int tail)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("podId", result.PodId);
            writer.WriteBoolean("success", result.Success);
            writer.WriteString("summary", result.Summary);
            writer.WriteString("deploymentName", result.DeploymentName ?? deploymentName);
            writer.WriteNumber("tail", result.Tail ?? tail);
            writer.WriteBoolean("follow", result.Follow);
            writer.WriteString("failureKind", result.FailureKind);
            writer.WriteString("stdout", result.Output ?? string.Empty);
            writer.WriteString("stderr", result.StdErr);
            writer.WriteString("command", result.Command);
            WriteNullableNumber(writer, "exitCode", result.ExitCode);
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintInitJson(string fullPath)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", true);
            writer.WriteString("path", fullPath);
            writer.WriteString("summary", $"Created sample pod config at {fullPath}");
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintListJson(PodsConfig config)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("podCount", config.Pods.Count);
            writer.WriteString("activePodId", config.ActivePodId);
            writer.WritePropertyName("pods");
            writer.WriteStartArray();
            foreach (var pod in config.Pods)
            {
                WritePodDefinitionObject(writer, pod, IsActivePod(config, pod));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintActiveJson(string activePodId, string fullPath)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", true);
            writer.WriteString("activePodId", activePodId);
            writer.WriteString("configPath", fullPath);
            writer.WriteString("summary", $"Active pod set to {activePodId}");
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintRemoveJson(string podId, string? activePodId, string fullPath)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", true);
            writer.WriteString("podId", podId);
            writer.WriteString("activePodId", activePodId);
            writer.WriteString("configPath", fullPath);
            writer.WriteString("summary", $"Removed pod {podId}");
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintSetupRegistrationJson(PodDefinition pod, string? activePodId, string fullPath)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", true);
            writer.WriteString("podId", pod.Id);
            writer.WriteBoolean("registered", true);
            writer.WriteBoolean("setupExecuted", false);
            writer.WriteBoolean("active", activePodId is not null && activePodId.Equals(pod.Id, StringComparison.OrdinalIgnoreCase));
            writer.WriteString("activePodId", activePodId);
            writer.WriteString("configPath", fullPath);
            writer.WriteString("sshHost", pod.SshHost);
            writer.WriteNumber("sshPort", pod.SshPort ?? 22);
            writer.WriteString("modelsPath", pod.ModelsPath);
            writer.WriteString("vllmVersion", pod.VllmVersion);
            writer.WriteString("summary", $"Registered pod {pod.Id}; setup execution was not run.");
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintValidateJson(bool success, IReadOnlyList<string> errors)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", success);
            writer.WriteString("summary", success ? "Config is valid." : $"Config has {errors.Count} error(s).");
            writer.WritePropertyName("errors");
            writer.WriteStartArray();
            foreach (var error in errors)
            {
                writer.WriteStringValue(error);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintDeploymentsJson(PodDeploymentsResult result)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("podId", result.PodId);
            writer.WriteBoolean("success", result.Success);
            writer.WriteString("summary", result.Summary);
            writer.WritePropertyName("deployments");
            writer.WriteStartArray();
            foreach (var deployment in result.Deployments)
            {
                WriteDeploymentObject(writer, deployment);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void WritePodDefinitionObject(Utf8JsonWriter writer, PodDefinition pod, bool active)
    {
        writer.WriteStartObject();
        writer.WriteString("podId", pod.Id);
        writer.WriteBoolean("active", active);
        writer.WriteBoolean("enabled", pod.Enabled);
        writer.WriteString("provider", pod.Provider);
        writer.WriteString("model", pod.Model);
        writer.WriteString("region", pod.Region);
        writer.WriteString("endpoint", pod.Endpoint);
        writer.WriteString("host", pod.SshHost);
        WriteNullableNumber(writer, "port", pod.SshPort);
        writer.WriteString("modelsPath", pod.ModelsPath);
        writer.WriteString("vllmVersion", pod.VllmVersion);
        writer.WritePropertyName("tags");
        writer.WriteStartArray();
        foreach (var tag in pod.Tags)
        {
            writer.WriteStringValue(tag);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteDeploymentObject(Utf8JsonWriter writer, PodDeploymentInfo deployment)
    {
        writer.WriteStartObject();
        writer.WriteString("name", deployment.Name);
        writer.WriteString("model", deployment.Model);
        writer.WriteString("status", deployment.Status);
        writer.WriteString("timestamp", deployment.Timestamp);
        writer.WriteEndObject();
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
            writer.WriteString("revision", plan.Revision);
            writer.WriteString("modelPath", plan.ModelPath);
            writer.WriteBoolean("usesSnapshotDiscovery", plan.UsesSnapshotDiscovery);
            writer.WriteNumber("port", plan.Port);
            writer.WriteString("servedModel", plan.ServedModelName);
            WriteVllmPlanOptionFields(writer, plan);
            WriteKnownModelObject(writer, plan);
            writer.WriteString("unit", plan.UnitName);
            writer.WriteString("logPath", plan.LogPath);
            writer.WriteString("runnerScriptPath", plan.RunnerScriptPath);
            writer.WriteString("wrapperScriptPath", plan.WrapperScriptPath);
            writer.WriteBoolean("usesPseudoTtyWrapper", plan.UsesPseudoTtyWrapper);
            writer.WriteString("serveCommand", plan.ServeCommand);
            writer.WriteString("systemdUnit", plan.SystemdUnit);
            writer.WritePropertyName("metadata");
            WriteVllmPlanMetadataObject(writer, plan);
            writer.WriteString("metadataJson", plan.MetadataJson);
            writer.WriteString("remoteCommand", plan.RemoteCommand);
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintVllmOperationText(PodVllmOperationResult result)
    {
        Console.WriteLine($"{result.PodId} | ok={result.Success} | operation={result.Operation} | deployment={result.DeploymentName} | failure={result.FailureKind} | exit={result.ExitCode} | {result.Summary}");
        Console.WriteLine("[remote-command]");
        Console.WriteLine(result.Command);
        if (result.Prefetch is not null)
        {
            Console.WriteLine("[prefetch]");
            Console.WriteLine(
                $"ok={result.Prefetch.Success} | operation={result.Prefetch.Operation} | model={result.Prefetch.ModelId} | revision={result.Prefetch.RequestedRevision ?? "-"} | trigger={result.PrefetchTriggerFailureKind ?? "-"} | {result.Prefetch.Summary}");
            if (!string.IsNullOrWhiteSpace(result.Prefetch.Output))
            {
                Console.WriteLine("[prefetch-output]");
                Console.WriteLine(result.Prefetch.Output.TrimEnd());
            }
        }
        if (result.Plan is not null)
        {
            Console.WriteLine($"logPath={result.Plan.LogPath}");
            Console.WriteLine($"runnerScriptPath={result.Plan.RunnerScriptPath}");
            Console.WriteLine($"wrapperScriptPath={result.Plan.WrapperScriptPath}");
            Console.WriteLine($"usesPseudoTtyWrapper={result.Plan.UsesPseudoTtyWrapper}");
            Console.WriteLine("[serve-command]");
            Console.WriteLine(result.Plan.ServeCommand);
        }

        if (result.Preflight is not null)
        {
            Console.WriteLine("[preflight]");
            PrintVllmPreflightText(result.Preflight, includeCommand: true, prefix: "preflight-");
        }

        PrintStdStreams(result.StdOut, result.StdErr);
        if (result.StartupWatch is not null)
        {
            Console.WriteLine("[startup-watch]");
            PrintVllmStartupWatchText(result.StartupWatch, includeCommand: true, streamPrefix: "startup-watch-");
        }

        if (result.Health is not null)
        {
            Console.WriteLine("[health]");
            PrintVllmHealthText(result.Health, includeCommand: true, streamPrefix: "health-");
        }

        if (result.Rollback is not null)
        {
            Console.WriteLine("[rollback]");
            Console.WriteLine($"ok={result.Rollback.Success} | deployment={result.Rollback.DeploymentName} | failure={result.Rollback.FailureKind} | exit={result.Rollback.ExitCode} | {result.Rollback.Summary}");
            Console.WriteLine("[rollback-command]");
            Console.WriteLine(result.Rollback.Command);
            PrintStdStreams(result.Rollback.StdOut, result.Rollback.StdErr, prefix: "rollback-");
        }
    }

    private static void PrintVllmRollbackText(PodVllmRollbackResult result)
    {
        Console.WriteLine($"{result.PodId} | ok={result.Success} | operation=rollback | deployment={result.DeploymentName} | failure={result.FailureKind} | exit={result.ExitCode} | {result.Summary}");
        Console.WriteLine("[rollback-command]");
        Console.WriteLine(result.Command);
        PrintStdStreams(result.StdOut, result.StdErr, prefix: "rollback-");
    }

    private static void PrintVllmStatusText(PodVllmStatusResult result)
    {
        Console.WriteLine($"{result.PodId} | ok={result.Success} | operation=status | deployment={result.DeploymentName} | state={result.State} | ready={result.Ready} | unhealthy={result.Unhealthy} | exit={result.ExitCode} | {result.Summary}");
        Console.WriteLine("[remote-command]");
        Console.WriteLine(result.Command);
        PrintStdStreams(result.StdOut, result.StdErr);
    }

    private static void PrintVllmHealthText(
        PodVllmHealthResult result,
        bool includeCommand = true,
        string prefix = "",
        string streamPrefix = "")
    {
        Console.WriteLine($"{prefix}{result.PodId} | ok={result.Success} | operation=health | deployment={result.DeploymentName} | state={result.State} | ready={result.Ready} | unhealthy={result.Unhealthy} | failure={result.FailureKind} | attempts={result.Attempts}/{result.MaxAttempts} | exit={result.ExitCode} | {result.Summary}");
        if (includeCommand)
        {
            Console.WriteLine("[health-command]");
            Console.WriteLine(result.Command);
        }

        PrintStdStreams(result.StdOut, result.StdErr, prefix: streamPrefix);
    }

    private static void PrintVllmStartupWatchText(
        PodVllmStartupWatchResult result,
        bool includeCommand = true,
        string prefix = "",
        string streamPrefix = "")
    {
        Console.WriteLine($"{prefix}{result.PodId} | ok={result.Success} | operation=startup-watch | deployment={result.DeploymentName} | state={result.State} | ready={result.Ready} | unhealthy={result.Unhealthy} | failure={result.FailureKind} | attempts={result.Attempts}/{result.MaxAttempts} | exit={result.ExitCode} | {result.Summary}");
        if (includeCommand)
        {
            Console.WriteLine("[startup-watch-command]");
            Console.WriteLine(result.Command);
        }

        PrintStdStreams(result.StdOut, result.StdErr, prefix: streamPrefix);
    }

    private static void PrintVllmOperationJson(PodVllmOperationResult result)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            WriteVllmOperationObject(writer, result);
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintVllmStopAllJson(
        PodDefinition pod,
        PodDeploymentsResult deployments,
        IReadOnlyList<PodVllmOperationResult> results,
        bool success,
        string summary)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("pod", pod.Id);
            writer.WriteBoolean("ok", success);
            writer.WriteString("operation", "stop-all");
            writer.WriteString("summary", summary);
            writer.WriteNumber("deploymentCount", deployments.Deployments.Count);
            writer.WriteNumber("stoppedCount", results.Count(result => result.Success));
            writer.WriteString("listSummary", deployments.Summary);
            writer.WritePropertyName("deployments");
            writer.WriteStartArray();
            foreach (var result in results)
            {
                WriteVllmOperationObject(writer, result);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintVllmStopAllText(
        PodDefinition pod,
        PodDeploymentsResult deployments,
        IReadOnlyList<PodVllmOperationResult> results,
        bool success,
        string summary)
    {
        Console.WriteLine($"{pod.Id} | ok={success} | operation=stop-all | deployments={deployments.Deployments.Count} | stopped={results.Count(result => result.Success)} | {summary}");
        foreach (var result in results)
        {
            Console.WriteLine($"- {result.DeploymentName} | ok={result.Success} | exit={result.ExitCode} | {result.Summary}");
        }
    }

    private static void WriteVllmOperationObject(Utf8JsonWriter writer, PodVllmOperationResult result)
    {
        writer.WriteStartObject();
        writer.WriteString("pod", result.PodId);
        writer.WriteBoolean("ok", result.Success);
        writer.WriteString("operation", result.Operation);
        writer.WriteString("deployment", result.DeploymentName);
        writer.WriteString("summary", result.Summary);
        writer.WriteString("failureKind", result.FailureKind);
        writer.WriteString("remoteCommand", result.Command);
        writer.WriteNumber("exitCode", result.ExitCode);
        writer.WriteString("stdout", result.StdOut);
        writer.WriteString("stderr", result.StdErr);
        WriteNullableNumber(writer, "processId", result.ProcessId);
        if (result.Prefetch is not null)
        {
            writer.WritePropertyName("prefetch");
            WriteModelOperationObject(writer, result.Prefetch, result.PrefetchTriggerFailureKind);
        }
        if (result.Plan is not null)
        {
            writer.WritePropertyName("plan");
            WriteVllmPlanObject(writer, result.Plan);
        }
        if (result.Preflight is not null)
        {
            writer.WritePropertyName("preflight");
            WriteVllmPreflightObject(writer, result.Preflight);
        }
        if (result.StartupWatch is not null)
        {
            writer.WritePropertyName("startupWatch");
            WriteVllmStartupWatchObject(writer, result.StartupWatch);
        }
        if (result.Health is not null)
        {
            writer.WritePropertyName("health");
            WriteVllmHealthObject(writer, result.Health);
        }
        if (result.Rollback is not null)
        {
            writer.WritePropertyName("rollback");
            WriteVllmRollbackObject(writer, result.Rollback);
        }
        writer.WriteEndObject();
    }

    private static void PrintVllmRollbackJson(PodVllmRollbackResult result)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            WriteVllmRollbackObject(writer, result);
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintVllmStatusJson(PodVllmStatusResult result)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("pod", result.PodId);
            writer.WriteBoolean("ok", result.Success);
            writer.WriteString("operation", "status");
            writer.WriteString("deployment", result.DeploymentName);
            writer.WriteString("summary", result.Summary);
            writer.WriteString("remoteCommand", result.Command);
            writer.WriteNumber("exitCode", result.ExitCode);
            writer.WriteString("state", result.State);
            writer.WriteBoolean("ready", result.Ready);
            writer.WriteBoolean("unhealthy", result.Unhealthy);
            writer.WritePropertyName("metadata");
            WriteNullableJsonObject(writer, result.MetadataJson);
            writer.WriteString("metadataJson", result.MetadataJson);
            writer.WriteString("stdout", result.StdOut);
            writer.WriteString("stderr", result.StdErr);
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintVllmHealthJson(PodVllmHealthResult result)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            WriteVllmHealthObject(writer, result);
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintVllmPreflightText(
        PodVllmPreflightResult result,
        bool includeCommand = true,
        string prefix = "")
    {
        Console.WriteLine($"{prefix}{result.PodId} | ok={result.Success} | operation=preflight | deployment={result.DeploymentName} | model={result.ModelId} | modelCachePresent={result.ModelCachePresent} | snapshotCount={result.SnapshotCount} | vllmAvailable={result.VllmAvailable} | failure={result.FailureKind} | exit={result.ExitCode} | {result.Summary}");
        if (!string.IsNullOrWhiteSpace(result.RequestedRevision))
        {
            Console.WriteLine($"{prefix}requestedRevision={result.RequestedRevision}");
        }
        Console.WriteLine($"{prefix}modelCachePath={result.ModelCachePath}");
        Console.WriteLine($"{prefix}resolvedModelPath={result.ResolvedModelPath ?? "-"}");
        if (includeCommand)
        {
            Console.WriteLine("[preflight-command]");
            Console.WriteLine(result.Command);
        }

        PrintStdStreams(result.StdOut, result.StdErr, prefix: prefix);
    }

    private static void PrintVllmPreflightJson(PodVllmPreflightResult result)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            WriteVllmPreflightObject(writer, result);
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void WriteVllmPlanObject(Utf8JsonWriter writer, PodVllmServePlan plan)
    {
        writer.WriteStartObject();
        writer.WriteString("deployment", plan.DeploymentName);
        writer.WriteString("model", plan.ModelId);
        writer.WriteString("revision", plan.Revision);
        writer.WriteString("modelPath", plan.ModelPath);
        writer.WriteBoolean("usesSnapshotDiscovery", plan.UsesSnapshotDiscovery);
        writer.WriteNumber("port", plan.Port);
        writer.WriteString("servedModel", plan.ServedModelName);
        WriteVllmPlanOptionFields(writer, plan);
        WriteKnownModelObject(writer, plan);
        writer.WriteString("unit", plan.UnitName);
        writer.WriteString("logPath", plan.LogPath);
        writer.WriteString("runnerScriptPath", plan.RunnerScriptPath);
        writer.WriteString("wrapperScriptPath", plan.WrapperScriptPath);
        writer.WriteBoolean("usesPseudoTtyWrapper", plan.UsesPseudoTtyWrapper);
        writer.WriteString("serveCommand", plan.ServeCommand);
        writer.WriteString("systemdUnit", plan.SystemdUnit);
        writer.WritePropertyName("metadata");
        WriteVllmPlanMetadataObject(writer, plan);
        writer.WriteString("metadataJson", plan.MetadataJson);
        writer.WriteString("planRemoteCommand", plan.RemoteCommand);
        writer.WriteEndObject();
    }

    private static void WriteVllmPlanOptionFields(Utf8JsonWriter writer, PodVllmServePlan plan)
    {
        if (plan.RequestedGpuCount is null)
        {
            writer.WriteNull("requestedGpuCount");
        }
        else
        {
            writer.WriteNumber("requestedGpuCount", plan.RequestedGpuCount.Value);
        }

        writer.WritePropertyName("selectedGpus");
        if (plan.SelectedGpuIds is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartArray();
            foreach (var gpuId in plan.SelectedGpuIds)
            {
                writer.WriteNumberValue(gpuId);
            }
            writer.WriteEndArray();
        }

        writer.WriteString("memory", plan.MemoryOverride);
        if (plan.MemoryUtilization is null)
        {
            writer.WriteNull("memoryUtilization");
        }
        else
        {
            writer.WriteNumber("memoryUtilization", plan.MemoryUtilization.Value);
        }

        writer.WriteString("context", plan.ContextOverride);
        if (plan.ContextTokens is null)
        {
            writer.WriteNull("contextTokens");
        }
        else
        {
            writer.WriteNumber("contextTokens", plan.ContextTokens.Value);
        }
    }

    private static void WriteKnownModelObject(Utf8JsonWriter writer, PodVllmServePlan plan)
    {
        writer.WritePropertyName("knownModel");
        if (string.IsNullOrWhiteSpace(plan.KnownModelName))
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("name", plan.KnownModelName);
        if (plan.KnownModelGpuCount is not null)
        {
            writer.WriteNumber("gpuCount", plan.KnownModelGpuCount.Value);
        }

        writer.WritePropertyName("args");
        writer.WriteStartArray();
        foreach (var arg in plan.KnownModelArgs ?? [])
        {
            writer.WriteStringValue(arg);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("env");
        if (plan.KnownModelEnvironment is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            foreach (var pair in plan.KnownModelEnvironment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(pair.Key, pair.Value);
            }
            writer.WriteEndObject();
        }

        writer.WriteString("notes", plan.KnownModelNotes);
        writer.WriteEndObject();
    }

    private static void WriteVllmPlanMetadataObject(Utf8JsonWriter writer, PodVllmServePlan plan)
    {
        using var metadata = JsonDocument.Parse(plan.MetadataJson);
        writer.WriteStartObject();
        var wroteUsesSnapshotDiscovery = false;
        foreach (var property in metadata.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("usesSnapshotDiscovery", StringComparison.Ordinal))
            {
                writer.WriteBoolean("usesSnapshotDiscovery", plan.UsesSnapshotDiscovery);
                wroteUsesSnapshotDiscovery = true;
                continue;
            }

            property.WriteTo(writer);
        }

        if (!wroteUsesSnapshotDiscovery)
        {
            writer.WriteBoolean("usesSnapshotDiscovery", plan.UsesSnapshotDiscovery);
        }

        writer.WriteEndObject();
    }

    private static void WriteVllmPreflightObject(Utf8JsonWriter writer, PodVllmPreflightResult result)
    {
        writer.WriteStartObject();
        writer.WriteString("pod", result.PodId);
        writer.WriteBoolean("ok", result.Success);
        writer.WriteString("operation", "preflight");
        writer.WriteString("deployment", result.DeploymentName);
        writer.WriteString("model", result.ModelId);
        writer.WriteString("requestedRevision", result.RequestedRevision);
        writer.WriteString("modelCachePath", result.ModelCachePath);
        writer.WriteString("resolvedModelPath", result.ResolvedModelPath);
        writer.WriteBoolean("modelCachePresent", result.ModelCachePresent);
        writer.WriteNumber("snapshotCount", result.SnapshotCount);
        writer.WriteBoolean("vllmAvailable", result.VllmAvailable);
        writer.WriteString("failureKind", result.FailureKind);
        writer.WriteString("summary", result.Summary);
        writer.WriteString("remoteCommand", result.Command);
        writer.WriteNumber("exitCode", result.ExitCode);
        writer.WriteString("stdout", result.StdOut);
        writer.WriteString("stderr", result.StdErr);
        writer.WriteEndObject();
    }

    private static void WriteVllmHealthObject(Utf8JsonWriter writer, PodVllmHealthResult result)
    {
        writer.WriteStartObject();
        writer.WriteString("pod", result.PodId);
        writer.WriteBoolean("ok", result.Success);
        writer.WriteString("operation", "health");
        writer.WriteString("deployment", result.DeploymentName);
        writer.WriteString("summary", result.Summary);
        writer.WriteString("remoteCommand", result.Command);
        writer.WriteNumber("exitCode", result.ExitCode);
        writer.WriteString("state", result.State);
        writer.WriteBoolean("ready", result.Ready);
        writer.WriteBoolean("unhealthy", result.Unhealthy);
        writer.WriteString("failureKind", result.FailureKind);
        writer.WriteNumber("attempts", result.Attempts);
        writer.WriteNumber("maxAttempts", result.MaxAttempts);
        writer.WritePropertyName("metadata");
        WriteNullableJsonObject(writer, result.MetadataJson);
        writer.WriteString("metadataJson", result.MetadataJson);
        writer.WriteString("stdout", result.StdOut);
        writer.WriteString("stderr", result.StdErr);
        writer.WriteEndObject();
    }

    private static void WriteVllmStartupWatchObject(Utf8JsonWriter writer, PodVllmStartupWatchResult result)
    {
        writer.WriteStartObject();
        writer.WriteString("pod", result.PodId);
        writer.WriteBoolean("ok", result.Success);
        writer.WriteString("operation", "startup-watch");
        writer.WriteString("deployment", result.DeploymentName);
        writer.WriteString("summary", result.Summary);
        writer.WriteString("remoteCommand", result.Command);
        writer.WriteNumber("exitCode", result.ExitCode);
        writer.WriteString("state", result.State);
        writer.WriteBoolean("ready", result.Ready);
        writer.WriteBoolean("unhealthy", result.Unhealthy);
        writer.WriteString("failureKind", result.FailureKind);
        writer.WriteNumber("attempts", result.Attempts);
        writer.WriteNumber("maxAttempts", result.MaxAttempts);
        writer.WriteString("stdout", result.StdOut);
        writer.WriteString("stderr", result.StdErr);
        writer.WriteEndObject();
    }

    private static void WriteNullableJsonObject(Utf8JsonWriter writer, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            writer.WriteNullValue();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                document.RootElement.WriteTo(writer);
                return;
            }
        }
        catch (JsonException)
        {
        }

        writer.WriteNullValue();
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string propertyName, int? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
            return;
        }

        writer.WriteNull(propertyName);
    }

    private static void WriteNullableDouble(Utf8JsonWriter writer, string propertyName, double? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
            return;
        }

        writer.WriteNull(propertyName);
    }

    private static void WriteVllmRollbackObject(Utf8JsonWriter writer, PodVllmRollbackResult result)
    {
        writer.WriteStartObject();
        writer.WriteString("pod", result.PodId);
        writer.WriteBoolean("ok", result.Success);
        writer.WriteString("operation", "rollback");
        writer.WriteString("deployment", result.DeploymentName);
        writer.WriteString("failureKind", result.FailureKind);
        writer.WriteString("summary", result.Summary);
        writer.WriteString("remoteCommand", result.Command);
        writer.WriteNumber("exitCode", result.ExitCode);
        writer.WriteString("stdout", result.StdOut);
        writer.WriteString("stderr", result.StdErr);
        writer.WriteEndObject();
    }

    private static void PrintStdStreams(string stdout, string stderr, string prefix = "")
    {
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Console.WriteLine($"[{prefix}stdout]");
            Console.WriteLine(stdout.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Console.WriteLine($"[{prefix}stderr]");
            Console.WriteLine(stderr.TrimEnd());
        }
    }

    private static async Task<int> ModelListAsync(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodModelService modelService)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 2, out var positionalArgs);
        if (!TryParseModelCommandWithOptionalPod(positionalArgs, minValueCount: 0, "Usage: model list [--json] [--config path] [--pod pod-id]", out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var result = await modelService.ListAsync(pod).ConfigureAwait(false);
        if (jsonOutput)
        {
            PrintModelListJson(result);
            return result.Success ? 0 : 1;
        }

        Console.WriteLine($"{result.PodId} | ok={result.Success} | {result.Summary}");
        foreach (var model in result.Models)
        {
            Console.WriteLine($"- {model.ModelId} | cache={model.CacheDirectory} | snapshots={model.SnapshotCount} | resolved={model.ResolvedModelPath ?? "-"} | snapshotFailure={model.SnapshotFailureKind}");
        }

        return result.Success ? 0 : 1;
    }

    private static async Task<int> ModelPullAsync(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodModelService modelService)
    {
        const string usage = "Usage: model pull [--json] [--config path] [--pod pod-id] [--revision rev] [--snapshot rev] <model-id>";

        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 2, out var pullArgs);
        var revision = ConsumeStringOptionAny(pullArgs, ["--revision", "--snapshot"], startIndex: 2, out var positionalArgs, out var optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            return 1;
        }

        if (!TryParseModelCommandWithOptionalPod(positionalArgs, minValueCount: 1, usage, out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var modelId = parsed.Values[0];
        var result = await modelService.PullAsync(pod, modelId, revision).ConfigureAwait(false);
        if (jsonOutput)
        {
            PrintModelOperationJson(result);
            return result.Success ? 0 : 1;
        }

        Console.WriteLine($"{result.PodId} | ok={result.Success} | operation={result.Operation} | model={result.ModelId} | {result.Summary}");
        return result.Success ? 0 : 1;
    }

    private static async Task<int> ModelRemoveAsync(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodModelService modelService)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 2, out var positionalArgs);
        if (!TryParseModelCommandWithOptionalPod(positionalArgs, minValueCount: 1, "Usage: model remove [--json] [--config path] [--pod pod-id] <model-id>", out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var modelId = parsed.Values[0];
        var result = await modelService.RemoveAsync(pod, modelId).ConfigureAwait(false);
        if (jsonOutput)
        {
            PrintModelOperationJson(result);
            return result.Success ? 0 : 1;
        }

        Console.WriteLine($"{result.PodId} | ok={result.Success} | operation={result.Operation} | model={result.ModelId} | {result.Summary}");
        return result.Success ? 0 : 1;
    }

    private static async Task<int> ModelStatusAsync(
        string[] args,
        PodsConfigStore store,
        PodsConfigValidator validator,
        PodModelService modelService)
    {
        var jsonOutput = TryConsumeFlag(args, "--json", startIndex: 2, out var positionalArgs);
        if (!TryParseModelCommandWithOptionalPod(positionalArgs, minValueCount: 1, "Usage: model status [--json] [--config path] [--pod pod-id] <model-id>", out var parsed))
        {
            return 1;
        }

        var pod = LoadPodOrActiveReport(parsed.ConfigPath, parsed.PodId, store, validator, out var exitCode);
        if (pod is null) return exitCode;

        var modelId = parsed.Values[0];
        var result = await modelService.StatusAsync(pod, modelId).ConfigureAwait(false);
        if (jsonOutput)
        {
            PrintModelStatusJson(result);
            return result.Success ? 0 : 1;
        }

        Console.WriteLine($"{result.PodId} | ok={result.Success} | present={result.Present} | model={result.ModelId} | snapshots={result.SnapshotCount} | resolved={result.ResolvedModelPath ?? "-"} | snapshotFailure={result.SnapshotFailureKind} | {result.Summary}");
        return result.Success ? 0 : 1;
    }

    private static void PrintModelListJson(PodModelListResult result)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("podId", result.PodId);
            writer.WriteBoolean("success", result.Success);
            writer.WriteString("summary", result.Summary);
            writer.WritePropertyName("models");
            writer.WriteStartArray();
            foreach (var model in result.Models)
            {
                writer.WriteStartObject();
                writer.WriteString("modelId", model.ModelId);
                writer.WriteString("cacheDirectory", model.CacheDirectory);
                writer.WriteNumber("snapshotCount", model.SnapshotCount);
                writer.WriteString("resolvedModelPath", model.ResolvedModelPath);
                writer.WriteString("snapshotFailureKind", model.SnapshotFailureKind);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void PrintModelOperationJson(PodModelOperationResult result)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            WriteModelOperationObject(writer, result);
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void WriteModelOperationObject(
        Utf8JsonWriter writer,
        PodModelOperationResult result,
        string? triggerFailureKind = null)
    {
        writer.WriteStartObject();
        writer.WriteString("podId", result.PodId);
        writer.WriteBoolean("success", result.Success);
        writer.WriteString("summary", result.Summary);
        writer.WriteString("operation", result.Operation);
        writer.WriteString("modelId", result.ModelId);
        writer.WriteString("output", result.Output);
        writer.WriteString("requestedRevision", result.RequestedRevision);
        writer.WriteString("triggerFailureKind", triggerFailureKind);
        writer.WriteEndObject();
    }

    private static void PrintModelStatusJson(PodModelStatusResult result)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("podId", result.PodId);
            writer.WriteBoolean("success", result.Success);
            writer.WriteString("summary", result.Summary);
            writer.WriteString("modelId", result.ModelId);
            writer.WriteBoolean("present", result.Present);
            writer.WriteString("modelCachePath", result.ModelCachePath);
            writer.WriteNumber("snapshotCount", result.SnapshotCount);
            writer.WriteString("resolvedModelPath", result.ResolvedModelPath);
            writer.WriteString("snapshotFailureKind", result.SnapshotFailureKind);
            writer.WriteString("output", result.Output);
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static PodsConfig? LoadOrReport(string path, PodsConfigStore store, PodsConfigValidator validator, out int exitCode)
    {
        exitCode = 0;
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

    private static PodDefinition? LoadPodOrActiveReport(
        string path,
        string? podId,
        PodsConfigStore store,
        PodsConfigValidator validator,
        out int exitCode)
    {
        var config = LoadOrReport(path, store, validator, out exitCode);
        if (config is null) return null;

        var resolvedPodId = string.IsNullOrWhiteSpace(podId) ? config.ActivePodId : podId;
        if (string.IsNullOrWhiteSpace(resolvedPodId))
        {
            Console.Error.WriteLine("No active pod. Use 'active <pod-id>' to set one.");
            exitCode = 1;
            return null;
        }

        var pod = config.Pods.FirstOrDefault(p => p.Id.Equals(resolvedPodId, StringComparison.OrdinalIgnoreCase));
        if (pod is null)
        {
            Console.Error.WriteLine($"Pod not found: {resolvedPodId}");
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
        Console.Error.WriteLine("Usage: model <list|pull|remove|status> [--json] [--config path] [--pod pod-id] [--revision rev] [--snapshot rev] [model-id]");
        return 1;
    }

    private static int UnknownPodsSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"Unknown pods subcommand: {subcommand}");
        Console.Error.WriteLine("Usage: pods [--json] [path] OR pods <setup|active|remove> [args]");
        return 1;
    }

    private static int UnknownSetupSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"Unknown setup subcommand: {subcommand}");
        Console.Error.WriteLine("Usage: setup <plan|run> [--json] [path] <pod-id>");
        return 1;
    }

    private static int UnknownVllmSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"Unknown vllm subcommand: {subcommand}");
        Console.Error.WriteLine("Usage: vllm <plan|preflight|deploy|status|health|stop|rollback> [--json] [--config path] [--pod pod-id] <model-id|deployment-name> [--name deployment-name] [--gpus n] [--memory percent] [--context size] [--vllm <args...>]");
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

    private static string? ConsumeStringOption(
        string[] args,
        string option,
        int startIndex,
        out string[] positionalArgs,
        out string? error)
    {
        error = null;
        string? value = null;
        var values = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (i >= startIndex && args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    error = $"Invalid {option} value.";
                    positionalArgs = args;
                    return null;
                }

                value = args[i + 1];
                i++;
                continue;
            }

            values.Add(args[i]);
        }

        positionalArgs = values.ToArray();
        return value;
    }

    private static string? ConsumeStringOptionAny(
        string[] args,
        IReadOnlyList<string> options,
        int startIndex,
        out string[] positionalArgs,
        out string? error)
    {
        error = null;
        string? value = null;
        var values = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            string? matchedOption = null;
            if (i >= startIndex)
            {
                foreach (var option in options)
                {
                    if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedOption = option;
                        break;
                    }
                }
            }

            if (matchedOption is not null)
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    error = $"Invalid {matchedOption} value.";
                    positionalArgs = args;
                    return null;
                }

                value = args[i + 1];
                i++;
                continue;
            }

            values.Add(args[i]);
        }

        positionalArgs = values.ToArray();
        return value;
    }

    private static bool TryConsumeConfigAndPodOptions(
        string[] args,
        int startIndex,
        string usage,
        out string[] positionalArgs,
        out string configPath,
        out string? podId,
        out bool hasExplicitConfig)
    {
        var explicitConfigPath = ConsumeStringOption(args, "--config", startIndex, out var configArgs, out var optionError);
        hasExplicitConfig = explicitConfigPath is not null;
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            positionalArgs = args;
            configPath = GetDefaultConfigPath();
            podId = null;
            return false;
        }

        podId = ConsumeStringOption(configArgs, "--pod", startIndex, out positionalArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(usage);
            positionalArgs = configArgs;
            configPath = GetDefaultConfigPath();
            return false;
        }

        configPath = explicitConfigPath ?? GetDefaultConfigPath();
        return true;
    }

    private static bool TrySplitVllmExtraArgs(
        string[] args,
        string usage,
        out string[] cliArgs,
        out IReadOnlyList<string>? extraArgs)
    {
        var sentinelIndex = Array.FindIndex(
            args,
            2,
            arg => arg.Equals(VllmExtraArgsSentinel, StringComparison.OrdinalIgnoreCase));
        if (sentinelIndex < 0)
        {
            cliArgs = args;
            extraArgs = null;
            return true;
        }

        if (sentinelIndex == args.Length - 1)
        {
            Console.Error.WriteLine(usage);
            return FailVllmExtraArgs(out cliArgs, out extraArgs);
        }

        cliArgs = args[..sentinelIndex];
        extraArgs = args[(sentinelIndex + 1)..];
        return true;
    }

    private static bool FailVllmExtraArgs(out string[] cliArgs, out IReadOnlyList<string>? extraArgs)
    {
        cliArgs = [];
        extraArgs = null;
        return false;
    }

    private static bool TryPlanVllmServe(
        PodVllmCommandPlanner planner,
        PodDefinition pod,
        PodVllmServeOptions options,
        out PodVllmServePlan? plan)
    {
        try
        {
            plan = planner.PlanServe(pod, options);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            plan = null;
            return false;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            plan = null;
            return false;
        }
    }

    private static bool TryParseVllmServeCommand(
        string[] args,
        string usage,
        out VllmServeCommandArguments parsed)
    {
        var defaultConfigPath = GetDefaultConfigPath();
        parsed = new VllmServeCommandArguments(defaultConfigPath, null, string.Empty, null, null, null, null, null);
        var configPath = ConsumeStringOption(args, "--config", startIndex: 2, out var podArgs, out var optionError);
        var hasExplicitConfig = configPath is not null;
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            return false;
        }

        var podId = ConsumeStringOption(podArgs, "--pod", startIndex: 2, out var nameArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            return false;
        }

        var deploymentName = ConsumeStringOptionAny(nameArgs, ["--name", "--deployment"], startIndex: 2, out var deploymentArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            return false;
        }

        var revision = ConsumeStringOptionAny(deploymentArgs, ["--revision", "--snapshot"], startIndex: 2, out var revisionArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            return false;
        }

        var memory = ConsumeStringOption(revisionArgs, "--memory", startIndex: 2, out var memoryArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            return false;
        }

        var context = ConsumeStringOption(memoryArgs, "--context", startIndex: 2, out var contextArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            return false;
        }

        var requestedGpuCount = ConsumePositiveIntOption(contextArgs, "--gpus", startIndex: 2, out var positionalArgs, out optionError);
        if (optionError is not null)
        {
            Console.Error.WriteLine(optionError);
            return false;
        }

        configPath ??= defaultConfigPath;
        var values = positionalArgs.Skip(2).ToArray();
        if (!hasExplicitConfig &&
            values.Length >= 3 &&
            LooksLikeConfigPath(values[0]))
        {
            configPath = values[0];
            values = values[1..];
        }

        if (!string.IsNullOrWhiteSpace(podId))
        {
            if (values.Length is < 1 or > 2 ||
                (deploymentName is not null && values.Length > 1))
            {
                Console.Error.WriteLine(usage);
                return false;
            }

            parsed = new VllmServeCommandArguments(
                configPath,
                podId,
                values[0],
                deploymentName ?? (values.Length > 1 ? values[1] : null),
                revision,
                requestedGpuCount,
                memory,
                context);
            return true;
        }

        switch (values.Length)
        {
            case 1:
                parsed = new VllmServeCommandArguments(configPath, null, values[0], deploymentName, revision, requestedGpuCount, memory, context);
                return true;
            case 2:
                parsed = new VllmServeCommandArguments(configPath, values[0], values[1], deploymentName, revision, requestedGpuCount, memory, context);
                return true;
            case 3 when deploymentName is null:
                parsed = new VllmServeCommandArguments(configPath, values[0], values[1], values[2], revision, requestedGpuCount, memory, context);
                return true;
            default:
                Console.Error.WriteLine(usage);
                return false;
        }
    }

    private static bool TryParseSetupRegistrationCommand(
        string[] args,
        string usage,
        out SetupRegistrationArguments parsed)
    {
        var defaultConfigPath = GetDefaultConfigPath();
        parsed = new SetupRegistrationArguments(defaultConfigPath, string.Empty, string.Empty);
        if (args.Length is < 3 or > 4)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        var targetIndex = 1;
        var configPath = defaultConfigPath;
        if (args.Length == 4 && LooksLikeConfigPath(args[1]))
        {
            configPath = args[1];
            targetIndex = 2;
        }
        else if (args.Length == 4)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        parsed = new SetupRegistrationArguments(configPath, args[targetIndex], args[targetIndex + 1]);
        return true;
    }

    private static bool TryParseSimpleSshTarget(string sshCommand, out SimpleSshTarget target, out string? error)
    {
        target = new SimpleSshTarget(string.Empty, 22);
        error = null;

        var tokens = sshCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            error = "SSH target is required.";
            return false;
        }

        var index = 0;
        if (tokens[index].Equals("ssh", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        var port = 22;
        if (index < tokens.Length && tokens[index].Equals("-p", StringComparison.Ordinal))
        {
            if (index + 1 >= tokens.Length || !int.TryParse(tokens[index + 1], out port) || port is < 1 or > 65535)
            {
                error = "Invalid ssh port.";
                return false;
            }

            index += 2;
        }
        else if (index < tokens.Length &&
                 tokens[index].StartsWith("-p", StringComparison.Ordinal) &&
                 tokens[index].Length > 2)
        {
            if (!int.TryParse(tokens[index][2..], out port) || port is < 1 or > 65535)
            {
                error = "Invalid ssh port.";
                return false;
            }

            index++;
        }

        if (tokens.Skip(index).Any(token => token.StartsWith("-", StringComparison.Ordinal)))
        {
            error = "Complex SSH options are not supported by setup registration yet. Use a simple form: \"ssh [-p port] <host>\".";
            return false;
        }

        if (tokens.Length - index != 1)
        {
            error = "Setup registration only supports a simple SSH target: \"ssh [-p port] <host>\".";
            return false;
        }

        var host = tokens[index].Trim('\'', '"');
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "SSH host is required.";
            return false;
        }

        target = new SimpleSshTarget(host, port);
        return true;
    }

    private static string? ExtractModelsPathFromMount(string? mountCommand)
    {
        if (string.IsNullOrWhiteSpace(mountCommand))
        {
            return null;
        }

        var parts = mountCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidate = parts.LastOrDefault()?.Trim('\'', '"');
        return candidate is not null && candidate.StartsWith("/", StringComparison.Ordinal)
            ? candidate
            : null;
    }

    private static PodsConfig? LoadExistingOrCreate(
        string path,
        PodsConfigStore store,
        PodsConfigValidator validator,
        out int exitCode)
    {
        exitCode = 0;
        if (!File.Exists(path))
        {
            return new PodsConfig();
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

    private static bool IsActivePod(PodsConfig config, PodDefinition pod) =>
        config.ActivePodId is not null && config.ActivePodId.Equals(pod.Id, StringComparison.OrdinalIgnoreCase);

    private static bool TryConsumeTargetJsonFlag(string[] args, out string[] positionalArgs)
    {
        var values = new List<string>(args);
        var found = false;

        if (values.Count > 1 && values[1].Equals("--json", StringComparison.OrdinalIgnoreCase))
        {
            values.RemoveAt(1);
            found = true;
        }
        else if (values.Count > 2 &&
                 LooksLikeConfigPath(values[1]) &&
                 values[2].Equals("--json", StringComparison.OrdinalIgnoreCase))
        {
            values.RemoveAt(2);
            found = true;
        }

        positionalArgs = values.ToArray();
        return found;
    }

    private static bool TryParseLogsCommand(
        string[] args,
        string usage,
        out ModelCommandArguments parsed)
    {
        parsed = new ModelCommandArguments(GetDefaultConfigPath(), null, []);
        if (args.Length < 2)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        if (!TryConsumeConfigAndPodOptions(args, 1, usage, out var positionalArgs, out var configPath, out var podId, out var hasExplicitConfig))
        {
            return false;
        }

        var valueStart = 1;
        if (!hasExplicitConfig &&
            positionalArgs.Length > valueStart &&
            LooksLikeConfigPath(positionalArgs[valueStart]))
        {
            configPath = positionalArgs[valueStart];
            valueStart++;
        }

        var remaining = positionalArgs.Skip(valueStart).ToArray();
        if (podId is not null)
        {
            if (remaining.Length is < 1 or > 2)
            {
                Console.Error.WriteLine(usage);
                return false;
            }
        }
        else if (remaining.Length is < 1 or > 3)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        parsed = new ModelCommandArguments(configPath, podId, remaining);
        return true;
    }

    private static bool TryParseExecCommand(
        string[] args,
        string usage,
        out ExecCommandArguments parsed)
    {
        parsed = new ExecCommandArguments(GetDefaultConfigPath(), null, []);
        if (args.Length < 2)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        if (!TryConsumeConfigAndPodOptions(args, 1, usage, out var positionalArgs, out var configPath, out var podId, out var hasExplicitConfig))
        {
            return false;
        }

        var valueStart = 1;
        var hasPositionalConfig = false;
        if (!hasExplicitConfig &&
            positionalArgs.Length > valueStart &&
            LooksLikeConfigPath(positionalArgs[valueStart]))
        {
            configPath = positionalArgs[valueStart];
            valueStart++;
            hasPositionalConfig = true;
        }

        var remaining = positionalArgs.Skip(valueStart).ToArray();
        if (!string.IsNullOrWhiteSpace(podId))
        {
            if (remaining.Length < 1)
            {
                Console.Error.WriteLine(usage);
                return false;
            }

            parsed = new ExecCommandArguments(configPath, podId, remaining);
            return true;
        }

        if (hasExplicitConfig)
        {
            if (remaining.Length < 1)
            {
                Console.Error.WriteLine(usage);
                return false;
            }

            parsed = new ExecCommandArguments(configPath, null, remaining);
            return true;
        }

        if (hasPositionalConfig)
        {
            if (remaining.Length >= 2)
            {
                parsed = new ExecCommandArguments(configPath, remaining[0], remaining.Skip(1).ToArray());
                return true;
            }

            Console.Error.WriteLine(usage);
            return false;
        }

        if (remaining.Length >= 2)
        {
            parsed = new ExecCommandArguments(configPath, remaining[0], remaining.Skip(1).ToArray());
            return true;
        }

        Console.Error.WriteLine(usage);
        return false;
    }

    private static bool TryParseShellCommand(
        string[] args,
        string usage,
        out ShellCommandArguments parsed)
    {
        parsed = new ShellCommandArguments(GetDefaultConfigPath(), null);
        if (args.Length < 1)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        if (!TryConsumeConfigAndPodOptions(args, 1, usage, out var positionalArgs, out var configPath, out var podId, out var hasExplicitConfig))
        {
            return false;
        }

        var valueStart = 1;
        if (!hasExplicitConfig &&
            positionalArgs.Length > valueStart &&
            LooksLikeConfigPath(positionalArgs[valueStart]))
        {
            configPath = positionalArgs[valueStart];
            valueStart++;
        }

        var remaining = positionalArgs.Skip(valueStart).ToArray();
        if (!string.IsNullOrWhiteSpace(podId))
        {
            if (remaining.Length != 0)
            {
                Console.Error.WriteLine(usage);
                return false;
            }

            parsed = new ShellCommandArguments(configPath, podId);
            return true;
        }

        switch (remaining.Length)
        {
            case 0:
                parsed = new ShellCommandArguments(configPath, null);
                return true;
            case 1:
                parsed = new ShellCommandArguments(configPath, remaining[0]);
                return true;
            default:
                Console.Error.WriteLine(usage);
                return false;
        }
    }

    private static bool TryParseAgentCommand(
        string[] args,
        string usage,
        out AgentCommandArguments parsed)
    {
        parsed = new AgentCommandArguments(GetDefaultConfigPath(), null, string.Empty, []);
        if (args.Length < 2)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        if (!TryConsumeConfigAndPodOptions(args, 1, usage, out var positionalArgs, out var configPath, out var podId, out var hasExplicitConfig))
        {
            return false;
        }

        var valueStart = 1;
        if (!hasExplicitConfig &&
            positionalArgs.Length > valueStart &&
            LooksLikeConfigPath(positionalArgs[valueStart]))
        {
            configPath = positionalArgs[valueStart];
            valueStart++;
        }

        var remaining = positionalArgs.Skip(valueStart).ToArray();
        if (remaining.Length < 1)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        parsed = new AgentCommandArguments(configPath, podId, remaining[0], remaining.Skip(1).ToArray());
        return true;
    }

    private static bool TryResolveLogsTarget(
        PodsConfig config,
        ModelCommandArguments parsed,
        out string podId,
        out string deploymentName,
        out int tail,
        out bool tailSpecified)
    {
        podId = string.Empty;
        deploymentName = string.Empty;
        tail = 100;
        tailSpecified = false;

        if (!string.IsNullOrWhiteSpace(parsed.PodId))
        {
            podId = parsed.PodId;
            deploymentName = parsed.Values[0];
            if (!TryParseTail(parsed.Values, 1, out tail, out tailSpecified))
            {
                return false;
            }

            return true;
        }

        switch (parsed.Values.Count)
        {
            case 1:
                if (!TryResolveActivePodId(config, out podId))
                {
                    return false;
                }

                deploymentName = parsed.Values[0];
                return true;
            case 2:
                if (ContainsPod(config, parsed.Values[0]))
                {
                    podId = parsed.Values[0];
                    deploymentName = parsed.Values[1];
                    return true;
                }

                podId = parsed.Values[0];
                deploymentName = parsed.Values[1];
                return true;
            case 3:
                podId = parsed.Values[0];
                deploymentName = parsed.Values[1];
                if (!TryParseTailValue(parsed.Values[2], out tail))
                {
                    return false;
                }

                tailSpecified = true;
                return true;
            default:
                Console.Error.WriteLine("Usage: logs [--json] [--follow|-f] [--config path] [--pod pod-id] <deployment-name> [tail] or logs [--json] [--follow|-f] [path] <pod-id> <deployment-name> [tail]");
                return false;
        }
    }

    private static bool TryResolveActivePodId(PodsConfig config, out string podId)
    {
        podId = config.ActivePodId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(podId))
        {
            Console.Error.WriteLine("No active pod. Use 'active <pod-id>' to set one.");
            return false;
        }

        return true;
    }

    private static bool ContainsPod(PodsConfig config, string podId) =>
        config.Pods.Any(pod => pod.Id.Equals(podId, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseTailValue(string value, out int tail)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out tail) || tail <= 0)
        {
            Console.Error.WriteLine($"Invalid tail value: {value}");
            return false;
        }

        return true;
    }

    private static bool TryParseTail(IReadOnlyList<string> values, int index, out int tail, out bool tailSpecified)
    {
        tail = 100;
        tailSpecified = false;
        if (values.Count <= index)
        {
            return true;
        }

        tailSpecified = true;
        return TryParseTailValue(values[index], out tail);
    }

    private static int ConsumeIntOption(
        string[] args,
        string option,
        int startIndex,
        int defaultValue,
        out string[] positionalArgs,
        out string? error)
    {
        error = null;
        var value = defaultValue;
        var values = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (i >= startIndex && args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value) || value < 0)
                {
                    error = $"Invalid {option} value.";
                    positionalArgs = args;
                    return defaultValue;
                }

                i++;
                continue;
            }

            values.Add(args[i]);
        }

        positionalArgs = values.ToArray();
        return value;
    }

    private static int? ConsumePositiveIntOption(
        string[] args,
        string option,
        int startIndex,
        out string[] positionalArgs,
        out string? error)
    {
        error = null;
        int? value = null;
        var values = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (i >= startIndex && args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length ||
                    !int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
                    parsed <= 0)
                {
                    error = $"Invalid {option} value.";
                    positionalArgs = args;
                    return null;
                }

                value = parsed;
                i++;
                continue;
            }

            values.Add(args[i]);
        }

        positionalArgs = values.ToArray();
        return value;
    }

    private static bool TryParseTargetCommand(
        string[] args,
        int minValueCount,
        string usage,
        out TargetCommandArguments parsed)
    {
        var defaultConfigPath = GetDefaultConfigPath();
        parsed = new TargetCommandArguments(defaultConfigPath, string.Empty, []);
        if (args.Length < 2 + minValueCount)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        var targetIndex = 1;
        var configPath = defaultConfigPath;
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
        var defaultConfigPath = GetDefaultConfigPath();
        parsed = new TargetCommandArguments(defaultConfigPath, string.Empty, []);
        if (args.Length < 3 + minValueCount)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        var targetIndex = 2;
        var configPath = defaultConfigPath;
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

    private static bool TryParseModelCommandWithOptionalPod(
        string[] args,
        int minValueCount,
        string usage,
        out ModelCommandArguments parsed)
    {
        parsed = new ModelCommandArguments(GetDefaultConfigPath(), null, []);
        if (args.Length < 2)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        if (!TryConsumeConfigAndPodOptions(args, 2, usage, out var positionalArgs, out var configPath, out var podId, out var hasExplicitConfig))
        {
            return false;
        }

        var valueStart = 2;
        if (!hasExplicitConfig &&
            positionalArgs.Length > valueStart &&
            LooksLikeConfigPath(positionalArgs[valueStart]))
        {
            configPath = positionalArgs[valueStart];
            valueStart++;
        }

        var remaining = positionalArgs.Skip(valueStart).ToArray();
        IReadOnlyList<string> values;
        if (podId is not null)
        {
            if (remaining.Length != minValueCount)
            {
                Console.Error.WriteLine(usage);
                return false;
            }

            values = remaining;
        }
        else if (minValueCount == 0)
        {
            podId = remaining.Length > 0 ? remaining[0] : null;
            values = remaining.Length > 0 ? remaining.Skip(1).ToArray() : [];
        }
        else
        {
            if (remaining.Length < minValueCount)
            {
                Console.Error.WriteLine(usage);
                return false;
            }

            if (remaining.Length == minValueCount)
            {
                values = remaining;
            }
            else
            {
                podId = remaining[0];
                values = remaining.Skip(1).ToArray();
            }
        }

        if (values.Count < minValueCount)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        parsed = new ModelCommandArguments(configPath, podId, values);
        return true;
    }

    private static bool TryParseTargetCommandWithOptionalPod(
        string[] args,
        int valueStart,
        int minValueCount,
        string usage,
        out ModelCommandArguments parsed)
    {
        parsed = new ModelCommandArguments(GetDefaultConfigPath(), null, []);
        if (args.Length < valueStart)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        if (!TryConsumeConfigAndPodOptions(args, valueStart, usage, out var positionalArgs, out var configPath, out var podId, out var hasExplicitConfig))
        {
            return false;
        }

        if (!hasExplicitConfig &&
            positionalArgs.Length > valueStart &&
            LooksLikeConfigPath(positionalArgs[valueStart]))
        {
            configPath = positionalArgs[valueStart];
            valueStart++;
        }

        var remaining = positionalArgs.Skip(valueStart).ToArray();
        IReadOnlyList<string> values;
        if (podId is not null)
        {
            if (remaining.Length != minValueCount)
            {
                Console.Error.WriteLine(usage);
                return false;
            }

            values = remaining;
        }
        else if (minValueCount == 0)
        {
            podId = remaining.Length > 0 ? remaining[0] : null;
            values = remaining.Length > 0 ? remaining.Skip(1).ToArray() : [];
        }
        else
        {
            if (remaining.Length < minValueCount)
            {
                Console.Error.WriteLine(usage);
                return false;
            }

            if (remaining.Length == minValueCount)
            {
                values = remaining;
            }
            else
            {
                podId = remaining[0];
                values = remaining.Skip(1).ToArray();
            }
        }

        if (values.Count < minValueCount)
        {
            Console.Error.WriteLine(usage);
            return false;
        }

        parsed = new ModelCommandArguments(configPath, podId, values);
        return true;
    }

    private static bool TryParseConfigCommand(string[] args, string usage, out string configPath)
    {
        configPath = GetDefaultConfigPath();
        if (args.Length == 1)
        {
            return true;
        }

        if (args.Length == 2)
        {
            configPath = args[1];
            return true;
        }

        Console.Error.WriteLine(usage);
        return false;
    }

    private static bool LooksLikeConfigPath(string value) =>
        File.Exists(value) ||
        value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase) ||
        value.Contains('/') ||
        value.Contains('\\');

    private static string GetPodTransport(PodDefinition pod)
    {
        if (!string.IsNullOrWhiteSpace(pod.Endpoint))
        {
            return "http";
        }

        if (!string.IsNullOrWhiteSpace(pod.SshHost))
        {
            return "ssh";
        }

        return "none";
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Tau.Pods commands:");
        Console.WriteLine("  init [--json] [path]           Create a sample pod config");
        Console.WriteLine("  pods [--json] [path]           List configured pods (* = active)");
        Console.WriteLine("  pods setup [--json] [--mount <command>] [--models-path <path>] [--vllm release|nightly|gpt-oss] [path] <id> \"ssh [-p port] <host>\" Register a pod");
        Console.WriteLine("  pods active [--json] [path] <id> Switch active pod");
        Console.WriteLine("  pods remove [--json] [path] <id> Remove pod from local config");
        Console.WriteLine("  list [--json] [--config path] [--pod id] List running vLLM deployments on the active or specified pod");
        Console.WriteLine("  validate [--json] [path]       Validate pod config");
        Console.WriteLine("  status [--json] [path]         Print enabled/disabled and transport summary");
        Console.WriteLine("  probe [--json] [path]          Probe enabled pod endpoints or ssh/tcp targets");
        Console.WriteLine("  health [--json] [path]         Check health of all enabled pods");
        Console.WriteLine("  exec [--json] [--config path] [--pod id] <command> Execute a remote command on the active or specified ssh pod");
        Console.WriteLine("  ssh [--json] [--config path] [--pod id] <command> Alias for exec");
        Console.WriteLine("  shell [--json] [--config path] [--pod id] [pod-id] Open an SSH shell on the active or specified pod");
        Console.WriteLine("  start <model> --name <name> [--json] [--config path] [--pod id] [--memory percent] [--context size] [--gpus n] [--vllm <args...>] Start a vLLM deployment");
        Console.WriteLine("  deploy [--json] [path] <id> <model> [name] Deploy a model to a pod");
        Console.WriteLine("  stop [--json] [--config path] [--pod id] [name] Stop a vLLM deployment, or all deployments if no name is given");
        Console.WriteLine("  restart [--json] [path] <id> <name> Restart a deployment on a pod");
        Console.WriteLine("  logs [--json] [--follow|-f] [--config path] [--pod id] <name> [tail] Fetch or stream deployment logs from the active or specified pod");
        Console.WriteLine("  agent [--config path] [--pod id] <name> [message/options...] Build the upstream pod-agent prompt mapping; execution is still open");
        Console.WriteLine("  deployments [--json] [--config path] [--pod id] List deployments on a pod");
        Console.WriteLine("  setup plan [--json] [--mount <command>] [--models-path <path>] [--vllm release|nightly|gpt-oss] [path] <id> Print a plan-only pod setup command");
        Console.WriteLine("  setup run [--json] [--script <path>] [--mount <command>] [--models-path <path>] [--vllm release|nightly|gpt-oss] [path] <id> Execute pod setup over ssh/scp");
        Console.WriteLine("  model list [--json] [--config path] [--pod id] List cached models on an ssh pod");
        Console.WriteLine("  model pull [--json] [--config path] [--pod id] [--revision rev] [--snapshot rev] <model> Pull a Hugging Face model on an ssh pod");
        Console.WriteLine("  model remove [--json] [--config path] [--pod id] <model> Remove a cached model from an ssh pod");
        Console.WriteLine("  model status [--json] [--config path] [--pod id] <model> Check whether a model is cached");
        Console.WriteLine("  vllm plan [--json] [--config path] [--pod id] <model> [--name name] [--revision rev] [--snapshot rev] [--gpus n] [--memory percent] [--context size] [--vllm <args...>] Print a plan-only vLLM serve command");
        Console.WriteLine("  vllm preflight [--json] [--config path] [--pod id] <model> [--name name] [--revision rev] [--snapshot rev] Resolve remote HF snapshot path and vLLM availability");
        Console.WriteLine("  vllm deploy [--json] [--no-health] [--prefetch] [--health-attempts n] [--health-backoff-ms n] [--config path] [--pod id] <model> [--name name] [--revision rev] [--snapshot rev] [--gpus n] [--memory percent] [--context size] [--vllm <args...>] Execute a vLLM deploy plan on an ssh pod");
        Console.WriteLine("  vllm status [--json] [--config path] [--pod id] <name> Fetch remote vLLM deployment status");
        Console.WriteLine("  vllm health [--json] [--health-attempts n] [--health-backoff-ms n] [--config path] [--pod id] <name> Check remote vLLM /health readiness");
        Console.WriteLine("  vllm stop [--json] [--config path] [--pod id] <name> Stop a remote vLLM deployment");
        Console.WriteLine("  vllm rollback [--json] [--config path] [--pod id] <name> Roll back a remote vLLM deployment");
        Console.WriteLine("Environment:");
        Console.WriteLine("  PI_CONFIG_DIR                  Config directory; default is ~/.pi");
        Console.WriteLine("  Default config                 <config-dir>/pods.json");
    }

    private static void PrintPodsHelp()
    {
        Console.WriteLine("Tau.Pods pod commands:");
        Console.WriteLine("  pods [--json] [path]           List configured pods (* = active)");
        Console.WriteLine("  pods setup [--json] [--mount <command>] [--models-path <path>] [--vllm release|nightly|gpt-oss] [path] <id> \"ssh [-p port] <host>\"");
        Console.WriteLine("  pods active [--json] [path] <id>");
        Console.WriteLine("  pods remove [--json] [path] <id>");
    }

    private sealed record TargetCommandArguments(string ConfigPath, string PodId, IReadOnlyList<string> Values);

    private sealed record ModelCommandArguments(string ConfigPath, string? PodId, IReadOnlyList<string> Values);

    private sealed record StopCompatCommandArguments(string ConfigPath, string? PodId, IReadOnlyList<string> Values, bool JsonOutput);

    private sealed record ExecCommandArguments(string ConfigPath, string? PodId, IReadOnlyList<string> CommandParts);

    private sealed record ShellCommandArguments(string ConfigPath, string? PodId);

    private sealed record AgentCommandArguments(string ConfigPath, string? PodId, string ModelName, IReadOnlyList<string> UserArgs);

    private sealed record PodAgentPromptPlan(
        string PodId,
        string ModelName,
        string UpstreamModel,
        int Port,
        string BaseUrl,
        string Api,
        string ApiKeySource,
        string SystemPrompt,
        IReadOnlyList<string> AgentArgs);

    private sealed record VllmServeCommandArguments(
        string ConfigPath,
        string? PodId,
        string ModelId,
        string? DeploymentName,
        string? Revision,
        int? RequestedGpuCount,
        string? Memory,
        string? Context);

    private sealed record SetupRegistrationArguments(string ConfigPath, string PodId, string SshCommand);

    private sealed record SimpleSshTarget(string Host, int Port);
}
