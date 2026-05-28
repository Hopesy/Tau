namespace Tau.Pods.Models;

public sealed record PodExecResult(
    string PodId,
    bool Success,
    string Transport,
    string Command,
    string Target,
    int ExitCode,
    string StdOut,
    string StdErr,
    TimeSpan Duration,
    string Summary,
    string FailureKind = PodExecFailureKinds.Unknown);

public static class PodExecFailureKinds
{
    public const string None = "none";
    public const string Unknown = "unknown";
    public const string UnsupportedTransport = "unsupported-transport";
    public const string SshProcessStartFailed = "ssh-process-start-failed";
    public const string SshProcessRunnerFailed = "ssh-process-runner-failed";
    public const string SshExecCancelled = "ssh-exec-cancelled";
    public const string SshExecFailed = "ssh-exec-failed";
    public const string SshAuthFailed = "ssh-auth-failed";
    public const string SshHostKeyFailed = "ssh-host-key-failed";
    public const string SshHostUnresolved = "ssh-host-unresolved";
    public const string SshConnectTimeout = "ssh-connect-timeout";
    public const string SshConnectionFailed = "ssh-connection-failed";
    public const string SshExecNotAttempted = "ssh-exec-not-attempted";

    public static string FromResult(PodExecResult? result, string fallback = SshExecFailed)
    {
        if (result is null || result.Success)
        {
            return None;
        }

        if (!string.IsNullOrWhiteSpace(result.FailureKind) &&
            !result.FailureKind.Equals(Unknown, StringComparison.OrdinalIgnoreCase))
        {
            return result.FailureKind;
        }

        return result.Summary switch
        {
            "ssh process start failed" => SshProcessStartFailed,
            "ssh process runner failed" => SshProcessRunnerFailed,
            "ssh exec cancelled" => SshExecCancelled,
            "ssh exec not attempted" => SshExecNotAttempted,
            _ => fallback
        };
    }
}
