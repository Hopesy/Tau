## [2026-05-24 11:03] | Task: 收口并行审查反馈

### Execution Context

* **Agent ID**: `Codex 主控 + 并行 worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续移植。

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.WebUi`, `Tau.Mom`, `Tau.Pods`

**Key Actions:**

* **Tau.CodingAgent**: RPC `bash` 正常返回 `Cancelled=true` 时输出 `bash_event cancelled`，不再输出 `completed`；最终 `bash` response 保持兼容。
* **Tau.WebUi**: 通用 WebChat import 默认剥离客户端提交的 `SourceMetadata`；只有 CodingAgent JSONL 专用 import 保留 source tree/audit metadata。
* **Tau.Mom**: Slack preflight 捕获非外部取消的 `TaskCanceledException` 并返回结构化失败；preflight 成功/失败消息都会说明 `SlackSocketModeEnabled` 当前状态。
* **Tau.Pods**: vLLM plan 在缺少 resolved snapshot path 时，公开 `ServeCommand`、systemd unit 和远端执行使用同一 snapshot discovery serve command，避免用户复制裸 cache-root serve command。

### Design Intent (Why)

本轮不扩展新功能面，而是把上一批并行审查暴露的可收口风险直接并行修掉：RPC event 语义、WebUi metadata 信任边界、Slack preflight 失败体验和 vLLM plan 输出一致性。继续保持文档降速，只写 history，不更新大段 README/ARCHITECTURE/QUALITY。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `src/Tau.Mom/SlackSocketModeTransport.cs`
* `src/Tau.Pods/Services/PodVllmCommandPlanner.cs`
