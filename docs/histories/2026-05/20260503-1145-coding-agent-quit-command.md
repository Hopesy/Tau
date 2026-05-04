## [2026-05-03 11:45] | Task: CodingAgent quit command

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.x`
* **Runtime**: `Windows PowerShell, .NET 10 preview`

### 📥 User Query

> 继续推进 Tau CodingAgent 的上游 slash command 迁移。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.CodingAgent.Tests`, docs

**Key Actions:**

* **Command result**: `CodingAgentCommandResult` 增加 `ShouldExit`，用结构化结果表达退出信号。
* **Slash command**: `CodingAgentCommandRouter` 增加 `/quit`，参数错误返回 `usage: /quit`；正常执行返回退出结果，不调用 runner。
* **Host loop**: `CodingAgentHost` 消费 `ShouldExit`，渲染一次 `Goodbye!` 后停止读取后续输入；文本 `exit` 兼容路径保持不变。
* **Tests**: 补 router 和 host 测试，固定 `/quit` 不进入 LLM、不读取后续输入、不重复输出 goodbye。
* **Docs**: 同步 architecture、quality、next 和 active execution plan。

### 🧠 Design Intent (Why)

`/quit` 是上游内建命令列表里最小、低风险且用户可见的控制命令。实现时没有让 router 直接操作 UI 或进程，而是通过 command result 传递退出信号，由 host 统一负责渲染、持久化和 loop 生命周期，保持命令解析层可测试、无 UI 依赖。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandResult.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
