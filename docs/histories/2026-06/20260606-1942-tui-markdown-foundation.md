## [2026-06-06 19:42] | Task: Tui Markdown foundation parity

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 按照 `GOAL.md` 继续推进 Tau 100% pi-mono parity。

### Changes Overview

**Scope:** `Tau.Tui`

**Key Actions:**

* Added `TuiMarkdown`, a .NET-native library foundation for upstream `packages/tui/src/components/markdown.ts`.
* Added `TuiMarkdownTheme` and `TuiDefaultTextStyle` to model upstream markdown element styling and default text styling hooks.
* Added targeted tests for headings, paragraphs, inline code/bold/italic/strikethrough/link handling, OSC 8 hyperlink output, fenced code blocks, lists, tables, blockquotes, horizontal rules, padding/background/cache behavior and terminal image escape line preservation.
* Corrected the parity matrix Markdown source path from root `markdown.ts` to upstream `components/markdown.ts`.
* Updated `docs/ARCHITECTURE.md`, `next.md`, `docs/QUALITY_SCORE.md`, the parity matrix and the 100% parity active plan.

### Design Intent

Upstream `Markdown` is a TUI component, not just a helper for exported HTML. Tau previously had message/status plain-text wrapping and HTML exporter Markdown logic, but no reusable `Tau.Tui` Markdown component. This slice adds a small, dependency-free Markdown component that covers the user-visible block and inline structures needed by the TUI foundation while leaving terminal image rendering, exact Marked tokenizer behavior and full syntax-highlight/theme integration for later slices.

### Validation

* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal`
  * Passed: 206/206.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  * Passed: src/test build 0 warnings / 0 errors.
  * Test counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 115, `Tau.Tui.Tests` 206, `Tau.CodingAgent.Tests` 435, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 166.

### Files Modified

* `src/Tau.Tui/Components/TuiMarkdown.cs`
* `tests/Tau.Tui.Tests/TuiMarkdownTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/histories/2026-06/20260606-1942-tui-markdown-foundation.md`
