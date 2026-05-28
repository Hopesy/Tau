## [2026-05-24 14:35] | Task: Mom validation contract 与 composition status 同步

### Execution Context

* **Agent ID**: `Codex 主控 + 并行 worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.Mom`, `Tau.CodingAgent`, `Tau.Tui`

**Key Actions:**

* **Tau.Mom**: 新增统一的 validation contracts 文件，把 sandbox/slack validate 的 typed result 与 JSON DTO 集中到同一位置；`--validate-slack --json` 现在显式映射到 `MomSlackValidationJsonResult`。
* **Tau.CodingAgent**: `CodingAgentHost` 的 shadow composition session 不再只同步 transcript，还会同步当前 session/model/status/error/shutdown/cancelled 状态。
* **Tau.CodingAgent**: `CodingAgentHost` 内部分散的 `_ui.WriteStatus/_ui.WriteRuntimeError/_ui.WriteShutdown/_ui.WriteCancelled` 收口到 helper，避免 composition status 漏同步。
* **Tau.Tui**: 继续复用 `TuiNullRenderSurface` + `TuiCompositionSession` 作为不改变现有终端输出的 shadow host。

### Design Intent (Why)

本轮继续沿“低风险、可接线”的方向推进。Mom 把 validation JSON surface 从 transport 内嵌 record 收口到统一 contract；CodingAgent/Tui 则把 shadow composition 的同步范围从 transcript 扩到宿主状态，让后续真正切主屏时不必再回头补 status/session/model 事实。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.Mom/MomValidationContracts.cs`
* `src/Tau.Mom/Program.cs`
* `src/Tau.Mom/MomSandboxValidator.cs`
* `src/Tau.Mom/SlackSocketModeTransport.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
