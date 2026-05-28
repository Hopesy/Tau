## [2026-05-24 11:53] | Task: Status/health metadata 与 TUI transcript bridge

### Execution Context

* **Agent ID**: `Codex 主控 + 并行 worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续继续，不要停，移植完一轮开始下一轮，持续下去。

### Changes Overview

**Scope:** `Tau.Pods`, `Tau.CodingAgent`, `Tau.Mom`, `Tau.Tui`

**Key Actions:**

* **Tau.Pods**: `vllm status --json` 与 `vllm health --json` 在 JSON 模式下 best-effort 读取远端 `~/.tau_pods/<deployment>.json`，输出 `metadata` / `metadataJson`；文本输出不触发额外读取。
* **Tau.CodingAgent**: RPC `bash_event started` 暴露 `outputQueueCapacity`，让客户端明确 bash output streaming 的 bounded queue 上限。
* **Tau.Mom**: `--validate-sandbox --json` 改用 `MomSandboxValidationJsonResult` typed record，保留既有 `succeeded/message/sandbox/error` 字段和 exit code 语义。
* **Tau.Tui**: `InteractiveConsoleSession` 增加 `SnapshotMessages()`，可把旧 transcript 与运行中 streaming buffer 映射为 `TuiMessage`；`TuiMessageRole` 增加 `Thinking`，为后续主屏迁到 composition/viewport 提供状态桥。

### Design Intent (Why)

本轮继续补真实可观察 contract：Pods JSON status/health 能带回部署 metadata，CodingAgent RPC 客户端能看见 bash stream 背压边界，Mom validation 输出从匿名结构收口到 typed DTO，Tui 先建立旧交互会话到新 transcript viewport 的最小桥接，而不是一次性重写主屏。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.Pods/Models/PodVllmResults.cs`
* `src/Tau.Pods/Services/PodVllmOrchestrationService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.Mom/MomSandboxValidator.cs`
* `src/Tau.Mom/Program.cs`
* `src/Tau.Tui/Components/TuiMessageArea.cs`
* `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`
