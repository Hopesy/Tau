## [2026-06-06 19:23] | Task: AI test image generator parity

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 按照 `GOAL.md` 继续推进 Tau 100% pi-mono parity。

### Changes Overview

**Scope:** Root scripts, CI, parity docs

**Key Actions:**

* Added `scripts/generate-ai-test-image.ps1`, a Tau-native equivalent for upstream `packages/ai/scripts/generate-test-image.ts`.
* Added `scripts/verify-ai-test-image.ps1`, a local fixture smoke that validates PNG structure, zlib/adler32, scanline filters, key pixels and SHA256 output.
* Wired `ai-test-image-smoke` into `scripts/plan-release.ps1`, `scripts/verify-release-contracts.ps1` and `.github/workflows/tau-ci.yml`.
* Updated `README.md`, `next.md`, `docs/QUALITY_SCORE.md`, the 100% parity plan and the parity matrix to move this row from `missing` to local smoke-backed `ported`.

### Design Intent

The upstream script exists only to generate a deterministic AI vision fixture: a 200x200 white PNG with a red circle. Tau should not depend on node-canvas or platform graphics APIs for that utility, so the PowerShell version writes PNG chunks and zlib stored blocks directly. The smoke decodes the generated image rather than only checking that a file exists, which keeps the script useful in CI.

### Validation

* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-ai-test-image.ps1`
  * Passed, 30 assertions.
  * Generated fixture SHA256: `94a1639c1a1996b93ea2b3c636d2f168ff1f0fd080e050093e66845940fa0a3a`.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1`
  * Passed, 55 assertions.
* `git diff --check`
  * Passed; only existing CRLF-to-LF warnings were reported for touched markdown files.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  * Passed.
  * Test counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 115, `Tau.Tui.Tests` 190, `Tau.CodingAgent.Tests` 435, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 166.

### Files Modified

* `scripts/generate-ai-test-image.ps1`
* `scripts/verify-ai-test-image.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
* `.github/workflows/tau-ci.yml`
* `README.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/histories/2026-06/20260606-1923-ai-test-image-generator.md`
