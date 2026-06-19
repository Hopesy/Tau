## [2026-06-18 01:33] | Task: close AI provider/OAuth matrix harness baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### User Query

> 继续继续

### Changes Overview

**Scope:** `Tau.Ai`, release contract scripts, migration docs

**Key Actions:**

* Added `scripts/verify-ai-provider-e2e-matrix.ps1` as a repo-owned AI provider/OAuth matrix harness with a deterministic inspect mode, isolated contract mode and a separate `-RunConfigured` path for real credentials.
* Fixed the temporary harness wiring so the generated consumer project references `Tau.Ai` via an absolute repo-root path, reuses one `ModelConfigurationStore` instance, and builds before running to keep JSON output clean.
* Tightened the inspect contract to avoid false failures when local credentials are present; isolated mode now asserts zero configured/attempted/succeeded providers while still proving the open-provider set exists.
* Wired the new matrix smoke into `scripts/plan-release.ps1` and `scripts/verify-release-contracts.ps1`, and synced `README.md`, `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md`, and the active parity plans/matrix to reflect the new baseline.

### Design Intent (Why)

This slice closes the gap between “the repo has provider/OAuth coverage scripts” and “the provider/OAuth contract is part of the release gate.” The goal is not to pretend real external provider e2e is complete. The goal is to make the deterministic local contract explicit, auditable, and impossible to confuse with real credential-backed runs. That keeps the foundation gate honest while giving later work a stable harness for `-RunConfigured`.

### Validation

* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-ai-provider-e2e-matrix.ps1 -Json -Isolated` passed with `succeeded=true`, `mode=inspect`, `isolated=true`, `providerCount=11`, `configuredProviderCount=0`, `attemptedProviderCount=0`, `succeededProviderCount=0`, and `openProviderCount=5`.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1 -Json` passed with `succeeded=true` and the new `aiProviderOauthMatrix` release-contract result included in the JSON payload.
* `git diff --check` was kept clean for the current edits, aside from the pre-existing CRLF normalization warning on `docs/QUALITY_SCORE.md` from earlier work in this workspace.

### Remaining Boundaries

This only closes the local provider/OAuth matrix harness baseline and its release-contract integration. It does not close real provider/OAuth e2e with live credentials, nor does it remove the remaining `external-e2e-needed` work for provider-specific integrations.

### Files Modified

* `scripts/verify-ai-provider-e2e-matrix.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
* `README.md`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
