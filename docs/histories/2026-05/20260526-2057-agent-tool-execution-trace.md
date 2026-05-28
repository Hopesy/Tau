## [2026-05-26 20:57] | Task: Agent tool execution trace

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 继续下一轮移植；多 Agent 并行加速，少做低收益单元测试，把核心 baseline 写进仓库。

### Changes Overview

**Scope:** `Tau.Agent` shared tool execution observability

**Key Actions:**

* **Shared trace point**: Strengthened `ToolExecutor` as the common `AgentLoopConfig.LogSink` tool trace point instead of duplicating tool logs in individual hosts.
* **Start fields**: `tool/execution.start` now records `toolCallId`, `toolName`, `executionMode`, and UTF-8 `argumentBytes`.
* **End fields**: `tool/execution.end` now records `success`, `isError`, `failureKind`, `durationMs`, `contentBlockCount`, `textBytes`, and optional `detailType`.
* **Failure classification**: Added lightweight classifications for `not-found`, `blocked`, `invalid-arguments`, `cancelled`, `exception`, `tool-result-error`, and `none`.
* **Safe payloads**: Runtime events intentionally do not write full tool arguments or full tool result content.
* **Mom integration**: The default `RuntimeDelegationAgentRunner` factory now passes its runtime log sink into the inner `RuntimeCodingAgentRunner`, so Mom's default path can emit the shared `tool/execution.*` trace in addition to Mom-specific projection events.
* **Targeted coverage**: Added `AgentRuntimeToolTraceTests` to verify the trace through `AgentRuntime` and `AgentLoopConfig.LogSink`, including success and missing-tool failure paths.
* **Roadmap sync**: Updated `next.md` and `docs/QUALITY_SCORE.md` so unified tool execution trace is no longer listed as a remaining observability gap.

### Design Intent (Why)

Tool execution is a shared runtime concern used by CodingAgent, Mom, WebUi, and future hosts. The repository already had a `LogSink` route into `ToolExecutor`, but the emitted events were too thin for a useful audit trail and did not consistently classify failures. This slice keeps the public agent event stream unchanged and only enriches the runtime JSONL observability path with compact, non-secret summaries.

### Files Modified

* `src/Tau.Agent/Runtime/ToolExecutor.cs`
* `src/Tau.Mom/RuntimeDelegationAgentRunner.cs`
* `tests/Tau.Agent.Tests/AgentRuntimeToolTraceTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`

### Validation

* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter FullyQualifiedName~AgentRuntimeToolTraceTests --no-restore --verbosity minimal` passed: 2/2.
* `dotnet build src\Tau.Agent\Tau.Agent.csproj --no-restore --verbosity minimal` passed: 0 warnings, 0 errors.
* `dotnet build src\Tau.Mom\Tau.Mom.csproj --no-restore --verbosity minimal` passed: 0 warnings, 0 errors.
