## [2026-05-13 15:54] | Task: coding-agent html export metadata

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 继续推进 Tau 的 pi-mono 迁移。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` HTML transcript export metadata

**Key Actions:**

* **[HTML metadata]**: `CodingAgentHtmlSessionExporter` 在存在 tree session summary 时显示 JSONL header 里的 cwd。
* **[Parent session visibility]**: clone/export 产生的 `parentSession` 现在也会进入 HTML transcript 的 meta grid。
* **[Tests/docs]**: 补充 tree session HTML export metadata 回归，并同步 README、ARCHITECTURE、QUALITY_SCORE、active plan 与 `next.md`。

### 🧠 Design Intent (Why)

上一片已经把 JSONL header 的 `cwd` / `parentSession` 暴露到 `/session` 和 `/tree`。HTML transcript export 是同一条 session 事实链的离线视图；继续复用 `CodingAgentTreeSessionSummary` 显示 metadata，可以避免用户打开 HTML 后仍然需要手动翻内嵌 JSONL 才能确认当前 session 的工作目录和分叉来源。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
