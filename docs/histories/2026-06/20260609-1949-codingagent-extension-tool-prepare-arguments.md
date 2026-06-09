## [2026-06-09 19:49] | Task: CodingAgent extension tool prepareArguments

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 继续 `GOAL.md` 100% pi-mono parity 主线，收口下一段可验证 CodingAgent extension runtime 缺口。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` JS/TS extension tool runtime / parity docs

**Key Actions:**

* **Added extension tool argument preparation**: `CodingAgentJavaScriptExtensionRuntime` now detects `prepareArguments` on JS/TS registered tools and can call it through the existing limited Node subprocess runtime.
* **Plugged into Agent validation order**: `CodingAgentExtensionToolAdapter.PrepareArgumentsAsync(...)` now invokes the extension shim before Tau Agent schema validation, matching upstream `ToolDefinition.prepareArguments` pass-through semantics.
* **Kept no-shim behavior stable**: tools without `prepareArguments` still receive raw args unchanged; `undefined` prepared results are normalized to an empty object instead of JSON `null`.
* **Added regression coverage**: tests now cover JavaScript and TypeScript registered tools that prepare raw args before execution.
* **Synchronized parity docs**: `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md`, and the active parity plan/matrix now mark `prepareArguments` baseline as locally covered while keeping full jiti, rich renderer/details, lifecycle, hot-swap, package/network smoke, and final verified parity open.

### 🧠 Design Intent (Why)

Upstream `pi-mono` defines `prepareArguments?: (args: unknown) => Static<TParams>` on extension `ToolDefinition` and passes it directly into the Agent tool wrapper. Tau already had `IAgentTool.PrepareArgumentsAsync(...)` and `ToolExecutor` calls that hook before schema validation, so this slice uses the existing contract rather than creating a separate custom-tool path. The implementation intentionally stays inside the limited JS/TS extension runtime and only closes the local parameter-preparation baseline.

### ✅ Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` passed with 0 warnings / 0 errors.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "ExtensionCommandStore|RuntimeCodingAgentRunner" --no-restore --verbosity minimal` passed 37/37.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` passed 514/514.
* `git diff --check` passed; Git only reported existing CRLF normalization warnings for touched Markdown files.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed: `Tau.Ai.Tests` 280/280, `Tau.Agent.Tests` 121/121, `Tau.Tui.Tests` 251/251, `Tau.CodingAgent.Tests` 514/514, `Tau.WebUi.Tests` 61/61, `Tau.Pods.Tests` 216/216.

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentJavaScriptExtensionRuntime.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionToolAdapter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionCommandStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentExtensionCommandStoreTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
