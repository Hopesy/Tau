## [2026-05-28 13:37] | Task: Agent facade parity baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 持续执行 Tau 100% pi-mono parity 多 Agent 移植计划，按 GOAL 和 active execution plan 推进，不要停在计划层。

### Changes Overview

**Scope:** `Tau.Agent` high-level facade and Agent package parity docs.

**Key Actions:**

* **Agent facade baseline**: 新增 `Tau.Agent.Agent` 与 `AgentOptions`，对齐上游 `packages/agent/src/agent.ts` 的 stateful wrapper 基线，覆盖 prompt、continue、subscribe、queue、abort、wait 和 reset。
* **State and events**: `AgentState` 增加 system prompt、model、tools；`MessageStartEvent` / `MessageEndEvent` 扩展为可承载任意 `ChatMessage`，facade 发出 prompt message lifecycle，runtime 发出 tool result message lifecycle。
* **Continue semantics**: assistant tail 下无 queued steering/follow-up 时拒绝 continue；有 queued steering/follow-up 时按上游语义 drain 队列并继续执行。
* **Docs and matrix**: 更新 Agent file-level matrix、100% parity plan、`next.md`、`docs/ARCHITECTURE.md` 和 `docs/QUALITY_SCORE.md`，明确本切片仍不是完整 `agent-loop.ts` parity。

### Design Intent (Why)

上游 `packages/agent/src/agent.ts` 的高层 `Agent` facade 是 CodingAgent、WebUi 和 Mom 继续共享 Agent 行为合同的公共入口。Tau 原先只有底层 `AgentRuntime`，无法直接固定 prompt/continue/listener/queue/idle/reset 这些用户可见语义。本切片先补 .NET-native facade，并保留底层 runtime，避免重写现有应用面。

### Files Modified

* `src/Tau.Agent/Agent.cs`
* `src/Tau.Agent/Abstractions/AgentEvents.cs`
* `src/Tau.Agent/Abstractions/AgentState.cs`
* `src/Tau.Agent/Runtime/AgentLoopConfig.cs`
* `src/Tau.Agent/Runtime/AgentRuntime.cs`
* `src/Tau.Mom/RuntimeDelegationAgentRunner.cs`
* `tests/Tau.Agent.Tests/AgentFacadeTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`

### Validation

* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter AgentFacadeTests --verbosity minimal` -> 6/6 passed.
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --verbosity minimal` -> 97/97 passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed; covered `Tau.Ai.Tests` 221, `Tau.Agent.Tests` 97, `Tau.Tui.Tests` 190, `Tau.CodingAgent.Tests` 433, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 166.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` -> passed; repeated project tests and completed WebUi + Mom `--once` smoke.
