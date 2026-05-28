namespace Tau.Pods.Models;

public sealed record PodSetupPlanOptions(
    string? MountCommand = null,
    string? ModelsPath = null,
    string? VllmVersion = null);

public sealed record PodSetupRunOptions(
    string? MountCommand = null,
    string? ModelsPath = null,
    string? VllmVersion = null,
    string? ScriptPath = null);

public sealed record PodSetupPlan(
    string PodId,
    string SshHost,
    int SshPort,
    string ModelsPath,
    string? MountCommand,
    string VllmVersion,
    bool HfTokenConfigured,
    bool PiApiKeyConfigured,
    string ScriptRemotePath,
    string SetupCommand,
    IReadOnlyList<string> Steps,
    bool IsPlanOnly = true);

public sealed record PodSetupRunResult(
    string PodId,
    bool Success,
    string Summary,
    PodSetupPlan Plan,
    string? ScriptPath,
    IReadOnlyList<PodSetupExecutionStep> Steps,
    IReadOnlyList<PodGpuInfo> Gpus);

public sealed record PodSetupExecutionStep(
    string Name,
    bool Success,
    string Summary,
    string DisplayCommand,
    int ExitCode,
    string StdOut,
    string StdErr,
    TimeSpan Duration);

public sealed record PodGpuInfo(
    int Id,
    string Name,
    string Memory);
