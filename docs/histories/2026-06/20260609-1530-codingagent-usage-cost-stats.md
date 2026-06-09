## [2026-06-09 15:30] | Task: CodingAgent usage-cost stats display

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 继续 `GOAL.md` 100% pi-mono parity 主线，收口上一个 assistant usage-cost persistence 切片后的本地 stats/display 缺口。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` runtime/session/RPC/tests/docs

**Key Actions:**

* **Session stats contract**: `CodingAgentSessionStats` 新增 `Tokens`、`Cost`、`CostRecords`，并由 `CodingAgentSessionUsageSummary` 汇总 assistant `Usage` token buckets 与 `Usage.Cost.Total`。
* **Runtime and CLI display**: flat runner stats、fake runner stats、CLI `/session` 都接入 persisted usage/cost；CLI 在有 usage/cost 时显示 `usage in/out/cache` 与 `$cost`。
* **JSONL tree aggregation**: tree session controller/store 新增 current-branch usage summary；CLI/RPC 在 tree session 模式下从当前 branch 的持久化 assistant message entries 汇总 usage/cost，避免 compaction 后只看 runtime snapshot 而丢失历史成本统计。
* **RPC stats**: RPC `get_session_stats` 现在返回 `tokens.input/output/cacheRead/cacheWrite/total`、`cost`、`costRecords`。
* **Tests and docs**: 补 Runtime runner、CLI `/session`、RPC stats、tree branch usage summary tests；同步 `GOAL.md`、`next.md`、`docs/QUALITY_SCORE.md` 和 active parity plans。

### 🧠 Design Intent (Why)

上游 interactive footer 和 RPC stats 都按 session entries 累计 assistant usage/cost，而不是只展示当前 runtime message snapshot。上一切片只保证 Tau 会持久化 assistant `usage.cost`；本切片把这些已持久化数据暴露到本地 `/session` 和 RPC stats，并在 JSONL tree session 下从当前 branch 汇总，保持 compaction 场景的成本统计连续性。

本切片不声明真实 provider/e2e cost samples、older-session backfill、exact upstream session root/path semantics 或 richer footer/TUI rendering 已完成。

### ✅ Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "RuntimeCodingAgentRunnerTests|CodingAgentCommandRouterTests|CodingAgentRpcHostTests|CodingAgentTreeSessionRedactionTests" --no-restore --verbosity minimal` -> 248/248 passed.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` -> 484/484 passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed: Ai 280, Agent 120, Tui 251, CodingAgent 484, WebUi 61, Pods 216.

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentSessionStats.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentTreeSessionRedactionTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
