## [2026-05-24 15:28] | Task: Composition 主屏接管 baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Tui`

**Key Actions:**

* **Tau.Tui**: 新增 `TuiCompositionInteractiveRenderer`，把 `InteractiveInputEditor` 的 prompt/buffer/reverse-search 渲染到 composition overlay，而不是直接写控制台行。
* **Tau.Tui**: 新增 `TuiPassiveTerminal`，在 composition 模式下屏蔽旧 terminal 直写，让 transcript/status 统一由 `InteractiveConsoleSession` + composition binding 驱动。
* **Tau.CodingAgent**: `Program` 在真实交互控制台中创建 `TuiCompositionSession(TuiAnsiRenderSurface.ForConsole())`，并改为使用 composition-backed renderer/terminal。
* **Tau.CodingAgent**: `CodingAgentHost.RunAsync()` 会启动/停止 composition session；host 内部 status/error/shutdown/cancelled 路径统一通过 helper，同步到 composition status bar。

### Design Intent (Why)

这轮不再只是 shadow 同步，而是把 `CodingAgent` 真实交互路径第一次切到 `TuiCompositionSession`：transcript、status 和输入 overlay 开始落到同一块 TUI surface。仍然保留现有 `InteractiveInputEditor` 和 turn input 语义，先做最小可运行替换点，不在同一轮重写 editor/selector/input loop。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.Tui/Runtime/TuiCompositionInteractiveRenderer.cs`
* `src/Tau.Tui/Runtime/TuiPassiveTerminal.cs`
* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
