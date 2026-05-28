## [2026-05-24 11:35] | Task: 收口验证 JSON 与 SSO 脱敏

### Execution Context

* **Agent ID**: `Codex 主控 + 并行 worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续继续，不要停，移植完一轮开始下一轮，持续下去。

### Changes Overview

**Scope:** `Tau.Ai`, `Tau.Mom`, `Tau.Tui`

**Key Actions:**

* **Tau.Ai**: Bedrock SSO RegisterClient 请求异常、HTTP error 和 JSON parse error 诊断复用 SSO cache 已知值脱敏，覆盖 access token / client secret / refresh token。
* **Tau.Mom**: `--validate-sandbox --json` 与 `--validate-slack --json` 共用 validation JSON 写出 helper，保持现有字段和 exit code 语义。
* **Tau.Tui**: overlay 光标语义收口：可见 overlay 存在时隐藏 cursor，最后一个可见 overlay 消失时恢复 cursor；动态可见性变化仍会刷新 overlay focus。

### Design Intent (Why)

本轮继续把运行态边界补实：SSO 错误不泄漏已知 secret，Mom validation 输出避免重复实现，Tui overlay 不再让 cursor 状态依赖外部宿主补救。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.Ai/Providers/Bedrock/BedrockSsoResolver.cs`
* `src/Tau.Mom/Program.cs`
* `src/Tau.Tui/Runtime/TuiTranscriptViewportHost.cs`
