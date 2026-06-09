## [2026-06-09 20:45] | Task: CodingAgent extension tool input mutation

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### User Query

> 继续 `GOAL.md` 100% pi-mono parity migration.

### Changes Overview

**Scope:** `Tau.Agent`, `Tau.CodingAgent`, parity docs/history

**Key Actions:**

* **Tool interceptor contract**: extended `ToolCallDecision` so a before-tool interceptor can return mutated JSON arguments.
* **Agent tool execution**: updated both parallel and sequential `ToolExecutor` paths to pass mutated arguments to later interceptors, tool update events, tool execution, and after-tool hooks without re-running schema validation.
* **Extension runtime bridge**: updated the JS/TS limited extension runtime to return mutated `event.input` from `tool_call` handlers and chain mutations across extension modules.
* **Regression coverage**: added Agent tests for parallel/sequential mutation and CodingAgent tests for cross-module JS hook mutation.
* **Docs/history**: synced `GOAL.md`, `next.md`, active parity plan, parity matrix, and quality score to mark local `tool_call` input mutation baseline as closed while keeping broader extension runtime gaps open.

### Design Intent

Upstream `core/extensions/types.ts` documents `event.input` as mutable, says later `tool_call` handlers see earlier mutations, and says mutation is not revalidated. The Tau implementation mirrors that contract at the shared `Tau.Agent` interceptor layer instead of special-casing only CodingAgent extension tools, so built-in and extension tools share the same behavior.

The implementation intentionally keeps validation before mutation. This preserves the existing prepare/validate boundary and matches upstream's "No re-validation is performed after mutation" behavior. The new Agent tests mutate an integer-validated argument into a string and assert the tool still executes, fixing that behavior as a contract.

### Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter "ToolInterceptorMutation|RunAsync_ToolInterceptorMutation" --no-restore --verbosity minimal` -> 2/2 passed
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "LoadToolInterceptors_MutatesJavascriptToolCallInputAcrossModules|LoadToolInterceptors_BlocksJavascriptToolCallHandler|LoadToolInterceptors_RewritesJavascriptToolResultHandler" --no-restore --verbosity minimal` -> 3/3 passed
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "ExtensionCommandStore|RuntimeCodingAgentRunner" --no-restore --verbosity minimal` -> 40/40 passed
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal` -> 123/123 passed
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` -> 517/517 passed
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed; project counts: Ai 280, Agent 123, Tui 251, CodingAgent 517, WebUi 61, Pods 216

### Remaining Boundaries

This closes only the local JS/TS extension `tool_call` input mutation baseline. It does not close full `@mariozechner/jiti` import/alias/virtualModules parity, rich tool content/renderers/details semantics, flags/shortcuts and broader lifecycle events, real extension UI calls, live `/reload` tool hot-swap, package consumer/network smoke, or final extension runtime `verified` status.

### Files Modified

* `src/Tau.Agent/Abstractions/IToolInterceptor.cs`
* `src/Tau.Agent/Runtime/ToolExecutor.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionToolEventInterceptor.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentJavaScriptExtensionRuntime.cs`
* `tests/Tau.Agent.Tests/AgentRuntimeContractTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentExtensionCommandStoreTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
