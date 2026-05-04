## [2026-05-03 12:30] | Task: CodingAgent session display name

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.x`
* **Runtime**: `Windows PowerShell, .NET 10 preview`

### 📥 User Query

> 继续推进 Tau CodingAgent 上游 session/slash command 迁移，补下一个最小可验证能力。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.CodingAgent.Tests`, docs

**Key Actions:**

* **Session metadata**: `CodingAgentSessionStore` 增加 `Name` 字段，随 provider/model/messages 一起保存和加载；启动时把已有 session name 注入 runner。
* **Runner state**: `ICodingAgentRunner` 增加 `SessionName`，`RuntimeCodingAgentRunner` 和 fake runner 都能持有当前 display name；`/new` 会清空 display name。
* **Slash command**: `CodingAgentCommandRouter` 增加 `/name [display name | clear]`，支持查看、设置和清空当前 session display name，不进入 LLM conversation。
* **Session status**: `/session` 输出加入 display name，便于本地确认当前平面 session 元数据。
* **Tests**: 补 router、host、session store 和 runner stats 测试，固定 name roundtrip、host 持久化、`/name clear` 和 `/session` name 输出。
* **Docs**: 同步 architecture、quality、next 和 active execution plan。

### 🧠 Design Intent (Why)

上游 pi-mono 通过 `session_info` entry 维护 session display name；Tau 当前还只有单文件 snapshot，没有 JSONL entry/tree。直接在 snapshot 上保存 `Name` 是当前最小可验证等价物：能跨启动恢复、能通过 `/name` 修改、能在 `/session` 中确认，同时不假装已经具备上游 session tree。

### 📁 Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSessionStats.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSessionStore.cs`
* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSessionStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
