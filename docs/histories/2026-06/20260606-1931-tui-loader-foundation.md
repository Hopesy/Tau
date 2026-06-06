## [2026-06-06 19:31] | Task: Tui loader foundation parity

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 按照 `GOAL.md` 继续推进 Tau 100% pi-mono parity。

### Changes Overview

**Scope:** `Tau.Tui`

**Key Actions:**

* Added `TuiLoader`, a .NET-native foundation for upstream `packages/tui/src/components/loader.ts`.
* Added `TuiCancellableLoader`, a cancellable input component equivalent for upstream `components/cancellable-loader.ts`.
* Added targeted tests for spinner rendering, manual frame advancement, message updates, formatter hooks, timer lifecycle, Escape/Ctrl+C cancellation and render delegation.
* Updated `docs/ARCHITECTURE.md`, `next.md`, `docs/QUALITY_SCORE.md`, the parity matrix and the 100% parity active plan.

### Design Intent

Upstream loader behavior is small but user-visible: a 10-frame spinner, a message, render requests on updates, and Escape cancellation for async operations. Tau now keeps that behavior as a reusable library component, but exposes deterministic manual frame advancement for tests and leaves real terminal host integration to a later Tui runtime slice.

### Validation

* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal`
  * Passed: 198/198.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  * Passed: src/test build 0 warnings / 0 errors.
  * Test counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 115, `Tau.Tui.Tests` 198, `Tau.CodingAgent.Tests` 435, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 166.

### Files Modified

* `src/Tau.Tui/Components/TuiLoader.cs`
* `src/Tau.Tui/Components/TuiCancellableLoader.cs`
* `tests/Tau.Tui.Tests/TuiLoaderTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/histories/2026-06/20260606-1931-tui-loader-foundation.md`
