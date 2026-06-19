## [2026-06-19 21:55] | Task: AI provider/OAuth required e2e guard

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 继续下一步；继续优先完善 `Tau.Ai` / `Tau.Agent` 基座，保证基础能力方便其它应用引用。

### 🛠 Changes Overview

**Scope:** `scripts`, `Tau.Ai` provider/OAuth validation contract, release contract docs

**Key Actions:**

* **Added required live-e2e guard**: `scripts/verify-ai-provider-e2e-matrix.ps1` now supports `-RequireConfigured` and reports `requireConfigured`, `realE2eSatisfied`, `completionStatus`, and `gateFailure` in JSON output.
* **Prevented no-op e2e overclaim**: `-RunConfigured -RequireConfigured` now fails when no provider is configured, no provider run is attempted, or the run is not a non-isolated all-success live provider result.
* **Pinned release contract behavior**: `scripts/verify-release-contracts.ps1` now asserts the deterministic no-credential guard path by running `verify-ai-provider-e2e-matrix.ps1 -RunConfigured -RequireConfigured -Isolated -Json` and expecting a non-zero exit with `completionStatus=no-configured-providers`.
* **Synchronized docs**: `README.md`, `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md`, and active parity plans now distinguish inspect/isolated/no-op evidence from real provider/OAuth e2e evidence.

### 🧠 Design Intent (Why)

The current machine has no configured real provider credentials in the AI provider/OAuth matrix. The previous `-RunConfigured` mode could legitimately skip every provider and still complete successfully because no configured run failed. That behavior is useful for optional configured smoke runs, but it is too weak for final provider/OAuth parity evidence. The new `-RequireConfigured` switch keeps optional smoke behavior intact while adding an explicit final-evidence guard that fails closed when the environment cannot prove real provider/OAuth e2e.

### 📁 Files Modified

* `scripts/verify-ai-provider-e2e-matrix.ps1`
* `scripts/verify-release-contracts.ps1`
* `README.md`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
