## [2026-05-03 13:00] | Task: CodingAgent export command

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.x`
* **Runtime**: `Windows PowerShell, .NET 10 preview`

### 📥 User Query

> 继续推进 Tau CodingAgent 上游 slash command 基础层迁移。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.CodingAgent.Tests`, docs

**Key Actions:**

* **Slash command**: `CodingAgentCommandRouter` 增加 `/export <path>`，把当前 Tau 平面 session snapshot 写到指定 JSON 文件。
* **Serialization reuse**: 导出复用 `CodingAgentSessionStore`，包含 provider、model、messages 和 session display name。
* **Command catalog**: `CodingAgentCommandCatalog` 增加 `/export`，`/help` 输出同步更新。
* **Tests**: 补 router 导出 roundtrip、usage 错误，以及 host 渲染和文件写出测试。
* **Docs**: 同步 architecture、quality、next 和 active execution plan。

### 🧠 Design Intent (Why)

上游 `/export` 默认偏 HTML/JSONL session tree；Tau 当前事实源仍是单文件 snapshot。先实现显式路径的 snapshot JSON 导出，能提供可用备份和迁移入口，同时不把尚未存在的 HTML rendering、JSONL tree、import/share 体系伪装成已完成能力。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
