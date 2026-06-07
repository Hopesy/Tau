## [2026-06-08 00:10] | Task: Pods PI_CONFIG_DIR config path parity

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Windows PowerShell

### User Query

> 目标是 100% 移植；全面审视还没完善的能力，把计划更新到 GOAL.md，并继续按 goal 执行、提交和推送。

### Changes Overview

**Scope:** `Tau.Pods` config path/env contract and parity planning docs.

**Key Actions:**

* Added `PI_CONFIG_DIR` support to `PodsCli`: when no explicit config path is provided, Tau now uses `<PI_CONFIG_DIR>/pods.json`; explicit `--config path` and positional config paths still take precedence.
* Added CLI regression tests for `list`, `init --json`, and `vllm plan --config` override behavior under `PI_CONFIG_DIR`.
* Updated `GOAL.md`, the active 100% parity plan, the parity matrix, `next.md`, `docs/QUALITY_SCORE.md`, `docs/ARCHITECTURE.md`, and `README.md` to record the current 100% acceptance boundary and this slice's remaining gaps.

### Design Intent

This slice closes the upstream env override sub-contract from `packages/pods/src/config.ts` without changing Tau's no-env default away from `tau.pods.json`. That keeps the commit reviewable and avoids mixing config path compatibility with missing-config semantics, upstream record-shaped config schema migration, and real pod e2e validation.

### Remaining Gap

* Tau still defaults to `tau.pods.json` when `PI_CONFIG_DIR` is not set; upstream defaults to `~/.pi/pods.json`.
* Tau still reports missing config as a CLI error in existing command paths; upstream `loadConfig()` returns `{ pods: {} }`.
* Upstream `pods: Record<string, Pod>` / `models: Record<string, Model>` schema compatibility, state migration, and real SSH/HF/GPU/vLLM smoke remain open.

### Validation

* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` passed with 0 warnings and 0 errors.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "PodsCli|Config" --no-restore --verbosity minimal` passed 87/87.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` passed 184/184.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed with counts: Ai 280, Agent 119, Tui 251, CodingAgent 438, WebUi 44, Pods 184.

### Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `GOAL.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/ARCHITECTURE.md`
* `README.md`
