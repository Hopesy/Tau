# [2026-06-08 10:14] | Task: Pods startup config ordering baseline

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Windows PowerShell / .NET 10

### User Query

> 按照 `GOAL.md` 继续推进 Tau 100% pi-mono parity 迁移。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **Remote-start seam**: Added an optional `onRemoteStarted` callback to `PodVllmOrchestrationService.DeployAsync` so callers can observe the exact point after remote start succeeds and before startup watcher / health checks run.
* **Config ordering**: Updated `PodsCli` to write configured model state immediately after remote start, then remove that state if startup watcher or health readiness fails.
* **Regression coverage**: Added service-level and CLI-level tests proving config exists before startup watcher / health commands execute and is removed after rollback on failure.
* **Docs sync**: Updated `GOAL.md`, active parity matrix/plan, `next.md` and `docs/QUALITY_SCORE.md` to mark the local save-before-watch / delete-on-failure ordering contract closed while keeping real remote e2e gaps open.

### Design Intent

Upstream `startModel()` saves `config.pods[podName].models[name]` as soon as the remote model runner starts, then tails startup logs and deletes the config entry on startup failure. Tau already had the same final failure state, but not the same observable ordering. The new seam keeps configuration ownership in the CLI/store layer instead of coupling the orchestration service to config persistence.

### Files Modified

* `src/Tau.Pods/Services/PodVllmOrchestrationService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodVllmOrchestrationServiceTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `GOAL.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`

### Validation

* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "VllmDeploy_WhenStartupWatcherFails|VllmDeploy_WhenHealthFails|VllmDeploy_Success_UpdatesConfiguredModelStateForGpuAllocation|DeployAsync_CallsRemoteStartedCallbackBeforeStartupWatch" --no-restore --verbosity minimal` -> 4/4 passed.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` -> 215/215 passed.
* `git diff --check` -> passed with CRLF normalization warnings only.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed (`Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 438, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 215).

### Remaining Boundaries

* No real remote SSH/HF/GPU/vLLM startup smoke was executed in this slice.
* Multi-version rollback remains open.
* Long-running remote transport hardening remains open.
