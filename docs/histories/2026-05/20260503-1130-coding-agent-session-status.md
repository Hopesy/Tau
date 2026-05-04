## [2026-05-03 11:30] | Task: CodingAgent session status command

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.x`
* **Runtime**: `Windows PowerShell, .NET 10 preview`

### 📥 User Query

> 继续推进 Tau 从 pi-mono 的 CodingAgent 命令迁移，当前切片补齐 session 状态入口。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.CodingAgent.Tests`, docs

**Key Actions:**

* **Session stats model**: 新增 `CodingAgentSessionStats`，由 runner 统一计算当前 provider/model、消息数、tool result 数、assistant tool call 数和可选 session 文件路径。
* **Slash command**: 在 `CodingAgentCommandRouter` 增加 `/session`，作为本地命令返回当前平面 session status；参数错误返回 `usage: /session`，不进入 LLM conversation。
* **Host wiring**: `CodingAgentHost` 将当前 `CodingAgentSessionStore.Path` 注入 router，使 `/session` 能报告真实 session 文件路径。
* **Tests**: 补 router、host、runtime runner 三层测试，固定 `/session` 输出、参数校验、session path 传递和消息/tool call 计数。
* **Docs**: 同步 architecture、quality、next 和 active execution plan，明确当前不是上游 JSONL tree/resume/full stats。

### 🧠 Design Intent (Why)

先把 Tau 当前已经真实存在的平面 session 状态暴露出来，而不是一次性移植上游完整 session tree。这样 `/session` 可立即服务本地排障和使用反馈，同时保持边界诚实：当前只有单文件 snapshot 与 runtime messages，resume/tree/branch/full stats 仍是后续切片。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentSessionStats.cs`
* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
