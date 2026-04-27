using System.Diagnostics;
using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Cli;

public static class PodsCli
{
    private const string DefaultConfigPath = "tau.pods.json";

    public static async Task<int> RunAsync(string[] args)
    {
        var store = new PodsConfigStore();
        var validator = new PodsConfigValidator();
        var probeService = new PodProbeService();
        var execService = new PodExecService();

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
            _ => Unknown(command)
        };
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
        if (args.Length < 4)
        {
            Console.Error.WriteLine("Usage: exec [path] <pod-id> <command>");
            return 1;
        }

        var config = LoadOrReport(path, store, validator, out var exitCode);
        if (config is null) return exitCode;

        var podId = args[2];
        var command = string.Join(' ', args.Skip(3));
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

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 1;
    }

    private static bool IsHelp(string arg) => arg is "help" or "--help" or "-h";

    private static void PrintHelp()
    {
        Console.WriteLine("Tau.Pods commands:");
        Console.WriteLine("  init [path]          Create a sample tau.pods.json");
        Console.WriteLine("  list [path]          List configured pods");
        Console.WriteLine("  validate [path]      Validate pod config");
        Console.WriteLine("  status [path]        Print enabled/disabled and transport summary");
        Console.WriteLine("  probe [path]         Probe enabled pod endpoints or ssh/tcp targets");
        Console.WriteLine("  exec [path] <id> <command>  Execute a remote command on an ssh pod");
    }
}
