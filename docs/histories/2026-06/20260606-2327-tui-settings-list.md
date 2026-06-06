## [2026-06-06 23:27] | Task: Tui settings-list component parity

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### User Query

> 继续执行 GOAL.md 的 Tau 100% pi-mono parity 计划，推进当前 Tui component 切片。

### Changes Overview

**Scope:** `Tau.Tui`

**Key Actions:**

* **Settings-list baseline**: 新增 `TuiSettingsList`、`TuiSettingItem`、`TuiSettingsListTheme` 和 `TuiSettingsListOptions`，对照上游 `packages/tui/src/components/settings-list.ts` 固定库层 settings-list 行为。
* **Interactive behavior**: 支持 label/value 对齐、description wrapping、可选搜索、Enter/Space value cycle、submenu delegate / done callback、Esc/Ctrl-C cancel、滚动提示和宽度截断。
* **Targeted coverage**: 新增 `TuiSettingsListTests`，覆盖渲染对齐、搜索过滤、value cycle、submenu 回调和 cancel 行为。
* **Docs sync**: 同步 `docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、parity matrix 和 active plan，明确本切片关闭的是 `components/settings-list.ts` 库层 foundation，不代表完整 CodingAgent settings 产品面已经完成。

### Design Intent (Why)

上游 `components/settings-list.ts` 是 TUI 组件矩阵里仍标为 partial 的明确库层缺口。这里选择直接补 Tau.Tui 内的通用组件，而不是重写 `Tau.CodingAgent` 当前 `/settings` selector，避免把库层 parity 和产品级全量 settings workflow 混成一个不可评审的大切片。

### Validation

* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal`
  * Passed: 249/249
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  * Passed: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 115, `Tau.Tui.Tests` 249, `Tau.CodingAgent.Tests` 435, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 166

### Files Modified

* `src/Tau.Tui/Components/TuiSettingsList.cs`
* `tests/Tau.Tui.Tests/TuiSettingsListTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
