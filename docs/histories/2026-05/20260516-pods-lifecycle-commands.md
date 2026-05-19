# Tau.Pods deploy/stop/restart/health lifecycle commands

Date: 2026-05-16

## Summary

Added deploy/stop/restart/health lifecycle commands to Tau.Pods, closing the core pod management gap.

## Changes

- `src/Tau.Pods/Services/PodLifecycleService.cs` — new service with `HealthAsync`, `DeployAsync`, `StopAsync`, `RestartAsync`
- `src/Tau.Pods/Models/PodLifecycleResults.cs` — `PodHealthResult`, `PodDeployResult`, `PodStopResult` records
- `src/Tau.Pods/Cli/PodsCli.cs` — added `health`, `deploy`, `stop`, `restart` CLI commands with argument parsing
- `tests/Tau.Pods.Tests/PodsCliTests.cs` — added CLI argument regression coverage for default `tau.pods.json` and explicit config paths.

### Health check
- HTTP pods: GET `/health` endpoint with timeout and latency measurement
- SSH pods: execute `echo ok` and measure round-trip

### Deploy/Stop/Restart
- SSH-based: writes/removes deployment metadata to `~/.tau_pods/{name}.json` on remote host
- HTTP pods: returns unsupported (deploy requires SSH)
- Restart verifies deployment exists before confirming
- Target commands now accept both `deploy <pod-id> <model-id>` using default `tau.pods.json` and `deploy <path> <pod-id> <model-id>` using an explicit config path.
- Deployment names are normalized before use as remote metadata filenames, and lifecycle metadata is shell-quoted before being passed through SSH.

## Verification

- `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore` — 0 errors
- `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` — 18 tests pass
