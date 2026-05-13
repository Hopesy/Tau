## [2026-05-13 14:45] | Task: coding-agent clone command

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 在完成上一段 `Tau.CodingAgent` tree viewer 提交后继续推进 pi-mono 迁移。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` JSONL session lifecycle

**Key Actions:**

* **新增 `/clone` 命令**：在 command catalog 和 router 中加入 `/clone`，用于把当前 active branch 复制成新的 JSONL session，并立即切换当前 runner/controller 到克隆 session。
* **复用 branch export writer**：在 `CodingAgentTreeSessionStore` 中复用当前 branch 导出路径生成新 session，避免为 clone 另写一套 JSONL 序列化逻辑；空 branch 返回 `Nothing to clone yet`。
* **补齐回归测试**：覆盖 clone 成功、空 session、参数错误和 `/help` 命令列表，`Tau.CodingAgent.Tests` 增至 85 个。
* **同步项目文档**：更新 README、架构文档、质量评分、完整移植 active plan 与 `next.md`，明确 `/clone` 已完成但 interactive tree navigator 仍未完成。

### 🧠 Design Intent (Why)

上游 `/clone` 的语义不是在同一个 session 文件里 fork，而是把当前 leaf 的完整 active branch 复制成一个新的 session 文件。Tau 已经有 `WriteCurrentBranch` / `.jsonl` export 的 ID remap 和 parent session 记录能力，所以直接复用导出路径最简单，也能保证 `/export <path.jsonl>`、`/import` 和 `/clone` 的 JSONL 语义一致。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
