## [2026-06-11 13:34] | Task: CodingAgent extension UI fire-and-forget RPC bridge

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 按 `GOAL.md` 继续 Tau 100% pi-mono parity 迁移，接手 CodingAgent JS/TS extension runtime 缺口。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **Extension UI bridge**: 让 JS/TS limited extension runtime 在绑定 RPC host 后向 handler 暴露 `ctx.hasUI=true`，并把 `ctx.ui.notify(...)`、`setStatus(...)`、string-array `setWidget(...)`、`setTitle(...)`、`setEditorText(...)` 和 `pasteToEditor(...)` 收集为 RPC `extension_ui_request`。
* **RPC integration**: `CodingAgentRpcHost` attach `CodingAgentRpcExtensionUiBridge` 后同步注入 `CodingAgentExtensionCommandStore` / shared JS runtime，使真实 extension tool handler 的 fire-and-forget UI action 能写到 JSONL stdout。
* **Regression coverage**: 新增真实 JavaScript extension tool 回归，固定 notify/status/widget/title/editor-text/paste-to-editor 的 RPC request shape。
* **Virtual import coverage**: 补充 JS extension focused 回归，固定当前 limited runtime 的 virtual package import 子集：`@sinclair/typebox`、`@mariozechner/pi-ai`、`@mariozechner/pi-ai/oauth`、`@mariozechner/pi-agent-core`、`@mariozechner/pi-tui` 和 `@mariozechner/pi-coding-agent` 能在真实 Node runtime 内参与 command/tool 注册与执行。
* **Docs sync**: 同步 `GOAL.md`、`next.md`、active parity plan/matrix 和 `docs/QUALITY_SCORE.md`，明确本切片只关闭 fire-and-forget UI request 与 limited virtual import stub baseline。

### 🧠 Design Intent (Why)

上游 RPC mode 中 `notify`、`setStatus`、string-array `setWidget`、`setTitle` 和 `setEditorText` 是 fire-and-forget request，不需要等待 `extension_ui_response`；`select`、`confirm`、`input` 和 `editor` 是等待型 dialog，需要 pending response。Tau 当前 JS/TS extension runtime 仍是一次性 Node subprocess，C# 只能在 Node 退出后读取 stdout result，因此本轮先关闭可审计的 fire-and-forget UI bridge，不伪造等待型 dialog parity。

Virtual import 子集只固定当前 limited runtime 已能稳定承载的 package identity 与 helper shape，目的是让真实 JS extension 可以用上游常见 import 参与 command/tool 注册；这不是完整 `@mariozechner/jiti` import/alias/virtualModules parity，也不代表真实 package consumer/network smoke 已完成。

### ✅ Validation

* `node -e "const m=require('node:module'); console.log(process.version); console.log(typeof m.registerHooks, typeof m.stripTypeScriptTypes)"`：当前 Node 为 v24.12.0，`registerHooks` / `stripTypeScriptTypes` 均可用。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "LoadStatus_LoadsJavascriptExtensionWithVirtualPackageImports" --no-restore --verbosity minimal`：1/1 passed。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "ExtensionToolUiFireAndForgetActionsEmitRpcRequests|LoadLifecycleEventSink_EmitsJavascriptMessageLifecycleHandler|RunAsync_EmitsJavascriptLifecycleEvents|RunAsync_LogsJavascriptLifecycleHandlerErrorWithoutFailingRun" --no-restore --verbosity minimal`：4/4 passed。
* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "ToolArgumentValidatorTests|AiCliRunnerTests" --no-restore --verbosity minimal`：18/18 passed。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：533/533 passed。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentJavaScriptExtensionRuntime.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionCommandStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentExtensionCommandStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
