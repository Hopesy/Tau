## [2026-06-09 21:06] | Task: CodingAgent extension flags/shortcuts baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 按照 `GOAL.md` 继续迁移，继续推进 Tau 对 pi-mono 的 100% parity。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **Extension flags**: JS/TS limited extension runtime now supports `pi.registerFlag(name, { description, type, default })`, stores boolean/string defaults, exposes flag metadata through `CodingAgentExtensionStatus`, and lets `pi.getFlag(name)` read defaults registered by the same extension.
* **Extension shortcuts**: JS/TS limited extension runtime now supports `pi.registerShortcut(key, { description, handler })` metadata discovery, exposes shortcuts through status, and reports non-zero flag/shortcut counts in module status.
* **User-visible status**: `/extensions` lists extension flags and shortcuts; `/reload` summary keeps the previous compact shape unless flags/shortcuts are present.
* **Tests**: Added focused JavaScript and TypeScript coverage for flag defaults, `getFlag`, shortcut metadata, and `/extensions` output.
* **Validation**: Passed CodingAgent build, focused flags/shortcuts tests 4/4, focused `ExtensionCommandStore|ExtensionsCommand` 30/30, full `Tau.CodingAgent.Tests` 521/521, targeted WebUi bridge retest 1/1 after a first full-gate browser timeout, `git diff --check`, and project-level `verify-dotnet.ps1 -SkipRestore`.
* **Docs**: Updated `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md`, the active 100% parity plan, and the parity matrix to split the newly closed metadata/default/status baseline from remaining CLI flag injection, interactive shortcut dispatch, shortcut conflict diagnostics, and broader lifecycle gaps.

### 🧠 Design Intent (Why)

This closes the smallest reviewable upstream extension contract still open after command/tool/hook work. Upstream separates registration metadata/default values from interactive shortcut dispatch and CLI flag value injection, so Tau now ports the local metadata/default/getFlag/status baseline without pretending to have completed keybinding dispatch, CLI parser integration, or full extension lifecycle parity.

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentJavaScriptExtensionRuntime.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionCommandStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentExtensionCommandStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
