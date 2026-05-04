## [2026-05-03 12:15] | Task: CodingAgent command catalog

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.x`
* **Runtime**: `Windows PowerShell, .NET 10 preview`

### 📥 User Query

> 继续推进 Tau CodingAgent slash command 基础层，并收口当前命令面的维护方式。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.CodingAgent.Tests`, docs

**Key Actions:**

* **Command catalog**: 新增 `CodingAgentCommandCatalog`，集中维护当前已支持本地 slash command 的 name、usage 和 description。
* **Router cleanup**: `/help` 输出和 `/help`、`/new`、`/quit`、`/session`、`/model`、`/provider`、`/models`、`/providers`、`/auth`、`/login` 的参数错误统一从 catalog 取 usage。
* **Providers validation**: `/providers` 从直接内联状态输出改成 handler，额外参数返回 `usage: /providers`。
* **Tests**: 补 catalog help line / command metadata 基本约束测试，以及 `/providers` 参数错误测试。
* **Docs**: 同步 architecture、quality、next 和 active execution plan。

### 🧠 Design Intent (Why)

当前本地命令数量已经进入两位数，如果继续把 `/help`、usage 和命令描述分散在 router 分支里，后续迁移 `/copy`、`/export`、`/resume` 等命令时会产生重复字符串和漂移风险。本轮只做轻量静态 catalog，不引入上游完整 extension/prompt/skill 动态 command registry，保持实现简单且符合 Tau 当前能力边界。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
