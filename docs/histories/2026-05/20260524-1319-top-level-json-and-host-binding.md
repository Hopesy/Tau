## [2026-05-24 13:19] | Task: 顶层 JSON contract 与 host binding seam

### Execution Context

* **Agent ID**: `Codex 主控 + 并行 worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.Pods`, `Tau.Tui`, `Tau.CodingAgent`, `Tau.WebUi`

**Key Actions:**

* **Tau.Pods**: 顶层 `status/probe/health` 增加 `--json` 输出，`status` 输出配置级 pod 摘要，`probe/health` 输出结果数组并保留文本分支兼容。
* **Tau.WebUi**: imported CodingAgent session 的 `sourceMetadata` 摘要在重新打开和手动刷新后仍会显示，导入时与重新打开后的摘要文案复用同一套 helper。
* **Tau.Tui**: `InteractiveConsoleSession` 增加 `TranscriptChanged` 事件，支持 transcript 改变时主动推送。
* **Tau.CodingAgent**: `CodingAgentHost` 增加可选 `TuiCompositionSession` 绑定 seam，外部若传入 composition session，会自动把旧 transcript 绑定过去。

### Design Intent (Why)

本轮继续做“可程序消费”和“可接线”的基线。Pods 顶层命令不再只剩文本输出，WebUi imported session 不会在刷新后丢来源信息，Tui/CodingAgent 则把旧 console transcript 到新 composition host 的桥从单次同步推进到宿主级绑定。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`
* `src/Tau.Tui/Runtime/TuiCompositionSession.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
