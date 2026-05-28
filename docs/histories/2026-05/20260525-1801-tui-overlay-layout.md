# [2026-05-25 18:01] | Task: TUI transcript overlay layout baseline

## Execution Context

* **Agent ID**: `Codex`
* **Runtime**: `Windows PowerShell, .NET 10`

## User Query

> 继续下一轮 Tau <- pi-mono-main 快速移植，多 Agent 并行，少做低收益单元测试和大段文档同步，尽快推进核心 baseline。

## Changes Overview

**Scope:** `Tau.Tui`

**Key Actions:**

* `TuiTranscriptOverlayOptions` now supports overlay `Anchor`, `Margin`, `OffsetRow`, `OffsetColumn`, `MinWidth`, and `MaxHeight`.
* `TuiTranscriptViewportHost` now resolves overlay sizing and position against viewport bounds and margins, then clamps the composed overlay into the visible screen.
* Existing absolute `Row` / `Column` / `Width` behavior remains compatible when `Anchor` is not specified.

## Design Intent

Upstream TUI overlays support anchor-based positioning, margins, minimum width, and maximum height. Tau already had transcript overlay composition but only absolute row/column placement. This slice adds the layout semantics that can be verified inside the existing in-memory viewport host without pulling in full terminal lifecycle, hardware cursor, non-capturing focus, or CodingAgent main-screen wiring.

## Files Modified

* `src/Tau.Tui/Runtime/TuiTranscriptViewportHost.cs`
* `tests/Tau.Tui.Tests/TuiTranscriptViewportHostTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`

## Validation

* `dotnet build src\Tau.Tui\Tau.Tui.csproj --no-restore --verbosity minimal` passed with 0 warnings / 0 errors.
* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~TuiTranscriptViewportHostTests"` passed: 19 / 19.
