## [2026-05-24 12:05] | Task: JSON 运维输出与 import UI 消费

### Execution Context

* **Agent ID**: `Codex 主控 + 并行 worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续继续，不要停，移植完一轮开始下一轮，持续下去。

### Changes Overview

**Scope:** `Tau.Pods`, `Tau.WebUi`, `Tau.CodingAgent`, `Tau.Tui`

**Key Actions:**

* **Tau.Pods**: 顶层 `logs` / `deployments` 增加 `--json` 输出；`logs` JSON 暴露 deployment、tail、stdout/stderr、remote command 和 exit code；`deployments` JSON 输出部署列表 item。
* **Tau.WebUi**: `.jsonl` 导入会识别 CodingAgent JSONL session header，优先走 conservative import endpoint；成功后使用 `result.summary` 生成导入消息数、源 entry/message 数、warning 数和 branch 范围的状态文案。
* **Tau.CodingAgent**: RPC `get_state` 增加 `bashOutputQueueCapacity`，与 `bash_event started.outputQueueCapacity` 复用同一 bounded queue 常量。
* **Tau.Tui**: `TuiCompositionSession` 增加 `SyncMessagesFrom(InteractiveConsoleSession)`，让旧交互会话 transcript 可直接同步到新 composition/viewport host。

### Design Intent (Why)

本轮继续补机器可读和 UI 可消费的 contract：Pods 顶层运维命令不再只有文本输出，WebUi 能更可靠地区分 WebUi JSONL 与 CodingAgent JSONL，CodingAgent RPC state 与 started event 暴露一致的 bash queue 边界，Tui 为后续主屏接 composition host 保留简单桥接点。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.Pods/Models/PodLifecycleResults.cs`
* `src/Tau.Pods/Services/PodLifecycleService.cs`
* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.Tui/Runtime/TuiCompositionSession.cs`
