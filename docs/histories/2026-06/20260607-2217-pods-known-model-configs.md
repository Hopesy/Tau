## [2026-06-07 22:17] | Task: port Pods known model configs

### Execution Context

- Agent ID: Codex
- Base Model: GPT-5
- Runtime: Codex CLI on Windows / PowerShell

### User Query

> 先提交 git 并 push，然后继续执行 `GOAL.md` 中所有能力 100% pi-mono parity 目标。

### Changes Overview

Scope: `Tau.Pods` known-model config planning, parity matrix, active goal docs, quality/next/history

Key actions:

- Continued the restored `Tau 100% pi-mono parity /goal` from the Phase 2 Candidate Queue by taking `Pods command/config/known-model compatibility`.
- Copied upstream `packages/pods/src/models.json` into `src/Tau.Pods/Models/models.json`; SHA256 matched the upstream source during review.
- Added `PodKnownModelRegistry` and DTOs for upstream known model data, covering known model list/name lookup and GPU type + GPU count config fallback.
- Updated `PodVllmCommandPlanner` so `vllm plan/deploy` automatically injects known-model args/env when no explicit `--vllm` extra args are supplied.
- Kept explicit `--vllm` as the override path, matching the upstream `startModel` custom-args behavior.
- Extended CLI JSON/text plan output and metadata to expose the selected known-model contract.
- Added targeted tests for registry lookup, known-model planner behavior, explicit override behavior and CLI JSON output.
- Synced `GOAL.md`, the parity matrix, active plan, `next.md` and `docs/QUALITY_SCORE.md` so this slice is auditable without claiming full Pods parity.

### Design Intent

The previous Tau vLLM planner could build generic serve commands, but it did not consume upstream's curated hardware/model matrix. This change ports the data-driven part first because it is locally testable, has a narrow write set, and improves future real SSH/GPU/vLLM deployment behavior without needing external credentials. The remaining upstream `commands/models.ts` behavior is deliberately left open where Tau still lacks equivalent runtime state or real infrastructure validation.

### Files Modified

- `GOAL.md`
- `src/Tau.Pods/Tau.Pods.csproj`
- `src/Tau.Pods/Models/models.json`
- `src/Tau.Pods/Models/PodKnownModels.cs`
- `src/Tau.Pods/Models/PodVllmServePlan.cs`
- `src/Tau.Pods/Serialization/PodsJsonContext.cs`
- `src/Tau.Pods/Services/PodKnownModelRegistry.cs`
- `src/Tau.Pods/Services/PodVllmCommandPlanner.cs`
- `src/Tau.Pods/Cli/PodsCli.cs`
- `tests/Tau.Pods.Tests/PodKnownModelRegistryTests.cs`
- `tests/Tau.Pods.Tests/PodVllmCommandPlannerTests.cs`
- `tests/Tau.Pods.Tests/PodsCliTests.cs`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `next.md`
- `docs/QUALITY_SCORE.md`
- `docs/histories/2026-06/20260607-2217-pods-known-model-configs.md`

### Validation

- `Get-FileHash -Algorithm SHA256 src\Tau.Pods\Models\models.json` matched `C:\Users\zhouh\Desktop\pi-mono-main\packages\pods\src\models.json`.
- `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` passed with 0 warnings and 0 errors.
- `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "PodsCli|Model|Config|PodVllmCommandPlanner" --no-restore --verbosity minimal` passed 101/101.
- `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` passed 174/174.
- `git diff --check` passed; Git only reported CRLF/LF normalization warnings.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed. Project counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 438, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 174.

### Remaining Gaps

- Upstream round-robin GPU allocation and model usage state are not ported in this slice.
- Upstream `--gpus`, `--memory` and `--context` convenience options remain open.
- Upstream direct `start/stop/list/logs` PID/log-tail flow and startup log streaming are still not command-compatible.
- Real SSH/HF/setup/GPU/vLLM smoke remains external-e2e-needed and is not closed by local planner tests.
