## [2026-05-26 22:08] | Task: Runtime log correlation context

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 继续下一轮快速移植，少做低收益单测，优先把核心 runtime observability baseline 写实。

### Changes Overview

**Scope:** `Tau.Ai`, `Tau.Agent`, `Tau.CodingAgent`, `Tau.WebUi`, targeted tests, migration status docs.

**Key Actions:**

* **Shared context type**: Added `TauRuntimeLogContext` with `correlationId`, `sessionId`, and `messageId` field injection helpers.
* **Agent run correlation**: `RuntimeCodingAgentRunner` now creates or inherits a per-run `correlationId` and writes the same context to `agent/run.start`, `agent/run.error`, `agent/run.end`, and `agent/run.cancel`.
* **Tool trace propagation**: `AgentLoopConfig.LogContext` flows through `AgentRuntime` into `ToolExecutor`, so `tool/execution.start|end` inherits the same runtime correlation fields as the enclosing agent run.
* **WebUi session context**: WebUi message stream default runner now injects the Web session id plus a transient per-send message id into the runner context, without changing the persisted WebChat message schema.
* **Compatibility**: Kept the existing three-argument WebUi runner factory constructor path working by adapting it to the new context-aware factory shape.
* **Targeted coverage**: Extended existing runner/tool/WebUi tests to assert correlation fields and same-run correlation equality.
* **Status sync**: Updated `next.md`, `docs/QUALITY_SCORE.md`, and the active migration plan to mark the minimal cross-module correlation baseline as present, while keeping Mom/Pods/real e2e trace correlation as remaining work.

### Design Intent (Why)

The runtime log baseline already had separate `agent/run.*` and `tool/execution.*` events, but they could not be reliably joined across WebUi session, agent run, and tool execution. This slice keeps `TauLogEvent` and JSONL top-level format unchanged, uses fields only, and makes the existing `AgentLoopConfig` run boundary carry the context into tool execution. WebUi uses a transient message id for now because `WebChatMessageDto` does not yet persist stable message ids.

### Files Modified

* `src/Tau.Ai/Observability/TauRuntimeLogContext.cs`
* `src/Tau.Agent/Runtime/AgentLoopConfig.cs`
* `src/Tau.Agent/Runtime/AgentRuntime.cs`
* `src/Tau.Agent/Runtime/ToolExecutor.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.WebUi/Services/WebUiRunnerFactory.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `tests/Tau.Agent.Tests/AgentRuntimeToolTraceTests.cs`
* `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

### Validation

* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter FullyQualifiedName~AgentRuntimeToolTraceTests --no-restore --verbosity minimal` passed: 2/2.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter FullyQualifiedName~RuntimeCodingAgentRunnerTests.RunAsync_EmitsRun --no-restore --verbosity minimal` passed: 2/2.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --filter FullyQualifiedName~WebUiEndpointTests.MessageStreamEndpoint_DefaultRunnerLogsAgentRunEvents --no-restore --verbosity minimal` passed: 1/1.
* A first parallel build attempt hit the known Windows/Roslyn shared `Tau.Ai.dll` file lock. `dotnet build-server shutdown` cleared it, and subsequent verification was run serially.
