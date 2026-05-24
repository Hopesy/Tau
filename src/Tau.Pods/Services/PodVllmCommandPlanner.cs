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
        var modelPath = string.IsNullOrWhiteSpace(options.ResolvedModelPath)
            ? BuildModelCachePath(pod, options.ModelId)
            : options.ResolvedModelPath.Trim();
        var unitName = $"tau-pod-{deploymentName}.service";
        var serveCommand = BuildServeCommand(modelPath, port, servedModelName, options.Environment, options.ExtraArgs);
        var unit = BuildSystemdUnit(unitName, serveCommand);
        var metadata = BuildMetadataJson(options.ModelId.Trim(), deploymentName, modelPath, port, servedModelName, unitName);
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
            remoteCommand);
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

        builder
            .Append("vllm serve ")
            .Append(ShellSingleQuote(modelPath))
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
        int port,
        string servedModelName,
        string unitName) =>
        "{\"model\":\"" + EscapeJsonString(modelId) +
        "\",\"name\":\"" + EscapeJsonString(deploymentName) +
        "\",\"status\":\"planned-vllm\"" +
        ",\"modelPath\":\"" + EscapeJsonString(modelPath) +
        "\",\"port\":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"servedModelName\":\"" + EscapeJsonString(servedModelName) +
        "\",\"unit\":\"" + EscapeJsonString(unitName) +
        "\",\"ts\":\"" + EscapeJsonString(DateTimeOffset.UtcNow.ToString("O")) + "\"}";

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
}
