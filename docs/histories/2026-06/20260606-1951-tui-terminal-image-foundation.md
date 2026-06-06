## [2026-06-06 19:51] | Task: Tui terminal image foundation parity

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 按照 `GOAL.md` 继续推进 Tau 100% pi-mono parity。

### Changes Overview

**Scope:** `Tau.Tui`

**Key Actions:**

* Added `TuiTerminalImage`, a .NET-native foundation for upstream `packages/tui/src/terminal-image.ts`.
* Added `TuiImage`, a component foundation for upstream `packages/tui/src/components/image.ts`.
* Added terminal image types for capabilities, cell dimensions, image dimensions, render options and render results.
* Added targeted tests for capability detection, kitty/iTerm2 escape encoding, Kitty delete escape, PNG/JPEG/GIF/WebP dimension sniffing, row calculation, renderImage selection, OSC 8 hyperlink, image fallback text and `TuiImage` protocol/fallback cache behavior.
* Updated `TuiMarkdown` to share `TuiTerminalImage.IsImageLine(...)` and `TuiTerminalImage.Hyperlink(...)`.
* Updated `docs/ARCHITECTURE.md`, `next.md`, `docs/QUALITY_SCORE.md`, the parity matrix and the 100% parity active plan.

### Design Intent

Upstream splits terminal image responsibilities between low-level helpers (`terminal-image.ts`) and the `Image` component (`components/image.ts`). Tau now mirrors that boundary: `TuiTerminalImage` owns capability detection, escape sequence generation, dimensions and fallback formatting, while `TuiImage` owns component cache and render/fallback behavior. This gives later CodingAgent tool rendering and real terminal smoke a shared library target instead of embedding protocol strings in the app host.

### Validation

* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal`
  * Passed: 214/214.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  * Passed: src/test build 0 warnings / 0 errors.
  * Test counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 115, `Tau.Tui.Tests` 214, `Tau.CodingAgent.Tests` 435, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 166.

### Files Modified

* `src/Tau.Tui/Rendering/TuiTerminalImage.cs`
* `src/Tau.Tui/Components/TuiImage.cs`
* `src/Tau.Tui/Components/TuiMarkdown.cs`
* `tests/Tau.Tui.Tests/TuiTerminalImageTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/histories/2026-06/20260606-1951-tui-terminal-image-foundation.md`
