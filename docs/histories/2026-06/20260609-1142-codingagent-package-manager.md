## [2026-06-09 11:42] | Task: CodingAgent package manager baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows / PowerShell`

### User Query

> 继续继续。按当前 `GOAL.md` 100% pi-mono parity 主线继续推进一个可审计移植切片。

### Changes Overview

**Scope:** `Tau.CodingAgent`, `tests/Tau.CodingAgent.Tests`, parity docs.

**Key Actions:**

* Added `CodingAgentPackageManager` / `CodingAgentPackageCli` for top-level `install`, `remove`, `uninstall`, `update`, `list` and `config` package commands before runner startup.
* Added upstream-shaped `packages` settings persistence for string and object package source forms, preserving package sources across later settings saves.
* Wired local package resources into extension, prompt, skill and theme discovery through package `pi` manifest entries or convention directories.
* Updated `/reload` to refresh package resources before extension resources, so package-contributed paths are visible without restarting.
* Added focused package/settings tests and synchronized `GOAL.md`, active parity plan/matrix, `next.md` and `docs/QUALITY_SCORE.md`.

### Design Intent

Upstream `package-manager-cli.ts` exposes a top-level package command surface, while `core/package-manager.ts` is a large npm/git/local installer and resource resolver. This slice deliberately closes the low-risk local contract first: source persistence, user/project scope, list/config output and local package resource discovery. It does not claim npm/git install or TypeScript extension runtime parity.

The local package resource path is wired into the existing stores instead of creating a parallel resource loader. That keeps the behavior simple and reuses current prompt, skill, theme and extension validation paths.

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "PackageManager|SettingsStore" --no-restore --verbosity minimal` passed 12/12.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` passed 463/463.

### Remaining Boundaries

* npm/git install, pull and update execution remain open.
* Glob/negation package resource filters remain open.
* Interactive config selector remains open.
* Package-loaded TypeScript extension runtime remains open.
* Startup changelog version state and install telemetry runtime remain open.
* Final `pi` package/bin identity and real package consumer smoke remain open.

### Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentPackageManager.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSettingsStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionCommandStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentPackageManagerTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSettingsStoreTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
