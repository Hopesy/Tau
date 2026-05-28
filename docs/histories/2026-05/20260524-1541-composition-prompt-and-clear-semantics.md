## [2026-05-24 15:41] | Task: Composition prompt/clear 语义收口

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Tui`

**Key Actions:**

* **Tau.Tui**: 新增 `TuiCompositionInteractiveRenderer`，把 `InteractiveInputEditor` 的 prompt、buffer 和 reverse-search 渲染到 composition overlay，而不是直接写控制台行。
* **Tau.Tui**: 新增 `TuiPassiveTerminal`，让 composition 主屏路径下 transcript/status 不再经过旧 terminal 直写。
* **Tau.CodingAgent**: `Program` 在真实交互控制台中启用 `TuiCompositionSession(TuiAnsiRenderSurface.ForConsole())`，并切到 composition-backed renderer/terminal。
* **Tau.CodingAgent**: composition 启用条件收紧到“真正启用交互式 editor”的场景，避免 `TAU_CODING_AGENT_DISABLE_INPUT_EDITOR=1` 时错误进入 passive terminal 路径。
* **Tau.Tui**: `InteractiveConsoleSession` 增加 composition 模式专用 `clearScreenAction` 和可见 transcript 起点，`/clear` 不再只是重绘后把旧 transcript 立即画回来。
* **Tau.CodingAgent**: host 在 turn 开始时会把 composition 左侧状态切到 `running`，status/error/shutdown/cancelled helper 继续统一同步到 shadow/main composition status bar。

### Design Intent (Why)

这轮真正把 `CodingAgent` 的输入渲染链切到 composition 主屏，并优先修掉切换后暴露的两个行为回归：一是禁用 editor 时不该进入 passive terminal 路径，二是 `/clear` 在 composition 模式下必须真正清掉当前可见 transcript，而不是立即被旧内容 redraw 回来。这样下一轮可以开始做 input overlay 的细节，而不是反复修基础语义。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.Tui/Runtime/TuiCompositionInteractiveRenderer.cs`
* `src/Tau.Tui/Runtime/TuiPassiveTerminal.cs`
* `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`
* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
