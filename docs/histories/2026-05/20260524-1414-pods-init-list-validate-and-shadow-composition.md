## [2026-05-24 14:14] | Task: Pods 剩余 JSON contract 与 shadow composition 接线

### Execution Context

* **Agent ID**: `Codex 主控 + 并行 worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.Pods`, `Tau.CodingAgent`, `Tau.Tui`

**Key Actions:**

* **Tau.Pods**: `init/list/validate` 增加 `--json` 输出；`init` 输出生成路径和 summary，`list` 输出 pod 列表，`validate` 输出 success 与 errors。
* **Tau.Pods**: 顶层 `exec/deploy/stop/restart` 也已接上 `--json`，并补精确 flag consume helper，避免 `exec` 把远端命令自身的 `--json` 误吞。
* **Tau.CodingAgent**: `Program` 现在会创建一个使用 `TuiNullRenderSurface` 的 shadow `TuiCompositionSession` 并传给 `CodingAgentHost`。
* **Tau.Tui**: 新增 `TuiNullRenderSurface`，让 composition host 能在不改变现有终端输出的前提下，持续同步 transcript state。

### Design Intent (Why)

本轮把 `Tau.Pods` CLI 的剩余纯文本入口继续推进到机器可读 contract，基本收齐整个命令面的 JSON 输出；同时把 `CodingAgent/Tui` 的 composition transcript bridge 真正挂到运行路径里，但仍保持当前 console 输出语义不变，用 shadow session 先把状态同步链跑通。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.Pods/Models/PodLifecycleResults.cs`
* `src/Tau.Pods/Services/PodLifecycleService.cs`
* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.Tui/Rendering/TuiNullRenderSurface.cs`
