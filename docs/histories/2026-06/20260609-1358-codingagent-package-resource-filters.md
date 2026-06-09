# [2026-06-09 13:58] | Task: CodingAgent package resource filters

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### User Query

> 继续当前 `GOAL.md` 100% pi-mono parity 迁移主线。

### Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **Resource filters**: Implemented package resource include/exclude filtering for manifest entries and package object filters.
* **Glob support**: Added a small local glob matcher for package resource paths, covering `*`, `?` and `**` over normalized POSIX-style paths.
* **Override support**: Added upstream-style `!` exclude, `+` exact force-include and `-` exact force-exclude handling; empty arrays still disable a resource type.
* **Docs**: Updated GOAL, matrix, next, quality and the active parity plan to remove the stale glob/negation and npm/git execution gaps from the local baseline while keeping runtime/e2e gaps open.

### Design Intent (Why)

The previous package manager work closed package source persistence and npm/git command execution, but package object filters still ignored globs and override patterns. This change closes the deterministic local filter contract without claiming the interactive config selector, TypeScript extension runtime, telemetry/changelog runtime, or real npm/git network/package consumer smoke.

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentPackageManager.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentPackageManagerTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`

### Verification

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "PackageManager|SettingsStore" --no-restore --verbosity minimal`：21/21 passed
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "Cli|Session|Settings|PackageManager" --no-restore --verbosity minimal`：128/128 passed
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：472/472 passed
* `git diff --check`：passed，only CRLF normalization warnings for existing docs
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed（Ai 280、Agent 119、Tui 251、CodingAgent 472、WebUi 61、Pods 216）
