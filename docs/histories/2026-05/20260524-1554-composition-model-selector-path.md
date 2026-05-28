## [2026-05-24 15:54] | Task: Composition model selector 主路径接线

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Tui`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentModelSelector` 增加 composition selector delegate，`/model select`、交互式裸 `/model`、`Ctrl+L` 在 composition 主屏模式下不再新开独立 `TuiAnsiRenderSurface`。
* **Tau.CodingAgent**: `Program` 在 composition 主屏启用时，把 `modelSelector` delegate 切到 composition 路径，和前面已经接入的 `theme/settings/thinking/auth/scopedModels` 保持一致。
* **Tau.CodingAgent / Tau.Tui**: composition 主屏模式的基础语义继续收口：只在真正启用 editor 时启用 composition UI，`/clear` 真正清掉当前可见 transcript，turn 开始时左侧状态切到 `running`。

### Design Intent (Why)

这轮把最后一个高频 selector 入口 `model selector` 也并到 composition 主屏，意味着主题、settings、thinking、auth、scoped models、model selector 这几条最常用交互路径已经开始共用同一块 surface。这样下一轮可以把精力集中到 input overlay 的长文本和 reverse-search 细节，而不是继续做 selector 主路径接线。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentModelSelector.cs`
* `src/Tau.CodingAgent/Program.cs`
