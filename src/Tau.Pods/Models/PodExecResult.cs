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
    string Summary);
