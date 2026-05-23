# 2026-05-22 17:26 | Task: CodingAgent settings selector baseline

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Codex CLI / Windows PowerShell

### User Query

> 继续推进 Tau 的 pi-mono 移植进度，按计划继续落地相邻 TUI selector 切片。

### Changes Overview

**Scope:** `Tau.CodingAgent`、`Tau.Tui` selector 复用层、测试与项目文档。

**Key Actions:**

* **Settings selector helper**: 新增 `CodingAgentSettingsSelector` 和 `CodingAgentSettingsSelectorState`，把当前 settings/runtime 状态转换为 `TuiSelectList` actions，并提供 console selector factory。
* **Command routing**: `/settings` 改为 selector-aware；交互式会话裸 `/settings` 或 `/settings select` 打开 TUI selector，`/settings current` / `/settings path` 保留摘要和路径查询。
* **Settings actions**: selector 当前可写回 auto-compaction、steering mode、follow-up mode、tree filter、thinking level，并可复用 `/theme select` 的 nested theme selector。
* **Host seam**: `CodingAgentHost` 接收 settings selector seam；auto-compaction base options 与 settings override 分离，支持 settings selector / reload 热更新当前 host 状态。
* **Regression coverage**: `Tau.CodingAgent.Tests` 增至 265 个测试，覆盖 selector list state、router 保存/取消/不可用、settings->theme nested selector 与 host 裸 `/settings` selector 接线。
* **Docs sync**: 同步 README、architecture、quality score、next、两份 active execution plan 和 release notes。

### Design Intent

先把 `/settings` 从只读摘要推进到可交互、可验证、可持久化的 selector 闭环，但只覆盖 Tau 已有真实 settings 字段和运行态 seam 的项目。完整上游 SettingsList/submenu、images/terminal/transport/packages、numeric editor 和 scoped-model selector 仍单独后置，避免把尚未建模的配置面伪装成已完成。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentSettingsSelector.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSettingsSelectorTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`

### Verification

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：通过，0 warnings / 0 errors。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，265/265。
* `git diff --check` 完成，仅有既有 CRLF normalization warnings。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过；repo-level test counts 为 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 58、`Tau.Tui.Tests` 75、`Tau.CodingAgent.Tests` 265、`Tau.Pods.Tests` 32。
