## [2026-05-02 23:57] | Task: Tau.CodingAgent session persistence

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.5`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 提交 git 后制定下一步计划并完成。

### 🛠 Changes Overview

**Scope:** `src/Tau.CodingAgent`, `tests/Tau.CodingAgent.Tests`, `docs/`

**Key Actions:**

* **[Session Store]**: 新增 `CodingAgentSessionStore`，支持从 `TAU_CODING_AGENT_SESSION_FILE` 或当前目录 `./.tau/coding-agent-session.json` 读写本地 JSON session。
* **[Runtime Rehydrate]**: `Program` 启动时加载保存的 provider/model/messages，并通过 `RuntimeCodingAgentRunner.Create(provider, model, history)` 恢复上下文。
* **[Turn Persist]**: `CodingAgentHost` 在每个输入回合结束后保存 runner 当前 messages/model；保存失败时只写运行时错误，不中断 CLI 主循环。
* **[Tests]**: 补 `CodingAgentSessionStore` round-trip / invalid JSON 测试，以及 host 回合后持久化测试；`Tau.CodingAgent.Tests` 当前 10 个测试通过。
* **[Repo Hygiene]**: 将默认生成的 `.tau/coding-agent-session.json` 和临时文件加入 `.gitignore`，避免运行 CLI 后污染工作区。
* **[Docs]**: 同步 `next.md`、architecture、quality score 与 active execution plan，把本轮决策和剩余边界落仓库。

### 🧠 Design Intent (Why)

* 上游 `pi-mono` 的 session manager 包含 JSONL tree、branch、compaction、label、extension entry 等完整能力；Tau 当前阶段最缺的是跨运行恢复 conversation 的基础层。
* 本轮选择 Tau-native 的最小 JSON snapshot，而不是一次性移植上游完整 session tree，是为了先固定可验证的消息持久化边界，并为后续 settings、slash command、compaction 与 WebUi/Mom 会话复用打基础。
* Store 只序列化 Tau 当前真实抽象：`UserMessage`、`AssistantMessage`、`ToolResultMessage` 与 `TextContent`、`ThinkingContent`、`ImageContent`、`ToolCallContent`，未知 role/content 安全跳过。

### 📁 Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSessionStore.cs`
* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSessionStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `.gitignore`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
