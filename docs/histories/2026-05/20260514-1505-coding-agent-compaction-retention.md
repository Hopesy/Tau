## [2026-05-14 15:05] | Task: coding-agent compaction retention

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 继续继续，完成整个框架的移植。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` JSONL compaction restore parity

**Key Actions:**

* **[Retention cut-point baseline]**: `CodingAgentTreeSessionController.RecordCompaction()` 现在会在 compaction 写入前为缺省 `FirstKeptEntryId` 自动选择 current boundary 后最近 message cut-point。
* **[Runtime restore]**: 写入 compaction entry 后，controller 会通过 tree snapshot 把 runner 恢复成 `summary + retained messages + post-compaction messages`，避免当前内存会话和 resume 行为不一致。
* **[Branch restore]**: `CodingAgentTreeSessionStore.LoadCurrentBranchSnapshot()` 现在按最后一个 compaction entry 重建消息：先 summary，再从 `firstKeptEntryId` 到 compaction 前的 retained messages，最后追加 compaction 后的新消息。
* **[Export/remap]**: 保留现有 JSONL branch export / clone 的 `firstKeptEntryId` remap 行为，并新增回归覆盖，防止导出 session 后 retained messages 丢失。
* **[Config seam]**: 新增 `TAU_CODING_AGENT_COMPACT_KEEP_RECENT_TOKENS` 和 `TAU_CODING_AGENT_COMPACT_KEEP_RECENT_MESSAGES`；默认先按 20000 estimated tokens 选择 cut-point，未命中时回落到最近 4 条 message；两个值都设为 `0` 可关闭这层 Tau-native retention baseline。
* **[Options seam]**: 新增 `CodingAgentCompactionRetentionOptions`，生产默认从环境读取，测试可直接注入 options，避免并行测试共享环境变量导致 cut-point 断言漂移。
* **[Tests/docs]**: `Tau.CodingAgent.Tests` 新增 recent-message retention restore/export remap、token-budget cut-point 和 invalid `firstKeptEntryId` fallback 覆盖；README、ARCHITECTURE、QUALITY_SCORE、active plan 和 `next.md` 同步更新。

### 🧠 Design Intent (Why)

上游 session manager 的 compaction 语义不是简单清空历史后只保留 summary，而是用 `firstKeptEntryId` 让恢复路径得到 summary、保留的 recent work 和 compaction 后的新消息。Tau 已经写入了 `firstKeptEntryId` 字段，但此前缺省为空，branch restore 也只会清空消息再放入 summary，导致 JSONL compaction entry 只是审计占位。

这次没有直接迁入上游完整 split-turn prefix summary、retry/rollback。原因是 Tau 当前还没有完整上游 compaction settings / extension hook / retry runtime；先以 token budget 加 message-count fallback 做 Tau-native baseline，可以立刻让 manual / auto compaction、resume、clone/export 共享同一可验证语义，并把后续 split-turn prefix summary 与 retry/rollback 留在 active plan 里继续推进。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`

### ✅ Verification

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
* `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`
* `git diff --check`
