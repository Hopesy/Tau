## [2026-05-24 11:20] | Task: 推进验证输出与 TUI composition

### Execution Context

* **Agent ID**: `Codex 主控 + 并行 worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续继续，不要停，移植完一轮开始下一轮，持续下去。

### Changes Overview

**Scope:** `Tau.Ai`, `Tau.Tui`, `Tau.WebUi`, `Tau.Mom`

**Key Actions:**

* **Tau.Ai**: Bedrock SSO OIDC `CreateToken` / `RegisterClient` HTTP 调用遇到外部 cancellation 时重新抛出，避免被包装成普通 refresh/register failure。
* **Tau.Tui**: 新增 `TuiCompositionSession`，作为 `TuiCompositionHost` 的轻量 session facade，暴露 start/stop/render、message/status mutation、overlay 和 input 入口。
* **Tau.WebUi**: `CodingAgentJsonlImportResultDto` 顶层返回同一份 `sourceMetadata`，WebUi-local JSONL export header 也会保留 session source metadata。
* **Tau.Mom**: `--validate-sandbox --json` 输出结构化 JSON；`--validate-slack --json` 已输出 Slack preflight JSON，二者失败时仍设置 exit code 1。

### Design Intent (Why)

这一轮继续把已落地的 seam 变成更适合脚本、UI 和后续宿主复用的 baseline：验证命令可机器读取，TUI composition 可作为 session 使用，WebUi import 来源事实不用调用方二次反查，Bedrock SSO 取消语义不被误报成业务失败。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.Ai/Providers/Bedrock/BedrockSsoResolver.cs`
* `src/Tau.Tui/Runtime/TuiCompositionSession.cs`
* `src/Tau.WebUi/Contracts/WebChatContracts.cs`
* `src/Tau.WebUi/Services/WebChatJsonlExporter.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `src/Tau.Mom/MomCommandLine.cs`
* `src/Tau.Mom/Program.cs`
* `src/Tau.Mom/SlackSocketModeTransport.cs`
