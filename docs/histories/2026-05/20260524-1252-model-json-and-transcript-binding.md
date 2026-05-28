## [2026-05-24 12:52] | Task: Model JSON contract 与 transcript binding

### Execution Context

* **Agent ID**: `Codex 主控 + 并行 worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续继续

### Changes Overview

**Scope:** `Tau.Pods`, `Tau.WebUi`, `Tau.Tui`

**Key Actions:**

* **Tau.Pods**: `model list/pull/remove/status` 增加 `--json` 输出；`model list` 输出 `models[]`，`pull/remove/status` 输出机器可读的 `operation/modelId/present/output` 等字段。
* **Tau.WebUi**: `session-meta` 在重新打开或手动刷新已导入的 CodingAgent JSONL session 时，继续显示 source metadata 摘要；导入时和重新打开后的摘要拼接逻辑收口成同一套 helper。
* **Tau.Tui**: `InteractiveConsoleSession` 增加 `TranscriptChanged` 事件；`TuiCompositionSession` 增加 `BindTranscript(...)`，可持续绑定旧 console transcript 到新 composition/viewport host，而不是只做一次性消息同步。

### Design Intent (Why)

本轮继续补“可被程序消费”和“可持续桥接”的基线。Pods 进一步把运维命令面从文本推进到 JSON contract，WebUi 避免 CodingAgent imported session 在刷新后丢失来源上下文，Tui 则把旧 transcript runtime 和新 composition host 之间的桥从 pull 模式推进到 push 模式。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`
* `src/Tau.Tui/Runtime/TuiCompositionSession.cs`
