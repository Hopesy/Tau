## [2026-06-08 02:17] | Task: Pods no-arg stop-all command

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### 📥 User Query

> 按照 `GOAL.md` 继续 Tau 100% pi-mono parity。

### 🛠 Changes Overview

**Scope:** `Tau.Pods` top-level command compatibility and parity docs.

**Key Actions:**

* **[Command compatibility]**: Added upstream-style no-arg `stop` behavior. `stop [--json] [--config path] [--pod id]` now lists deployments for the active or specified pod and stops each one through the existing vLLM stop service.
* **[Contract preservation]**: Kept `stop <name>`, `stop --pod <pod> <name>`, and legacy Tau positional stop behavior compatible with the previous command surface.
* **[Output contract]**: Added aggregated `operation=stop-all` text/JSON output while reusing the existing vLLM operation object shape for each stopped deployment.
* **[Regression coverage]**: Added a PodsCli test that fixes list-then-stop-all ordering and JSON shape for two remote deployments.
* **[Docs sync]**: Updated `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md`, and active parity plans to move no-arg stop-all out of the current local command-contract gap while keeping real remote e2e and startup/log parity open.

### 🧠 Design Intent (Why)

Upstream `pi stop` without a model name stops every running model on the selected pod. Tau already had remote deployment listing and single-deployment vLLM stop services, so the smallest correct parity slice was a command-level aggregation shim instead of duplicating SSH lifecycle logic. This advances top-level command compatibility while keeping the larger upstream local PID/GPU state, log-tail startup UX, and real SSH/GPU/vLLM evidence as separate open work.

### 📁 Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
