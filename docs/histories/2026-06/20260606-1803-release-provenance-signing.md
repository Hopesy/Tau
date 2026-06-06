## [2026-06-06 18:03] | Task: release provenance signing baseline

### Execution Context

* Agent ID: `Codex`
* Base Model: `GPT-5`
* Runtime: `Windows PowerShell`

### User Query

> 按照 `GOAL.md` 继续 Tau 100% pi-mono parity，不要停；本轮从 Phase 5 package signing/provenance 缺口继续推进。

### Changes Overview

**Scope:** Release scripts, CI, README, quality/plan/next docs

**Key Actions:**

* Added `scripts/generate-release-provenance.ps1` as a dry-run-first release provenance manifest generator for release archives and NuGet packages. The manifest records git identity, worktree state, version source, relative paths, file size and SHA256.
* Added `scripts/sign-release-packages.ps1` as a guarded `dotnet nuget sign` wrapper. It supports certificate path or fingerprint, timestamp URL, hash options, output directory and password env redaction; `-Apply` is required for mutation.
* Added `scripts/verify-release-provenance.ps1` with a temp Git fixture and fake dotnet command to validate provenance JSON, dirty apply blocking, signing command shape, certificate path/fingerprint entry points and certificate password redaction.
* Wired the new smoke into `scripts/plan-release.ps1`, `scripts/verify-release-contracts.ps1` and `.github/workflows/tau-ci.yml`.
* Synced README, `docs/QUALITY_SCORE.md`, the active parity plan and `next.md` so the new local baseline is visible while keeping real signing/provenance rehearsal and external release e2e open.

### Design Intent (Why)

Tau already had guarded release finalization and package publish previews, but Phase 5 still had no local evidence trail for package hashes or package signing command contracts. The new slice keeps the same release safety model: read-only by default, explicit `-Apply` for writes or signing execution, clean-worktree checks for mutable flows, and no secret values in JSON or command output.

This intentionally does not claim full supply-chain or registry parity. It closes only the local manifest/signing smoke baseline. Real code-signing certificates, real package registry publish, release archive signing, external attestation and end-to-end release rehearsal remain separate gates.

### Files Modified

* `scripts/generate-release-provenance.ps1`
* `scripts/sign-release-packages.ps1`
* `scripts/verify-release-provenance.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
* `.github/workflows/tau-ci.yml`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
