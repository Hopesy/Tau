## [2026-05-03 12:00] | Task: CodingAgent help command

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.x`
* **Runtime**: `Windows PowerShell, .NET 10 preview`

### 📥 User Query

> 继续推进 Tau CodingAgent 的上游 slash command 基础层迁移。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.CodingAgent.Tests`, docs

**Key Actions:**

* **Slash command**: `CodingAgentCommandRouter` 增加 `/help`，返回当前 Tau 已支持的本地命令列表。
* **Validation behavior**: `/help` 不接受额外参数，错误时返回 `usage: /help`；正常执行不调用 runner，不进入 LLM conversation。
* **Tests**: 补 router 和 host 测试，固定 help 输出、参数校验和 UI 渲染路径。
* **Docs**: 同步 architecture、quality、next 和 active execution plan。

### 🧠 Design Intent (Why)

Tau 已经有 `/new`、`/session`、`/quit`、model/settings、auth 和 `/compact` 等本地命令，缺少命令面自描述会让用户只能靠文档或记忆使用。当前先用静态列表描述已移植命令，不引入上游 extension/prompt/skill 动态 slash command 发现，避免把还不存在的扩展体系伪装成已完成能力。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
