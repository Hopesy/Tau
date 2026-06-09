# [2026-06-09 14:41] | Task: CodingAgent startup changelog telemetry

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### User Query

> 继续当前 `GOAL.md` 100% pi-mono parity 迁移主线。

### Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **Startup notice runtime**: Added `CodingAgentStartupNoticeService` and wired it into interactive `CodingAgentHost` startup after the welcome banner and before initial inputs.
* **Version state**: Added `lastChangelogVersion` persistence to `CodingAgentSettingsStore` and exposed the field through RPC `get_settings` / `update_settings`.
* **Install telemetry**: Added a best-effort install/update telemetry reporter using the upstream-style `https://pi.dev/install?version=...` ping, with startup never blocked or failed by telemetry errors.
* **Environment behavior**: `PI_OFFLINE` disables telemetry, while `PI_TELEMETRY` overrides the persisted setting.
* **Docs**: Updated GOAL, matrix, next, quality and the active parity plan so startup changelog/version state and install telemetry runtime are no longer listed as local CodingAgent package/changelog gaps.

### Design Intent (Why)

The previous CodingAgent package slices closed package source persistence, resource discovery/filtering and npm/git command execution, but startup changelog/version state and install telemetry still existed only as settings-list fields or `/changelog` command output. This change closes the deterministic local startup contract without claiming full upstream package identity, TypeScript extension runtime, real npm/git network smoke, real telemetry backend e2e or exact upstream TUI rendering parity.

Tau intentionally reuses the existing release-notes table parser instead of adding the upstream `CHANGELOG.md` version-section parser in this slice. Fresh installs record the current version and report telemetry without showing a changelog; updated installs record the new version and render either a collapsed notice or the first release-note entries; resumed sessions skip both version updates and telemetry.

### Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSettingsStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentStartupNoticeService.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSettingsStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentStartupNoticeServiceTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`

### Verification

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "StartupNotice|SettingsStore|Changelog|GetSettings|UpdateSettings" --no-restore --verbosity minimal`：20/20 passed
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "Cli|Session|Settings|PackageManager|Changelog|StartupNotice" --no-restore --verbosity minimal`：138/138 passed
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：478/478 passed
* First project gate attempt found an unrelated existing WebUi browser-flow timeout in `JavaScriptReplBridge_PollsPendingToolRequestAndPostsBrowserResult`; current WIP has no WebUi diff, and the failed test passed on targeted rerun 1/1.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：second full run passed（Ai 280、Agent 119、Tui 251、CodingAgent 478、WebUi 61、Pods 216）
* `git diff --check`：passed，only CRLF normalization warnings for existing files
