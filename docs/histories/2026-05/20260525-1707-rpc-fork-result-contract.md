## [2026-05-25 17:07] | Task: RPC fork result contract

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET`

### User Query

> 持续推进 Tau 的 pi-mono parity 移植；多 Agent 并行、减少低收益文档和单元测试，把核心迁移基线写到 AGENTS/active plan 中。

### Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **RPC fork 合同对齐**: RPC `fork` 现在返回上游兼容的 `text` + `cancelled`，其中 `text` 是被 fork 的 user message 文本。
* **fork before 语义修正**: RPC `fork` 选择 user message 时切回该 entry 的 parent branch，避免把被 fork 的 user prompt 留在恢复后的上下文里。
* **Tau 扩展保留**: 既有 `leafId`、`messageCount`、`provider`、`model` 继续作为 Tau 扩展字段返回，避免破坏现有集成。
* **最小回归覆盖**: 新增 focused RPC fork 测试，固定 response data 和恢复后的 runner messages。

### Design Intent (Why)

上游 `fork` RPC public contract 明确返回 `{ text, cancelled }`，而 Tau 之前只返回 `cancelled` 与 Tau 扩展元数据。这个切片只补公开合同和默认 fork-before 行为，不把 `branch_summary` 扩散到 RPC `fork` response，避免把内部 session tree entry 误当公开协议。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `next.md`
