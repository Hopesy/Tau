## [2026-05-13 15:08] | Task: coding-agent session metadata

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 继续推进 Tau 的 pi-mono 迁移。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` JSONL session metadata

**Key Actions:**

* **[Tree summary metadata]**: `CodingAgentTreeSessionSummary` 现在包含 JSONL header 的 `cwd` 和 `parentSession`。
* **[`/session` metadata]**: tree session 状态输出会显示当前 JSONL session 的 cwd，以及 clone/export 产生的 parent session 路径。
* **[`/tree` metadata]**: tree header 会显示 cwd 和 parent，方便直接确认当前 session 来源。
* **[Tests/docs]**: 补充 clone 后 `/session` / `/tree` parent metadata 回归，并同步 README、ARCHITECTURE、QUALITY_SCORE、active plan 与 `next.md`。

### 🧠 Design Intent (Why)

上游 JSONL session header 明确包含 `cwd` 和 `parentSession`。Tau 之前已经写入这些字段，但用户只能打开 JSONL 原文查看。把它们接进 `/session` 和 `/tree` 这两个现有事实源，可以让 `/clone` 之后的新 session 来源可见，同时避免新增一个只有 metadata 的命令面。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
