## [2026-05-24 11:13] | Task: 继续收口运行态输出

### Execution Context

* **Agent ID**: `Codex 主控 + 并行 worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续继续，不要停，移植完一轮开始下一轮，持续下去。

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.WebUi`, `Tau.Mom`, `Tau.Pods`

**Key Actions:**

* **Tau.CodingAgent**: RPC bash streaming output 改为局部 channel drain，progress 回调只入队，避免子进程 stdout/stderr reader 同步阻塞在 RPC stdout writer 上；final `bash_event` / `bash` response 会等已入队 output drain 完再写。
* **Tau.WebUi**: WebUi-local JSONL export session header 会带出 `SourceMetadata`，让 CodingAgent JSONL conservative import 的来源 tree/audit 事实随导出文件保留；普通 import 仍剥离客户端提交的 source metadata。
* **Tau.Mom**: `--validate-slack --json` 输出机器可读 JSON，包含 `succeeded/message/slackSocketModeEnabled/botUserId/socketHost/error`；无 `--json` 时保持原文本日志行为。
* **Tau.Pods**: vLLM snapshot discovery fallback 在远端启动日志中输出最终 `resolved_model_path`，方便定位实际传给 vLLM 的模型路径。

### Design Intent (Why)

本轮继续把上一轮已经落地的 seam 往可用 baseline 推进：bash streaming 不阻塞 pipe drain，WebUi 导出不丢来源审计事实，Slack preflight 可被脚本消费，Pods 远端启动日志能看到 snapshot 解析结果。仍不声明完整 Slack/vLLM/CodingAgent RPC/WebUi branch parity。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.WebUi/Services/WebChatJsonlExporter.cs`
* `src/Tau.Mom/MomCommandLine.cs`
* `src/Tau.Mom/Program.cs`
* `src/Tau.Mom/SlackSocketModeTransport.cs`
* `src/Tau.Pods/Services/PodVllmCommandPlanner.cs`
