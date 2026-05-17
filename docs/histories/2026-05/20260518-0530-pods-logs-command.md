# Tau.Pods /logs command

Date: 2026-05-18

## Summary

Extended `Tau.Pods` lifecycle commands with `/logs`. Operators can now fetch the last N lines (default 100) of a deployment's logs from an SSH-backed pod without having to open a remote shell. The remote command prefers `journalctl -u tau-pod-<name>` (the systemd unit convention used by Tau-deployed pods) and falls back to `tail -n N ~/.tau_pods/<name>.log` when journald isn't available.

## Changes

- `src/Tau.Pods/Models/PodLifecycleResults.cs`: new `PodLogsResult(PodId, Success, Summary, Output)` record so the CLI can distinguish summary status from the actual log body.
- `src/Tau.Pods/Services/PodLifecycleService.cs`: new `LogsAsync(pod, deploymentName, tail = 100, cancellationToken)` that:
  - rejects non-SSH pods with a clear summary;
  - normalizes the deployment name with the existing helper (no shell-metachar leakage);
  - builds a single-line shell pipeline that tries journalctl first, falls back to `tail` on the per-deployment log file, and exits non-zero with `no logs available` when neither is present;
  - returns the trimmed stdout as `Output` plus a summary that includes the matched line count.
- `src/Tau.Pods/Cli/PodsCli.cs`: new `logs [path] <pod-id> <deployment-name> [tail]` subcommand with the existing target-command parser; help text updated; invalid tail values are rejected before any remote work.
- `tests/Tau.Pods.Tests/PodLifecycleServiceTests.cs`: 4 new tests covering journalctl command construction, the default-100 fallback, the non-SSH rejection, and exec-failure summary surfacing.
- `tests/Tau.Pods.Tests/PodsCliTests.cs`: 2 new tests covering the CLI rejecting non-SSH pods and the tail-value validation.
- Synced `next.md` (Tau.Pods lifecycle item) and the active port plan (current-facts + verification list).

## Verification

- `dotnet build .\src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` — 24 tests passed (18 prior + 6 new).

## Decisions

- Picked `tau-pod-<name>` as the journalctl unit. Tau's existing deploy path writes deployment metadata to `~/.tau_pods/<name>.json`; mirroring the naming for the systemd unit gives operators a single deterministic identifier to find their service's logs without inspecting the metadata first.
- Used `command -v journalctl >/dev/null 2>&1` as the gate. systemd is the dominant init on the Linux fleets Tau targets, but Tau also runs on lightweight VMs / containers that use file-based logs; the fallback to `~/.tau_pods/<name>.log` keeps the command useful in both worlds without requiring per-pod configuration.
- Surfaced exec exit-status in `Summary` and the captured stderr/stdout in `Output`. Operators usually want both signals together — "journalctl found nothing" looks different from "ssh refused", and shipping the body unmodified lets them distinguish without adding a second roundtrip.
- Capped tail validation to "must parse as a positive integer" rather than imposing a Tau-side ceiling. Operators sometimes want huge tail counts when diagnosing a crash; the remote `journalctl`/`tail` will handle whatever the shell can deliver.
- Did **not** stream logs (follow / `-f`). Streaming requires keeping the SSH transport open and surfacing partial output through the CLI, which is a bigger interaction model change. The bulk-fetch shape is enough for the typical "what happened recently?" question and can layer on a `--follow` flag later.
