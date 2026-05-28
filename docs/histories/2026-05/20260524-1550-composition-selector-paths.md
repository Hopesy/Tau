## [2026-05-24 15:50] | Task: Composition selector 路径接线

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Tui`

### Key Actions

* **Tau.Tui**: 新增 `TuiCompositionOverlaySessions`，为 `TuiSelectList` 与 `TuiMultiSelectList` 提供 composition-overlay 驱动的运行 helper。
* **Tau.CodingAgent**: `theme/settings/thinking/auth/scopedModels` 五条 selector 路径在 composition 主屏启用时，不再另开 `TuiAnsiRenderSurface`，而是直接复用当前 `TuiCompositionSession`。
* **Tau.CodingAgent**: `Program` 根据是否启用 composition 主屏，分别选择 console selector 或 composition selector delegate。
* **Tau.CodingAgent / Tau.Tui**: 补了 composition 模式下的基础语义收口，包括 `/clear` 真正清空当前可见 transcript、turn 开始时左侧状态切到 `running`、以及禁用 editor 时不再错误进入 passive terminal 路径。

### Design Intent (Why)

这轮把 composition 主屏从“只有 transcript/status/input overlay”继续推进到“最常用 selector 也走同一块 surface”。这样主题、settings、thinking、auth 和 scoped models 不再各自抢屏，后续只剩更复杂的 `model selector` 与 richer overlay 细节需要继续接入。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.Tui/Runtime/TuiCompositionOverlaySessions.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentThemeSelector.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSettingsSelector.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentThinkingSelector.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentAuthSelector.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentScopedModelsSelector.cs`
* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`
