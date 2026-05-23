# 2026-05-22 18:10 | Task: CodingAgent scoped models selector baseline

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Codex CLI / Windows PowerShell

### User Query

> 继续推进 Tau 的 pi-mono 移植进度，按计划继续落地相邻 TUI selector 切片。

### Changes Overview

**Scope:** `Tau.CodingAgent`、`Tau.Tui` selector 复用层、测试与项目文档。

**Key Actions:**

* **Multi-select foundation**: 新增 `TuiMultiSelectList` 与 `TuiMultiSelectSession`，固定 null=all enabled、有序显式选择、filter、toggle、provider toggle、enable all、clear、reorder、save/cancel 语义。
* **Scoped models selector**: 新增 `CodingAgentScopedModelsSelector`，把当前 available models 和 settings `enabledModels` 转换为可交互多选 UI，并提供 console selector factory。
* **Command routing**: `/scoped-models` 支持 `current|select|set|add|remove|clear|all`；交互式会话的裸 `/scoped-models` 或 `/scoped-models select` 打开 TUI selector，无 selector 会话继续保留摘要/明确不可用。
* **Settings nesting**: `/settings` selector 增加 scoped models action，可从 settings selector 进入同一多选 selector 并写回 settings。
* **Host seam**: `CodingAgentHost` 和生产入口接入 scoped models selector seam，只有真实交互式 editor 存在时启用 ANSI selector。
* **Regression coverage**: `Tau.Tui.Tests` 增至 78 个测试，覆盖 multi-select save/cancel、filter、provider toggle 和 reorder；`Tau.CodingAgent.Tests` 增至 270 个测试，覆盖 selector state、persistence/cancel/unavailable、settings nested selector 和 host 裸 `/scoped-models` 接线。
* **Docs sync**: 同步 README、architecture、quality score、next、两份 active execution plan 和 release notes。

### Design Intent

先把 scoped model scope 的真实交互式编辑闭环接上现有 settings `enabledModels` 合同，而不是直接实现完整上游 Ctrl+P model cycling。这样可以复用 Tau.Tui 的多选 foundation，先固定选择、过滤、provider 批量切换、全量启用、清空、重排、保存和取消的可测试语义；运行中快速模型循环继续留给独立切片。

### Files Modified

* `src/Tau.Tui/Components/TuiMultiSelectList.cs`
* `src/Tau.Tui/Runtime/TuiMultiSelectSession.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentScopedModelsSelector.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSettingsSelector.cs`
* `tests/Tau.Tui.Tests/TuiComponentTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentScopedModelsSelectorTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSettingsSelectorTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`

### Verification

* `dotnet build src\Tau.Tui\Tau.Tui.csproj --no-restore --verbosity minimal`：通过，0 warnings / 0 errors。
* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：通过，0 warnings / 0 errors。
* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal`：通过，78/78。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，270/270。
* `git diff --check` 完成，仅有既有 CRLF normalization warnings。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过；repo-level test counts 为 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 58、`Tau.Tui.Tests` 78、`Tau.CodingAgent.Tests` 270、`Tau.Pods.Tests` 32。
