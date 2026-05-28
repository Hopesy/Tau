## [2026-05-24 15:34] | Task: Composition clear regression 修复

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Tui`

**Key Actions:**

* **Tau.CodingAgent**: composition 主屏启用条件收紧到“真正启用交互式 editor”场景，避免 `TAU_CODING_AGENT_DISABLE_INPUT_EDITOR=1` 时仍创建 composition session，结果落到 `TuiPassiveTerminal.PromptAsync()` 的无提示输入回归。
* **Tau.Tui**: `InteractiveConsoleSession` 增加可选 `clearScreenAction`，composition 模式下 `/clear` 不再是 no-op。
* **Tau.CodingAgent**: `Program` 在 composition 模式下为 `InteractiveConsoleSession` 注入清屏重绘动作：`ResetFrame()` + `Render(force: true)`。

### Design Intent (Why)

上一轮真正把 `CodingAgent` 交互路径切到 composition 主屏后，暴露了两个具体回归：一是禁用 editor 时仍会错误进入 passive terminal 路径，二是 `/clear` 在 composition 模式下失效。本轮优先修回兼容语义，而不是继续扩大 TUI 表面面积。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`
