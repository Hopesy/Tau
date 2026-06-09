# [2026-06-09 13:43] | Task: CodingAgent package manager execution baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### User Query

> 继续当前 `GOAL.md` 100% pi-mono parity 迁移主线。

### Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **Package command runner**: Added `ICodingAgentPackageCommandRunner`, process-backed default runner and fake-runner-friendly command/result records for npm/git package operations.
* **npm execution**: `install/remove/update` now execute npm commands before settings persistence, support global `npm install -g`, project `.tau/npm --prefix`, configured `npmCommand`, `@latest` update and non-persistence on command failure.
* **git execution**: Git package sources now parse common `git:` / HTTPS / SSH forms, clone into user/project `.tau/git`, checkout pinned refs, pull unpinned packages on update and remove only computed install-root children.
* **Offline/error boundary**: `PI_OFFLINE` skips package update execution; command stderr/stdout included in CLI errors is passed through existing CodingAgent secret redaction.
* **Tests and docs**: Added fake-runner package-manager tests for npm/global/project/git/offline/failure behavior and updated GOAL, active plans, matrix, next and quality notes.

### Design Intent (Why)

The previous package manager slice intentionally stopped at deterministic source persistence and local resource discovery. This follow-up closes the next upstream contract layer without touching the network in tests: Tau now has the same top-level command execution seam as upstream, while real registry/git service smoke, TypeScript extension runtime, package resource selector and telemetry/changelog behavior remain explicit open parity gaps.

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentPackageManager.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentPackageManagerTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`

### Verification

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "PackageManager|SettingsStore" --no-restore --verbosity minimal`：17/17 passed
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "Cli|Session|Settings|PackageManager" --no-restore --verbosity minimal`：124/124 passed
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：468/468 passed
* `git diff --check`：passed，only CRLF normalization warnings for existing docs
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed（Ai 280、Agent 119、Tui 251、CodingAgent 468、WebUi 61、Pods 216）
