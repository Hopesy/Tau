# [2026-06-08 09:47] | Task: Pods runner HF download command baseline

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Windows PowerShell / .NET 10

### User Query

> 按照 `GOAL.md` 继续推进 Tau 100% pi-mono parity 迁移。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **Runner download contract**: Updated generated `model_run_<name>.sh` to run upstream-style `HF_HUB_ENABLE_HF_TRANSFER=1 hf download "$MODEL_ID"` after the runner header, with stable failure and success markers.
* **PI API key contract**: Updated planned `vllm serve` command to include `--api-key "$PI_API_KEY"` before served model name and extra vLLM args.
* **Regression coverage**: Extended planner and orchestration tests to assert the uploaded runner command contains HF download markers and the PI API key argument.
* **Docs sync**: Updated `GOAL.md`, active parity matrix/plan, `next.md` and `docs/QUALITY_SCORE.md` to mark the local command contract closed while keeping real remote e2e gaps open.

### Design Intent

This keeps Tau's generated vLLM runner closer to upstream `packages/pods/scripts/model_run.sh` without claiming remote success from fake-runner tests. The local contract now proves the command text Tau uploads to a pod includes model download and API-key startup semantics; real HF download, PI API key environment, GPU/vLLM startup and rollback behavior still require external smoke evidence.

### Files Modified

* `src/Tau.Pods/Services/PodVllmCommandPlanner.cs`
* `tests/Tau.Pods.Tests/PodVllmCommandPlannerTests.cs`
* `tests/Tau.Pods.Tests/PodVllmOrchestrationServiceTests.cs`
* `GOAL.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`

### Validation

* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "PodVllmCommandPlannerTests|DeployAsync|VllmDeploy|VllmPlan|VllmRollback" --no-restore --verbosity minimal` -> 65/65 passed.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` -> 214/214 passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed (`Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 438, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 214).

### Remaining Boundaries

* No real remote HF download was executed in this slice.
* No real SSH/SCP/GPU/vLLM startup smoke was executed in this slice.
* Save-before-watch/delete-config exact ordering and multi-version rollback remain open.
