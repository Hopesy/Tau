## [2026-05-24 11:27] | Task: 推进运行态观测与队列边界

### Execution Context

* **Agent ID**: `Codex 主控 + 并行 worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续继续，不要停，移植完一轮开始下一轮，持续下去。

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.WebUi`, `Tau.Pods`, `Tau.Tui`

**Key Actions:**

* **Tau.CodingAgent**: RPC bash output channel 改为 bounded queue，满队列时丢弃新增 output event 并累计 `droppedOutputEvents`；最终 `bash` result shape 保持兼容。
* **Tau.WebUi**: CodingAgent JSONL import result 顶层返回 `warnings`，调用方不用从 nested audit 里再挖 warning；source-gen 覆盖 warning list。
* **Tau.Pods**: vLLM plan JSON / operation plan 暴露 `usesSnapshotDiscovery`，远端 metadata JSON 也记录是否依赖 snapshot discovery fallback。
* **Tau.Tui**: `TuiCompositionHost` / `TuiCompositionSession` 暴露 `HasVisibleOverlay`，后续主屏可以区分可见 overlay 与 focused input overlay。

### Design Intent (Why)

本轮继续把 runtime seam 向可控、可观测推进：bash streaming 避免无界内存增长，WebUi import warning 更直接，Pods plan 能被脚本判断是否依赖 snapshot discovery，Tui composition 状态更适合后续 CodingAgent 主屏接入。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.WebUi/Contracts/WebChatContracts.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `src/Tau.WebUi/Services/WebUiJsonContext.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.Pods/Models/PodVllmServePlan.cs`
* `src/Tau.Pods/Services/PodVllmCommandPlanner.cs`
* `src/Tau.Tui/Runtime/TuiCompositionHost.cs`
* `src/Tau.Tui/Runtime/TuiCompositionSession.cs`
