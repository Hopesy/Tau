using System.Text;
using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodVllmCommandPlanner
{
    private const string DefaultModelsPath = "$HOME/.cache/huggingface/hub";

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
        var modelCachePath = BuildModelCachePath(pod, options.ModelId);
        var hasResolvedModelPath = !string.IsNullOrWhiteSpace(options.ResolvedModelPath);
        var usesSnapshotDiscovery = !hasResolvedModelPath;
        var modelPath = hasResolvedModelPath ? options.ResolvedModelPath!.Trim() : modelCachePath;
        var unitName = $"tau-pod-{deploymentName}.service";
        var serveCommand = hasResolvedModelPath
            ? BuildServeCommand(modelPath, port, servedModelName, options.Environment, options.ExtraArgs)
            : BuildSnapshotDiscoveryServeCommand(modelCachePath, port, servedModelName, options.Environment, options.ExtraArgs, revision);
        var unit = BuildSystemdUnit(unitName, serveCommand);
        var metadata = BuildMetadataJson(options.ModelId.Trim(), deploymentName, modelPath, usesSnapshotDiscovery, port, servedModelName, unitName, revision);
        var remoteCommand =
            $"mkdir -p ~/.tau_pods && " +
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
            Revision: revision);
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

    private static string BuildSystemdUnit(string unitName, string serveCommand) =>
        "[Unit]\n" +
        $"Description=Tau Pods vLLM runner {unitName}\n" +
        "After=network-online.target\n\n" +
        "[Service]\n" +
        "Type=simple\n" +
        $"ExecStart=/usr/bin/env bash -lc {ShellSingleQuote(serveCommand)}\n" +
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
        string? revision) =>
        new StringBuilder()
            .Append("{\"model\":\"").Append(EscapeJsonString(modelId))
            .Append("\",\"name\":\"").Append(EscapeJsonString(deploymentName))
            .Append("\",\"status\":\"planned-vllm\"")
            .Append(",\"modelPath\":\"").Append(EscapeJsonString(modelPath))
            .Append("\",\"usesSnapshotDiscovery\":").Append(usesSnapshotDiscovery ? "true" : "false")
            .Append(",\"port\":").Append(port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(",\"servedModelName\":\"").Append(EscapeJsonString(servedModelName))
            .Append("\",\"unit\":\"").Append(EscapeJsonString(unitName))
            .Append("\"")
            .Append(revision is null ? string.Empty : ",\"revision\":\"" + EscapeJsonString(revision) + "\"")
            .Append(",\"ts\":\"").Append(EscapeJsonString(DateTimeOffset.UtcNow.ToString("O"))).Append("\"}")
            .ToString();

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
