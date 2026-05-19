## [2026-05-14 20:32] | Task: CodingAgent split-turn prefix summary baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续 Tau.CodingAgent P2 parity，推进 compaction split-turn prefix summary baseline。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` JSONL tree session、compaction restore、HTML transcript、tests、docs

**Key Actions:**

* **Split-turn metadata**: `compaction` JSONL entry 增加 Tau-native `isSplitTurn` 与 `turnPrefixSummary` 字段。当 retained cut point 落在一个 user turn 中间时，tree store 会为被丢弃的 turn prefix 生成确定性上下文摘要。
* **Runtime restore**: branch restore / resume / clone export 仍按 summary + retained messages + post-compaction messages 重建；如果有 split-turn prefix，会把该上下文拼进 compaction summary message，避免 retained suffix 脱离原始请求和早期进展。
* **HTML transcript**: compaction timeline 展示 split-turn prefix section，并把 `turnPrefixSummary` / `isSplitTurn` 纳入 branch outline 搜索文本。
* **Regression tests**: 新增 mid-turn retention 测试，覆盖 JSONL 字段、`firstKeptEntryId`、snapshot restore 和 summary message 内容；同步补 HTML export assertion。

### 🧠 Design Intent (Why)

上游在 compaction cut point 落在一个 turn 中间时，会单独总结被丢弃的 turn prefix，再和历史 summary 合并。Tau 当前还没有上游完整 LLM split-turn summarization 和 compaction extension/cancellation runtime；本切片先用 deterministic prefix context 固定 JSONL/restore/HTML 语义，保证 retained suffix 不丢上下文，同时不把它宣称为完整上游 parity。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCompactionMessages.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
