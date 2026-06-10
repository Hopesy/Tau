## [2026-06-10] | Task: CodingAgent extension CLI flag value injection baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex / Windows PowerShell`

### 📥 User Query

> 按照 `GOAL.md` 继续迁移，继续推进 Tau 对 pi-mono 的 100% parity。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **CLI flag capture**: `CodingAgentCliArguments` now collects unknown `--flag` / `--flag=value` / `--flag value` tokens into an `ExtensionFlags` dictionary (null value means the flag was supplied without a value), mirroring upstream `cli/args.ts` `unknownFlags` parsing instead of silently discarding them.
* **Runtime injection**: `CodingAgentJavaScriptExtensionRuntime.SetFlagValues(...)` seeds resolved CLI flag values into every JS/TS payload (`flagValues`); the limited Node runtime now seeds its `flagValues` map from the payload before the extension factory runs, so CLI-supplied values win over `registerFlag` defaults (whose default-set is `!flagValues.has`-guarded), matching upstream `applyExtensionFlagValues`.
* **Flag validation**: `CodingAgentExtensionCommandStore.ApplyExtensionFlagValues(...)` validates captured CLI flags against registered extension flags — boolean flags resolve to `true`, string flags require a value, value-less string flags and unknown flags produce `error` diagnostics with upstream-shaped messages (`Extension flag "--name" requires a value`, `Unknown option(s): --name`).
* **Program wiring**: `Program.cs` applies captured CLI flags before runner construction, prints flag diagnostics to stderr, and returns exit code 1 when extension flag validation reports an error.
* **Tests**: Added focused store coverage for CLI flag injection through `pi.getFlag(...)` and for unknown/value-less string flag diagnostics; extended the CLI args parser test to assert captured boolean and string extension flags.
* **Validation**: Passed CodingAgent build 0 warning / 0 error, focused `ExtensionCommandStore|ExtensionsCommand|CodingAgentCliArguments` 32/32, a CLI smoke for value-less string flag diagnostics (`Extension flag "--mode-name" requires a value`, `exit=1`), full `Tau.CodingAgent.Tests` 524/524, and project-level `verify-dotnet.ps1 -SkipRestore` (Ai 280, Agent 123, Tui 251, CodingAgent 524, WebUi 61, Pods 216).
* **Docs**: Updated `GOAL.md`, `next.md`, the active 100% parity plan, and the parity matrix to move CLI flag value injection from open to closed local baseline, leaving interactive shortcut dispatch, shortcut conflict diagnostics and broader lifecycle events open.

### 🧠 Design Intent (Why)

The previous slice closed `registerFlag`/`registerShortcut` metadata and defaults; the `GOAL.md` gap map names CLI flag value injection as the next open extension-runtime contract. Upstream resolves CLI `--flag` tokens against registered extension flags and writes `runtime.flagValues` so `pi.getFlag()` returns CLI-supplied values. Tau now ports that capture/validate/inject path as a reviewable local baseline. The runtime stays stateless per-invoke: resolved values live on the runtime instance and are re-injected into each payload, so the seed-then-factory ordering reproduces upstream's "CLI value overrides default" semantics without a persistent JS process. Interactive shortcut dispatch, shortcut conflict diagnostics, full jiti import parity and broader lifecycle events remain open.

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentInitialMessageBuilder.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentJavaScriptExtensionRuntime.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionCommandStore.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentExtensionCommandStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentInitialMessageBuilderTests.cs`
* `GOAL.md`
* `next.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
