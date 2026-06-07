## [2026-06-08 02:04] | Task: Pods top-level command shim

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### 📥 User Query

> 按照 `GOAL.md` 继续 Tau 100% pi-mono parity，并提交所有 git 变更后 push。

### 🛠 Changes Overview

**Scope:** `Tau.Pods` CLI compatibility, Pods tests, parity planning docs.

**Key Actions:**

* **[CLI shim]**: Added upstream-style `pods` group mapping for configured pod list, setup, active, and remove operations.
* **[Model operation shim]**: Added top-level `start <model> --name <name>`, `list`, and `stop <name>` compatibility through existing vLLM deploy/deployments/stop services.
* **[Regression coverage]**: Added Pods CLI contract tests for upstream-style `pods`, `start`, `list`, and `stop` behavior; updated existing config-list tests to use `pods`.
* **[Docs sync]**: Updated `GOAL.md`, `next.md`, quality score, and active parity plans to record the closed local command-shim contract and the still-open remote/e2e gaps.

### 🧠 Design Intent (Why)

This keeps the slice small by reusing existing Tau.Pods configuration and vLLM orchestration services instead of duplicating remote lifecycle logic. The change closes the local top-level command contract needed by upstream `packages/pods/src/cli.ts`, while explicitly leaving `shell`, `agent` / prompt mapping, no-arg stop-all, exact PID/log-tail/startup streaming, usage-aware GPU allocation, and real SSH/HF/GPU/vLLM smoke open for later parity slices.

### 📁 Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
