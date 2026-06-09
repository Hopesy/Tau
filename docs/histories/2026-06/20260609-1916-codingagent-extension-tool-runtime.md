## [2026-06-09 19:16] | Task: CodingAgent extension tool runtime

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 继续 `GOAL.md` 100% pi-mono parity 主线，关闭下一段可验证 CodingAgent parity 缺口。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` extension runtime / runner tool registry / parity docs

**Key Actions:**

* **Implemented JS/TS extension tools**: `CodingAgentJavaScriptExtensionRuntime` now discovers `pi.registerTool(...)` metadata and can execute tool handlers through the limited Node subprocess runtime.
* **Added Agent tool adapter**: `CodingAgentExtensionToolAdapter` maps extension tool schema, label, execution mode, text content, `isError`, and JSON `details` into Tau `IAgentTool` / `ToolResult`.
* **Merged tools into runner startup**: `RuntimeCodingAgentRunner.CreateDefaultTools(...)` accepts extension tools, keeps built-ins by default, and lets extension tools override same-name built-ins, matching upstream custom-tool precedence.
* **Updated extension status UI**: `/extensions` and `/reload` now report tool counts and extension tool details alongside command/module status.
* **Added regression coverage**: Tests now cover JS/TS tool metadata, execution, JSON details, sequential mode, duplicate extension tool name first-wins behavior, and built-in override behavior.
* **Synchronized parity docs**: Updated `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md`, and active parity plan/matrix entries to mark basic `registerTool` runtime as closed while preserving full jiti, `prepareArguments`, rich renderer/details, event lifecycle, hot-reload, and external smoke gaps.

### 🧠 Design Intent (Why)

Upstream `pi-mono` registers extension tools during extension loading, exposes the first registered extension tool for a name, and then lets extension/custom tools override built-in tools when the agent session refreshes its tool registry. Tau already had a limited JS/TS extension command runtime, so this slice extends that same bounded runtime instead of introducing a second extension system. The implementation deliberately stays narrow: it makes tool schema discovery and basic execution usable for local package/extensions, but it does not claim full `@mariozechner/jiti`, renderer, `prepareArguments`, lifecycle, or live reload parity.

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentJavaScriptExtensionRuntime.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionToolAdapter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionCommandStore.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentExtensionCommandStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
