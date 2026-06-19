## [2026-06-18 02:05] | Task: surface explicit AI configuration seam in local consumer smoke

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### User Query

> 继续继续

### Changes Overview

**Scope:** `Tau.Ai`, package consumer smoke, release contract scripts, migration docs

**Key Actions:**

* Extended `StreamFunctions` so external consumers can pass an explicit `ModelConfigurationStore` and `ProviderAuthResolver` instead of relying on process-global defaults.
* Fixed `OpenAiCompatibleProvider` header application so explicit request headers remove any existing same-name header before re-adding it, allowing request-time overrides of provider/model-configured headers.
* Extended `scripts/verify-agent-package-consumer.ps1` so the `Tau.Ai` consumer now exercises a real `models.json` dynamic provider, explicit auth resolver, auth status check, Bearer auth injection, provider/model/header precedence and request-path behavior in addition to the existing Faux provider path.
* Updated `scripts/verify-release-contracts.ps1`, `scripts/plan-release.ps1`, `README.md`, `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md`, and the active parity plan/matrix to record the new explicit consumer seam and header precedence contract.

### Design Intent (Why)

The previous package-consumer gate proved that external .NET projects can consume `Tau.Ai` and `Tau.Agent` through a local package source. This slice closes a narrower but important contract: external consumers should also be able to inject their own config/auth stores and rely on predictable header precedence when models.json and request-time headers overlap. That makes the local consumer smoke closer to a real host application that owns its own auth/config state.

### Validation

* The existing consumer smoke path was extended to print and assert `configuredStatus=models.json:True`, `configuredApi=consumer-config-api`, `configuredHost=consumer.example.test`, `configuredAuth=Bearer consumer-dynamic-key`, `configuredProviderHeader=explicit-provider-header`, `configuredModelHeader=model-header-value`, `configuredExplicitHeader=explicit-header`, and `configuredPath=/v1/chat/completions`.
* `scripts/verify-release-contracts.ps1` was updated to assert the new `Tau.Ai` consumer outputs in the release contract JSON path.
* Documentation updates were synchronized across the release plan, goal file, next-step tracker, quality score, README, and parity matrix.

### Remaining Boundaries

This only closes the explicit configuration seam and header-precedence behavior in local package-consumer smoke. It does not close real provider/OAuth e2e, real NuGet registry promotion, signing/provenance, or any future broader runtime config UX.

### Files Modified

* `scripts/verify-release-contracts.ps1`
* `scripts/plan-release.ps1`
* `README.md`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/histories/2026-06/20260618-0205-ai-config-seam-consumer-smoke.md`
