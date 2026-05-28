## [2026-05-24 11:45] | Task: Import 摘要与 plan metadata 收口

### Execution Context

* **Agent ID**: `Codex 主控 + 并行 worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续继续，不要停，移植完一轮开始下一轮，持续下去。

### Changes Overview

**Scope:** `Tau.WebUi`, `Tau.Pods`, `Tau.CodingAgent`

**Key Actions:**

* **Tau.WebUi**: CodingAgent JSONL conservative import result 增加 `summary`，汇总导入消息数、源 entry/message 数、warning 数和 current-branch-only 标记。
* **Tau.Pods**: `vllm plan --json` / `vllm deploy --json` 的 plan metadata 写出时补齐并覆盖 `usesSnapshotDiscovery`，避免顶层 plan 字段与 metadata 字段漂移。
* **Tau.CodingAgent**: RPC `get_state` 增加运行中 bash output queue 的 `bashDroppedOutputEvents`，便于客户端在 bash 仍运行时观察 stream 丢弃边界。

### Design Intent (Why)

本轮只补用户可见的 contract 边界：WebUi import 结果更容易被 UI/调用方消费，Pods JSON plan metadata 与真实执行策略一致，CodingAgent RPC 暴露正在运行的 bash stream 背压状态。避免扩写大文档和低收益测试，把时间继续投到移植 parity 缺口。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.WebUi/Contracts/WebChatContracts.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `src/Tau.WebUi/Services/WebUiJsonContext.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
