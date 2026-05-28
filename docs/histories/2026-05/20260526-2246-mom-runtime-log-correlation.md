## [2026-05-26 22:46] | Task: Mom runtime log correlation

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET`

### User Query

> 继续下一轮快速移植，少做低收益单测，优先推进核心 baseline。

### Changes Overview

**Scope:** `Tau.Mom`, `Tau.Agent.Tests`, migration status docs.

**Key Actions:**

* **Delegation context**: `RuntimeDelegationAgentRunner` now builds a per-delegation `TauRuntimeLogContext` from request metadata.
* **Stable ids**: `requestId` is used first for `correlationId/messageId`; Slack-style `ts/messageTs` is the fallback; `sessionId` is derived from `channel:threadTs|ts`.
* **Event correlation**: Mom `delegation.start/end`, `response.start/end`, `tool.start/end`, and `usage` events now carry the same context fields.
* **Runner propagation**: The same context is passed to `ICodingAgentRunner.RunAsync(...)`, so inner `agent/run.*` and `tool/execution.*` traces can join the Mom delegation.
* **Targeted coverage**: Extended the existing Mom runtime delegation log test to assert event fields and runner context propagation.

### Design Intent (Why)

Mom already emitted useful response/tool/usage runtime events, but they were not joinable with the underlying CodingAgent runner and tool execution events. This slice uses channel metadata that already exists in `DelegationRequest.Metadata` instead of local filesystem paths, because working directories are execution locations rather than durable conversation ids. It intentionally leaves `log.jsonl`, `status.json`, outbox, and channel session persistence unchanged.

### Files Modified

* `src/Tau.Mom/RuntimeDelegationAgentRunner.cs`
* `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

### Validation

* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter FullyQualifiedName~RuntimeDelegationAgentRunnerTests.ExecuteAsync_AggregatesUsage_StopReason_ToolEventsWithDuration --no-restore --verbosity minimal` passed: 1/1.
* `dotnet build src\Tau.Mom\Tau.Mom.csproj --no-restore --verbosity minimal` passed with 0 warnings and 0 errors.
