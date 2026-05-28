## [2026-05-26 22:31] | Task: CodingAgent runtime log context

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET`

### User Query

> 继续快速移植，不需要做低收益单元测试；多 Agent 并行加速，把核心 baseline 写进仓库记录。

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.CodingAgent.Tests`, migration docs.

**Key Actions:**

* **Per-run runner context**: `ICodingAgentRunner.RunAsync(...)` added optional per-run `TauRuntimeLogContext` overloads while keeping existing callers source-compatible.
* **Runtime merge rule**: `RuntimeCodingAgentRunner` now prefers the per-run context, falls back to configured context, then ensures a correlation id before emitting `agent/run.*` and passing context into `AgentRuntime`.
* **RPC context**: `CodingAgentRpcHost` now creates a prompt-level context from RPC command id and JSONL tree session summary, then passes it to runner calls.
* **CLI retry context**: `CodingAgentHost` now creates one turn context per user input and reuses it across retry attempts, so retry attempt logs remain joinable.
* **Targeted coverage**: Tests capture and verify per-run context override, RPC command/session context propagation, and CLI retry context stability.
* **Minimal docs sync**: Updated `next.md`, `docs/QUALITY_SCORE.md`, and the active migration plan to mark the baseline without expanding broad status docs.

### Design Intent (Why)

The previous runtime log correlation baseline joined WebUi runner events and tool execution, but direct CodingAgent CLI/RPC runs still lacked a stable per-turn or per-command runtime context. This slice adds that context at the host boundary where the command/turn identity exists. It deliberately does not preallocate JSONL message entry ids, because real entry ids are assigned after successful runner sync/persist; preallocating them would change retry and rollback semantics.

### Files Modified

* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "FullyQualifiedName~RuntimeCodingAgentRunnerTests.RunAsync_PerRunLogContextOverridesConfiguredContext|FullyQualifiedName~CodingAgentRpcHostTests.RunAsync_PromptPassesCommandAndTreeSessionLogContextToRunner|FullyQualifiedName~CodingAgentHostTests.RunAsync_RetryableTurnPassesStableTreeSessionLogContextToRunner" --no-restore --verbosity minimal` passed: 3/3.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "FullyQualifiedName~RuntimeCodingAgentRunnerTests.RunAsync_EmitsRun|FullyQualifiedName~CodingAgentRpcHostTests.RunAsync_PromptWritesAcceptedResponseAndAgentEvents|FullyQualifiedName~CodingAgentHostTests.RunAsync_RetryableAgentEndError_RetriesAndPersistsSuccessfulAttempt" --no-restore --verbosity minimal` passed: 4/4.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --filter FullyQualifiedName~WebUiEndpointTests.MessageStreamEndpoint_DefaultRunnerLogsAgentRunEvents --no-restore --verbosity minimal` passed: 1/1.
* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` passed with 0 warnings and 0 errors.
