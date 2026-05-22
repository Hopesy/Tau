## [2026-05-21 08:00] | Task: CodingAgent settings command baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### 📥 User Query

> 继续按 `pi-mono-main` 移植计划推进 `Tau.CodingAgent` parity。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`、相关测试、README / architecture / quality / next / release notes / active plans

**Key Actions:**

* **Command catalog**: 新增 `/settings [current|path]`，让 `/help` 与 usage 错误共用同一 command catalog。
* **Command routing**: 在 `CodingAgentCommandRouter` 中新增只读 settings handler；`/settings` 与 `/settings current` 输出当前 settings 文件路径和有效配置摘要，`/settings path` 只输出路径。
* **Settings summary**: 摘要包含当前 runner provider/model、当前 thinking、settings 默认 provider/model、tree filter、retry policy、default thinking 和 enabledModels scope。
* **Read-only boundary**: 命令只读取 `CodingAgentSettingsStore` 与 runner 当前状态，不保存 settings、不切换模型、不调用 runner。
* **Tests and docs**: 补 router/host targeted tests，更新 `/help` 预期，并同步 README、architecture、quality score、next、release notes 和两份 active execution plans。

### 🧠 Design Intent (Why)

上游 `/settings` 是可编辑 TUI selector；Tau 当前还没有完整 selector/edit 层，但已有 settings store 承载默认模型、tree filter、retry、default thinking 和 enabledModels scope。这个切片先固定只读 CLI summary，让用户能在会话内确认当前 settings 文件与有效配置，不需要直接打开 JSON 文件。

完整 settings selector UI 后续仍应在 TUI/selector 层实现；本切片不把只读 inspect 命令写成完整上游 parity。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`
* `next.md`

### ✅ Verification

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：通过，0 warning / 0 error。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，221/221。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过，src/tests 项目级 build/test 全部完成；测试计数为 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 54、`Tau.Tui.Tests` 56、`Tau.CodingAgent.Tests` 221、`Tau.Pods.Tests` 32。
* `git diff --check -- src\Tau.CodingAgent tests\Tau.CodingAgent.Tests README.md docs next.md`：待最终验证。
