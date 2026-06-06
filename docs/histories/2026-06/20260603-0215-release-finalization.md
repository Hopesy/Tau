## [2026-06-03 02:15] | Task: release finalization parity

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, Tau harness-init workspace`

### User Query

> 按照 `GOAL.md` 继续推进 Tau 到 `pi-mono-main` 的 100% parity。

### Changes Overview

**Scope:** Phase 5 release / CI / install delivery parity.

**Key Actions:**

* **Release finalization script**: Added `scripts/finalize-release.ps1`, a guarded dry-run-first final release stage for pushing the release branch/tag and optionally creating a GitHub Release with verified archives.
* **Release finalization smoke**: Added `scripts/verify-release-finalize.ps1`, using a temporary Git repository, temporary bare remote, fake release archive and fake `gh.cmd` to validate dry-run JSON, branch/tag push, GitHub Release arguments, draft/prerelease flags and dirty-worktree blocking without touching real remotes.
* **Release contract integration**: Updated `scripts/plan-release.ps1`, `scripts/verify-release-contracts.ps1` and `.github/workflows/tau-ci.yml` so release finalization is part of the planned release chain and CI smoke coverage.
* **Documentation sync**: Updated `README.md`, `docs/QUALITY_SCORE.md`, `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md` and `next.md` to record that guarded branch/tag push and GitHub Release archive upload now have a baseline, while package registry publish synchronization and real external release e2e remain open.

### Design Intent

Upstream `scripts/release.mjs` does local version/changelog commit/tag, publishes packages, adds a fresh `[Unreleased]` changelog section, commits again, then pushes the branch and tag. Tau already had guarded local release preparation/execution through commit/tag, but the branch/tag push and release archive upload stage still needed an explicit, auditable equivalent.

The Tau implementation keeps local release mutation and remote finalization split on purpose. `execute-release.ps1` remains responsible for version/notes/commit/tag. `finalize-release.ps1` is the only script that can push or create a GitHub Release, and only after preflight checks prove the local tag points at the branch tip and release archives exist. This avoids hiding remote mutation behind a version bump command and keeps package registry publishing clearly separate from GitHub Release archive upload.

### Validation

* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-finalize.ps1` passed with 22 assertions.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1` passed with 41 assertions.
* `git diff --check` passed.

### Files Modified

* `.github/workflows/tau-ci.yml`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/histories/2026-06/20260603-0215-release-finalization.md`
* `next.md`
* `scripts/finalize-release.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
* `scripts/verify-release-finalize.ps1`

### Continuation: package publish synchronization

**Scope:** Phase 5 release / CI / install delivery parity.

**Key Actions:**

* **Package publish script**: Added `scripts/publish-release-packages.ps1`, a guarded dry-run-first package registry publish stage for `dotnet pack` and `dotnet nuget push`.
* **Default package boundary**: Kept the default publish scope to library packages `Tau.Ai`, `Tau.Agent` and `Tau.Tui`; application projects remain archive-delivered by default and produce an explicit warning when included.
* **Credential boundary**: API keys are read only from the configured environment variable name and are redacted from JSON output, planned command previews, command results and subprocess output previews.
* **Package publish smoke**: Added `scripts/verify-release-package-publish.ps1`, using a temporary Git fixture, fake library/app projects and fake `dotnet.ps1` to validate dry-run JSON, pack/push command shape, default library package count, API key redaction, subprocess output preview redaction, application warning and dirty-worktree blocking without touching real package registries.
* **Release contract integration**: Updated `scripts/plan-release.ps1`, `scripts/verify-release-contracts.ps1` and `.github/workflows/tau-ci.yml` so package publish synchronization is part of the planned release chain and CI smoke coverage.
* **Documentation sync**: Updated `README.md`, `docs/QUALITY_SCORE.md`, the active 100% parity plan and `next.md` to record that package publish now has a guarded local baseline while real NuGet/package registry rehearsal, package signing/provenance and application package policy remain open.

### Design Intent

Upstream `npm run publish` publishes the JavaScript workspaces. Tau does not have an npm workspace equivalent, and the executable applications are already delivered through release archives. The package publish baseline therefore defaults to the library projects that map to reusable packages, while keeping app packaging an explicit operator choice.

The script intentionally separates preview, pack and push. Dry-run remains read-only, real mutation requires `-Apply`, and real registry publishing still requires the intended source, credentials and package boundary to be confirmed by the release operator. This keeps package registry mutation out of version/changelog/tag/finalization steps and prevents API key values from becoming part of script JSON, command previews, command result previews or history.

### Validation

* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-package-publish.ps1` passed with 21 assertions.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-finalize.ps1` passed with 22 assertions.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1` passed with 46 assertions.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-release-packages.ps1 -AllowNoApiKey -Json` passed as dry-run, reporting version `0.1.0`, three default library projects and redacted API key metadata.
* `git diff --check` passed; Git reported only CRLF-to-LF normalization warnings for already modified Markdown files.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed with `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 115, `Tau.Tui.Tests` 190, `Tau.CodingAgent.Tests` 435, `Tau.WebUi.Tests` 44 and `Tau.Pods.Tests` 166.

### Files Modified

* `.github/workflows/tau-ci.yml`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/histories/2026-06/20260603-0215-release-finalization.md`
* `next.md`
* `scripts/plan-release.ps1`
* `scripts/publish-release-packages.ps1`
* `scripts/verify-release-contracts.ps1`
* `scripts/verify-release-package-publish.ps1`
