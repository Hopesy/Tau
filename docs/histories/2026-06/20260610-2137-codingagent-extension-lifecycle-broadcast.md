## [2026-06-10 21:37] | Task: 继续 Tau.CodingAgent extension runtime 收口

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell`

### 📥 User Query

> 继续继续

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` runtime/Program/tests, docs sync

**Key Actions:**

* **Lifecycle broadcast**: `CodingAgentExtensionCommandStore` 现在可以收集 JS/TS 扩展里 `pi.on(...)` 注册的 `agent_start/end`、`turn_start/end`、`message_start/update/end` 和 `tool_execution_start/update/end` handler，`RuntimeCodingAgentRunner` 在产出对应 `AgentEvent` 前把只读事件 payload 传给扩展。
* **JS runtime**: `CodingAgentJavaScriptExtensionRuntime` 新增 `emitEvent` 入口，支持扩展生命周期事件回调并返回 handler error 汇总；普通 handler exception 不会中断 agent run。
* **Program wiring**: `src/Tau.CodingAgent/Program.cs` 把生命周期事件 sink 接到主 runner，让实际 CLI 运行路径能触发这条扩展广播链。
* **Runtime log**: handler error 以 `extension/event.error` runtime log 记录；真正的 lifecycle sink 基础设施异常仍按 `agent/run.error` 处理。
* **Tests**: `tests/Tau.CodingAgent.Tests/CodingAgentExtensionCommandStoreTests.cs` 和 `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs` 补充 JS 扩展生命周期回归，证明 message/agent event 能写回外部 `events.log`，且 handler 抛错时 run 仍正常结束。

### 🧠 Design Intent (Why)

上游扩展 runtime 不只允许 command/tool 注册，还允许观察 agent/message/tool lifecycle。Tau 之前已经有 `tool_call` / `tool_result` 的可变拦截面，这一轮把只读生命周期广播补上，目的是让扩展能读取运行态事件而不介入执行流，避免把观察和 mutation 混成一层。

### ✅ Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "RuntimeCodingAgentRunnerTests|CodingAgentExtensionCommandStoreTests" --no-restore --verbosity minimal`：51/51 passed
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：531/531 passed
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed，项目级计数 Ai 287、Agent 123、Tui 251、CodingAgent 531、WebUi 61、Pods 216

### 📁 Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionCommandStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentJavaScriptExtensionRuntime.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionLifecycleEventSink.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentExtensionCommandStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
