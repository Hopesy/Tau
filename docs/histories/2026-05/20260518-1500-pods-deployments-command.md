# Tau.Pods deployments subcommand

Date: 2026-05-18

## Summary

Added `deployments [path] <pod-id>` to the Tau.Pods CLI, backed by `PodLifecycleService.ListDeploymentsAsync`. The command SSHes into a pod, concatenates every `~/.tau_pods/*.json` metadata file written by `deploy`, parses each as JSON, and lists `name | model | status | ts` per deployment. Symmetric with the existing `deploy/stop/restart/logs` lifecycle commands and required only for the SSH transport (HTTP pods get a clear rejection message).

## Changes

- `src/Tau.Pods/Services/PodLifecycleService.cs`: new `ListDeploymentsAsync(PodDefinition, CancellationToken)`. Builds a shell `for f in ~/.tau_pods/*.json; do [ -f "$f" ] && cat "$f" && echo; done` so missing globs collapse to an empty result and each JSON object lands on its own line; `ParseDeployments` skips blank or non-`{`-prefixed lines and tolerates malformed JSON instead of failing the whole listing. Reports `No deployments` vs `Found N deployment(s)` summaries.
- `src/Tau.Pods/Models/PodLifecycleResults.cs`: `PodDeploymentInfo(Name, Model?, Status?, Timestamp?)` and `PodDeploymentsResult(PodId, Success, Summary, Deployments)` (uncommitted scaffold from prior turn, now consumed).
- `src/Tau.Pods/Cli/PodsCli.cs`: dispatches `deployments`; new `DeploymentsAsync` handles `[path] <pod-id>` parsing without a trailing values list (so it does not share the `TryParseTargetCommand` minValueCount=1 path). Help line registered under `logs`.
- `tests/Tau.Pods.Tests/PodLifecycleServiceTests.cs`: 5 new tests cover non-SSH rejection, two-deployment happy path (with command-arg assertion), empty output, malformed lines being skipped, and failure pass-through.
- `tests/Tau.Pods.Tests/PodsCliTests.cs`: 3 new tests cover non-SSH rejection, missing pod-id usage error, and unknown pod-id reporting.

## Verification

- `dotnet build src/Tau.Pods/Tau.Pods.csproj --verbosity minimal` — succeeded, 0 warnings.
- `dotnet test tests/Tau.Pods.Tests/Tau.Pods.Tests.csproj --verbosity minimal --no-restore` — 32 tests passed (8 new + 24 prior).

## Decisions

- **Shell loop instead of `ls | xargs cat`**: `xargs cat` concatenates without a separator, which would mash adjacent JSON objects into invalid input. `for ...; do cat; echo; done` keeps one record per line and naturally short-circuits to empty when the glob doesn't match — no need for `nullglob`.
- **JSON line skipped, not fatal**: a malformed `~/.tau_pods/<name>.json` produced by an aborted future feature should not break listing the remaining deployments. The parser silently drops bad rows; the surrounding shell still yields a `Success=true` result.
- **CLI parser branched separately from `TryParseTargetCommand`**: that helper requires at least one trailing value (e.g. `<command>`, `<model-id>`, `<deployment-name>`). `deployments` only needs `<pod-id>`, so wiring it through the helper would have meant relaxing `minValueCount=0` and complicating other callers. A small inline parser keeps the shared helper focused.
- **Did not yet parse `ts` into `DateTimeOffset`**: the timestamp is round-tripped as a string for display. Sorting by timestamp / detecting stale deployments can layer on later if real ops feedback asks for it.
