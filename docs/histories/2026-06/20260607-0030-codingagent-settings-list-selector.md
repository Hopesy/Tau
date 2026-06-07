## [2026-06-07 00:30] | Task: CodingAgent settings list selector

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows / PowerShell`

### 📥 User Query

> 继续执行 Tau 100% pi-mono parity 目标。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Tui`, parity docs/history

**Key Actions:**

* **CodingAgent settings surface**: 将 `/settings select` 的生产 selector 从旧的 7 项 `TuiSelectList` 主菜单切到 `TuiSettingsList` 主设置面，返回 `setting-id=value` payload。
* **Settings writeback**: Router 先解析 settings-list value payload，再写回 auto-compaction、terminal show images / clear on shrink、image auto-resize / block images、show hardware cursor、editor padding、autocomplete max visible、steering/follow-up mode、tree filter、thinking level、quiet startup、collapse changelog、install telemetry；scoped models 和 theme 继续打开既有子 selector。
* **Tui session wrapper**: 新增 `TuiSettingsListSession` 和 composition overlay runner，固定 settings-list change/cancel result；保留旧 action-id selector payload 兼容路径。
* **Tests and docs**: 补充 CodingAgent selector/router tests、TUI settings-list session tests，并同步 `ARCHITECTURE.md`、`QUALITY_SCORE.md`、active parity plans 和 `next.md`。

### 🧠 Design Intent (Why)

上游 CodingAgent 的 interactive settings selector 是 `SettingsList` 主设置面，而 Tau 之前的 `/settings select` 只暴露少量动作入口。这个切片先把主设置面和 `onChange(id, newValue)` 语义接到真实产品路径，扩大可编辑 settings 字段，同时避免把 package/transport/shell path、完整 settings submenus、terminal/image/editor runtime wiring 或完整 TUI focus stack 误声明为完成。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentSettingsSelector.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.Tui/Runtime/TuiSettingsListSession.cs`
* `src/Tau.Tui/Runtime/TuiCompositionOverlaySessions.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSettingsSelectorTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.Tui.Tests/TuiComponentTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
