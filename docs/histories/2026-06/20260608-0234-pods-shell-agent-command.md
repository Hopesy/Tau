## [2026-06-08 02:34] | Task: Pods shell and agent prompt command compatibility

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### User Query

> 按照 `GOAL.md` 继续 Tau 100% pi-mono parity。

### Changes Overview

**Scope:** `Tau.Pods` top-level command compatibility.

**Key Actions:**

* **[Shell command]**: Added top-level `shell [--json] [--config path] [--pod pod-id] [pod-id]`. It resolves the active or explicit pod and opens an SSH process with no remote command through `PodExecService.OpenShellAsync`.
* **[Agent prompt mapping]**: Added top-level `agent [--config path] [--pod pod-id] <name> [message/options...]`. It reads the configured pod model, builds upstream-style CodingAgent arguments (`--base-url`, `--model`, redacted `--api-key`, `--api`, `--system-prompt`) and preserves passthrough user args such as `--json`.
* **[Safety boundary]**: The `agent` command intentionally returns a stable not-implemented failure instead of pretending to run a working pod-agent runtime, matching the fact that upstream `prompt.ts` currently builds args and then throws `Not implemented`.
* **[Regression coverage]**: Added PodsCli tests for shell SSH argv, non-redirected shell process setup, agent prompt args, `PI_API_KEY` redaction and unknown configured model errors.
* **[Docs sync]**: Updated `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md` and the active parity plan/matrix to move shell and agent prompt argument mapping out of the local command-contract gap while keeping usable pod-agent runtime, real remote e2e and startup/log parity open.

### Design Intent

Upstream `pi shell` is a direct interactive SSH process, so Tau should not add another SSH stack or wrap it through remote shell text. Reusing `PodExecService` keeps local argv construction, injected-runner testing and structured failures in one place.

Upstream `pi agent` exposes a user-visible command but its body still throws after constructing CodingAgent arguments. Tau now ports that argument-construction contract and redacts secrets from its audit output, but does not claim product runtime parity until a real pod-agent execution path is wired or explicitly declared non-goal.

### Files Modified

* `src/Tau.Pods/Services/PodExecService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`

### Validation

* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "PodsCli" --no-restore --verbosity minimal` passed `89/89`.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` passed `201/201`.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed with `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 438, `Tau.WebUi.Tests` 44, and `Tau.Pods.Tests` 201.
