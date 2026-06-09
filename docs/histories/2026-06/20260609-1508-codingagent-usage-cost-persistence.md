# [2026-06-09 15:08] | Task: CodingAgent usage cost persistence

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### User Query

> 继续当前 `GOAL.md` 100% pi-mono parity 迁移主线。

### Changes Overview

**Scope:** `Tau.Ai`, `Tau.Agent`, `Tau.CodingAgent`, root session audit scripts

**Key Actions:**

* **Usage cost model**: Added optional `UsageCost` persistence to `Usage`, keeping existing token-only constructor calls source-compatible.
* **Runtime enrichment**: `AgentRuntime` now enriches provider done/error/failure assistant messages with provider/model/api/timestamp metadata and computed `usage.cost` when the active model has pricing.
* **Session persistence**: `CodingAgentSessionStore` now round-trips assistant usage tokens, computed cost, provider/model/api and timestamp through flat session snapshots and JSONL tree session entries.
* **Proxy preservation**: `ProxyStreamProvider` now preserves existing `usage.cost` when proxy requests or responses carry it.
* **Audit scripts**: `report-session-costs.ps1` no longer claims current CodingAgent sessions lack cost persistence; `verify-session-audit-scripts.ps1` uses a stable date window so its fixed fixture does not age out of the report.
* **Docs**: Updated `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md`, the active parity plan and matrix so local assistant `message.usage.cost` persistence is closed while real provider/e2e cost evidence remains open.

### Design Intent (Why)

Upstream `scripts/cost.ts` aggregates assistant `message.usage.cost` from session JSONL and does not infer price from raw token counts. Tau already had a compatible report script and model cost calculator, but CodingAgent did not persist the cost object in real session messages. The fix writes the cost at the source of truth, before assistant messages enter Agent state and CodingAgent persistence, so report scripts consume real persisted data instead of recalculating historical sessions.

The implementation only writes `usage.cost` when model pricing is known or an upstream/proxy response already supplied cost. Sessions for models without pricing still persist token usage without a dollar amount. This avoids inventing cost for custom or unknown models.

### Files Modified

* `src/Tau.Ai/Abstractions/Messages.cs`
* `src/Tau.Ai/Serialization/TauAiJsonContext.cs`
* `src/Tau.Agent/Runtime/AgentRuntime.cs`
* `src/Tau.Agent/Proxy/ProxyStreamProvider.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSessionStore.cs`
* `scripts/report-session-costs.ps1`
* `scripts/verify-session-audit-scripts.ps1`
* `tests/Tau.Agent.Tests/AgentRuntimeContractTests.cs`
* `tests/Tau.Agent.Tests/ProxyStreamProviderTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSessionStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentTreeSessionRedactionTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`

### Verification

* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter "AgentRuntimeContractTests|ProxyStreamProviderTests" --no-restore --verbosity minimal`: 18/18 passed
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentSessionStoreTests|CodingAgentTreeSessionRedactionTests" --no-restore --verbosity minimal`: 10/10 passed
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal`: 120/120 passed
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`: 480/480 passed
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-session-audit-scripts.ps1`: 15 assertions passed
* `git diff --check`: passed; Git reported only CRLF normalization warnings
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`: passed (`Tau.Ai.Tests` 280, `Tau.Agent.Tests` 120, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 480, `Tau.WebUi.Tests` 61, `Tau.Pods.Tests` 216)

### Remaining Boundary

This closes local default assistant `message.usage.cost` persistence. It does not close real provider/e2e cost samples, usage-cost UI/display, older-session backfill, exact upstream `~/.pi/agent/sessions` path semantics, full share/export transcript semantics, or final `verified` matrix status.
