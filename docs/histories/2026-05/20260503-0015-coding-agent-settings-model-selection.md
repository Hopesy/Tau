## [2026-05-03 00:15] | Task: Tau.CodingAgent settings and model selection

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.5`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续继续。

### 🛠 Changes Overview

**Scope:** `src/Tau.CodingAgent`, `src/Tau.Tui`, `tests/Tau.CodingAgent.Tests`, `docs/`

**Key Actions:**

* **[Settings Store]**: 新增 `CodingAgentSettingsStore`，默认从 `TAU_CODING_AGENT_SETTINGS_FILE` 或当前目录 `./.tau/coding-agent-settings.json` 读写默认 provider/model。
* **[Startup Selection]**: `Program` 的启动选择优先级收口为 env > session > settings，让显式环境变量优先，同时保留会话恢复和默认模型设置。
* **[Model Switching]**: `ICodingAgentRunner` 暴露 provider/model 列表与 `SelectModel(...)`；`RuntimeCodingAgentRunner` 通过共享 `ModelCatalog` 在运行中切换 `_config.Model`。
* **[Slash Commands]**: `CodingAgentHost` 支持 `/model`、`/provider`、`/models`、`/providers`；这些本地命令不写入 LLM conversation context，成功切换后保存默认模型。
* **[TUI Status]**: `InteractiveConsoleSession` 新增 `WriteStatus(...)`，用于本地命令输出，不复用 assistant 流式输出。
* **[Repo Hygiene]**: 将默认生成的 `.tau/coding-agent-settings.json` 和临时文件加入 `.gitignore`。
* **[Tests]**: 补 settings store round-trip / invalid JSON、host `/model` 命令、runtime model switch 测试；`Tau.CodingAgent.Tests` 当前 15 个测试通过。
* **[Docs]**: 同步 `next.md`、architecture、quality score 与 active execution plan，把本轮决策和剩余边界落仓库。

### 🧠 Design Intent (Why)

* 上一轮 session store 已能保存 provider/model/messages，但用户仍缺少 CLI 内部查看和切换模型的入口。
* 本轮只做 provider/model selection 需要的最小命令面，没有引入上游完整 slash command registry，避免把 auth、compaction、extensions 和 prompt/template expansion 混进同一切片。
* `/model` 等命令是本地控制命令，不应进入 LLM context；这样后续接入更多 slash command 时也能保持 command 与 prompt 的边界清楚。

### 📁 Files Modified

* `.gitignore`
* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSettingsStore.cs`
* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSettingsStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
