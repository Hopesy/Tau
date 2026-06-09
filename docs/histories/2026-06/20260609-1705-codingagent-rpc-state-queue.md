## [2026-06-09 17:05] | Task: CodingAgent RPC state queue baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 继续 `GOAL.md` 的 100% pi-mono parity 迁移主线。

### 🛠 Changes Overview

**Scope:** `Tau.Agent` queue state and `Tau.CodingAgent` RPC state projection.

**Key Actions:**

* **Runtime queue count**: Added `AgentRuntime.PendingMessageCount`, backed by steering/follow-up queue write/drain/clear accounting.
* **Runner state contract**: Extended `ICodingAgentRunner` with `PendingMessageCount` and `IsCompacting`; production runner maps queue count from `AgentRuntime` and tracks compact/branch-summary active state.
* **RPC get_state parity**: `CodingAgentRpcHost.CreateState()` now returns real `pendingMessageCount` and `isCompacting` instead of hard-coded `0` / `false`.
* **Compact timing**: RPC `compact` now runs as a background task, keeping the compact response completion semantics while allowing same-stream `get_state` to observe `isCompacting=true`.
* **Regression coverage**: Added RPC tests for pending queue state, user `message_start` consumption, and active compaction state; added Agent runtime tests for pending count drain/clear behavior.
* **Docs sync**: Updated `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md`, and active parity plan/matrix entries while keeping CodingAgent RPC final status `partial`.

### 🧠 Design Intent (Why)

Upstream `RpcSessionState` exposes queue count and compaction activity as first-class state. Tau previously hard-coded both values in RPC state, which hid active steering/follow-up queues and compact activity from RPC clients. The implementation places queue count at the true queue owner (`AgentRuntime`) and exposes it through the runner interface, instead of duplicating state in the RPC host. `compact` is run in the background because upstream RPC line handling is concurrent enough for `get_state` to observe compaction while the compact command response is still pending.

### ✅ Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentRpcHostTests" --no-restore --verbosity minimal`：63/63 passed.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter Rpc --no-restore --verbosity minimal`：70/70 passed.
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter "AgentRuntimeQueueModeTests" --no-restore --verbosity minimal`：5/5 passed.
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal`：121/121 passed.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：496/496 passed.
* `git diff --check`：passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed；Ai 280、Agent 121、Tui 251、CodingAgent 496、WebUi 61、Pods 216.

### 📁 Files Modified

* `src/Tau.Agent/Runtime/AgentRuntime.cs`
* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `tests/Tau.Agent.Tests/AgentRuntimeQueueModeTests.cs`
* `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.WebUi.Tests/FakeWebUiRunner.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
