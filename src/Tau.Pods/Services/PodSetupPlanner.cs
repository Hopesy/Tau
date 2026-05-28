using System.Text;
using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodSetupPlanner
{
    public const string DefaultModelsPath = "$HOME/.cache/huggingface/hub";
    public const string DefaultVllmVersion = "release";
    public const string ScriptRemotePath = "/tmp/pod_setup.sh";

    private static readonly HashSet<string> SupportedVllmVersions = new(StringComparer.OrdinalIgnoreCase)
    {
        "release",
        "nightly",
        "gpt-oss"
    };

    private readonly Func<string, string?> getEnvironmentVariable;

    public PodSetupPlanner(Func<string, string?>? getEnvironmentVariable = null)
    {
        this.getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    public PodSetupPlan Plan(PodDefinition pod, PodSetupPlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(pod);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            throw new InvalidOperationException("Setup requires SSH-based pod.");
        }

        var modelsPath = string.IsNullOrWhiteSpace(options.ModelsPath)
            ? string.IsNullOrWhiteSpace(pod.ModelsPath) ? DefaultModelsPath : pod.ModelsPath.Trim()
            : options.ModelsPath.Trim();
        var vllmVersion = NormalizeVllmVersion(
            string.IsNullOrWhiteSpace(options.VllmVersion) ? pod.VllmVersion : options.VllmVersion);
        var mountCommand = string.IsNullOrWhiteSpace(options.MountCommand) ? null : options.MountCommand.Trim();
        var hfTokenConfigured = !string.IsNullOrWhiteSpace(getEnvironmentVariable("HF_TOKEN"));
        var piApiKeyConfigured = !string.IsNullOrWhiteSpace(getEnvironmentVariable("PI_API_KEY"));
        var setupCommand = BuildSetupCommand(modelsPath, mountCommand, vllmVersion);

        return new PodSetupPlan(
            pod.Id,
            pod.SshHost.Trim(),
            pod.SshPort ?? 22,
            modelsPath,
            mountCommand,
            vllmVersion,
            hfTokenConfigured,
            piApiKeyConfigured,
            ScriptRemotePath,
            setupCommand,
            [
                $"Copy pod setup script to {ScriptRemotePath}",
                "Run setup script with HF_TOKEN and PI_API_KEY from the remote environment",
                "Record vLLM version metadata in the pod config"
            ]);
    }

    public static bool IsSupportedVllmVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ||
        SupportedVllmVersions.Contains(version.Trim());

    public static string NormalizeVllmVersion(string? version)
    {
        var normalized = string.IsNullOrWhiteSpace(version)
            ? DefaultVllmVersion
            : version.Trim().ToLowerInvariant();
        if (!SupportedVllmVersions.Contains(normalized))
        {
            throw new ArgumentException(
                $"Unsupported vLLM version '{version}'. Supported values: release, nightly, gpt-oss.",
                nameof(version));
        }

        return normalized;
    }

    private static string BuildSetupCommand(string modelsPath, string? mountCommand, string vllmVersion)
    {
        var builder = new StringBuilder();
        builder
            .Append("bash ")
            .Append(ShellSingleQuote(ScriptRemotePath))
            .Append(" --models-path ")
            .Append(ShellSingleQuote(modelsPath))
            .Append(" --hf-token \"$HF_TOKEN\"")
            .Append(" --vllm-api-key \"$PI_API_KEY\"")
            .Append(" --vllm ")
            .Append(ShellSingleQuote(vllmVersion));

        if (!string.IsNullOrWhiteSpace(mountCommand))
        {
            builder
                .Append(" --mount ")
                .Append(ShellSingleQuote(mountCommand));
        }

        return builder.ToString();
    }

    private static string ShellSingleQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
