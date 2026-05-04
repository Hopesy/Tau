## [2026-05-03 20:45] | Task: coding-agent import command

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.4`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续 Tau.CodingAgent 的 pi-mono 移植工作，推进下一块本地 session 命令能力。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **Implemented `/import <path>`**: 读取 Tau snapshot JSON，并恢复当前 runner 的 messages、provider/model 和 display name。
* **Added strict session load coverage**: 为 `CodingAgentSessionStore.LoadStrict()` 补缺失文件和坏 JSON 的显式失败测试。
* **Extended runner seam**: 在 `ICodingAgentRunner` 增加 `RestoreSession(...)`，让 import 命令不直接触碰 runtime 内部状态。
* **Updated command metadata**: `/help`、usage、架构文档、质量评分、execution plan 和 next 清单同步纳入 `/import`。

### 🧠 Design Intent (Why)

`/export` 已经固定了 Tau 当前单文件 snapshot 格式，下一步最小闭环是能把这个 snapshot 严格导回当前平面 session。导入路径使用 `LoadStrict()`，坏文件和缺失文件必须显式报错，避免把损坏备份静默恢复为空会话。当前仍不伪装成上游完整 JSONL session tree/import/share 体系。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSessionStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
