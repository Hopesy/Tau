## [2026-05-21 06:00] | Task: CodingAgent changelog command baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### 📥 User Query

> 继续按 `pi-mono-main` 移植计划推进 `Tau.CodingAgent` parity。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`、相关测试、release notes、README / architecture / quality / next / active plans

**Key Actions:**

* **Changelog store**: 新增 `CodingAgentChangelogStore`，默认从当前目录向上查找 `docs/releases/feature-release-notes.md`，并支持 `TAU_CODING_AGENT_CHANGELOG_FILE` 覆盖来源。
* **Command routing**: 在 command catalog / router / host 注入链路中接入 `/changelog [count|all]`；默认输出最近 20 条，显式数字限制条数，`all` 输出全部条目，非法参数返回 catalog usage。
* **Release notes source**: 在 `docs/releases/feature-release-notes.md` 新增 2026-05 记录，让命令默认数据源有真实 Tau 功能条目。
* **Tests and docs**: 补 router / host targeted tests，更新 `/help` 预期，并同步 README、architecture、quality score、next 和两份 active execution plans。

### 🧠 Design Intent (Why)

上游 `/changelog` 的显式用户价值是让用户在交互式模式内查看变更记录。Tau 当前没有根级 `CHANGELOG.md`，但已有 `docs/releases/feature-release-notes.md` 作为用户可感知发布记录。因此本切片先落 Tau-native CLI baseline，读取仓库内 release notes 表并输出纯文本；不引入启动时 changelog 渲染、`lastChangelogVersion` / `collapseChangelog` 状态或 install/update telemetry，避免把未移植的上游启动体验和网络副作用误写成完成。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentChangelogStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `docs/releases/feature-release-notes.md`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `next.md`

### ✅ Verification

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：通过，0 warning / 0 error。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，215/215。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过，src/tests 项目级 build/test 全部完成；测试计数为 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 54、`Tau.Tui.Tests` 56、`Tau.CodingAgent.Tests` 215、`Tau.Pods.Tests` 32。
* `git diff --check -- src\Tau.CodingAgent tests\Tau.CodingAgent.Tests README.md docs next.md`：退出码 0；仅提示若干已知 CRLF normalization warning，无空白错误。
