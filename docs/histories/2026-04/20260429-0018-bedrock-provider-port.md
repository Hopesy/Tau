## [2026-04-29 00:18] | Task: Bedrock provider port

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.4 Codex`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续 Tau 从 pi-mono-main 移植，按 AGENTS/docs 流程持续推进下一步。

### 🛠 Changes Overview

**Scope:** `Tau.Ai` provider coverage

**Key Actions:**

* **Bedrock provider**: 将 `bedrock-converse-stream` 从占位错误替换为 `HttpClient` 调用 Bedrock Runtime ConverseStream 的专用 provider。
* **认证路径**: 支持 `AWS_BEARER_TOKEN_BEDROCK` / explicit bearer token，以及 explicit/env `AWS_ACCESS_KEY_ID` + `AWS_SECRET_ACCESS_KEY` + optional session token 的 SigV4 签名。
* **协议转换**: 新增 Bedrock request payload converter，覆盖 system/messages/toolConfig/reasoning/requestMetadata，以及 user/assistant/toolResult 的内容块转换。
* **流解析**: 新增最小 AWS binary event stream parser 与 Bedrock event translator，映射 text/thinking/tool/usage/stop reason 到 Tau stream event。
* **测试**: 新增 `BedrockProviderTests`，覆盖 provider registry、bearer、SigV4、missing credentials、tool call streaming 和 `StreamSimple` reasoning payload。
* **文档同步**: 更新 `next.md`、architecture、quality、baseline plan，并为本切片落独立 execution plan。

### 🧠 Design Intent (Why)

Bedrock 是当前 Tau.Ai provider fidelity 的 P0 缺口。为了保持 Tau.Ai 零外部依赖和 AOT/source-gen 方向，本轮没有引入 AWS SDK，而是用 `HttpClient + SigV4 + eventstream parser` 建立可测试的最小真实调用路径。shared profile/SSO 和真实 AWS e2e 留给后续 provider e2e 切片。

### 📁 Files Modified

* `src/Tau.Ai/Providers/Bedrock/BedrockProvider.cs`
* `src/Tau.Ai/Providers/Bedrock/BedrockMessageConverter.cs`
* `src/Tau.Ai/Providers/Bedrock/BedrockSigV4Signer.cs`
* `src/Tau.Ai/Providers/Bedrock/BedrockEventStreamParser.cs`
* `src/Tau.Ai/Providers/Bedrock/BedrockStreamParser.cs`
* `tests/Tau.Ai.Tests/BedrockProviderTests.cs`
* `next.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `docs/exec-plans/active/2026-04-28-bedrock-provider-port.md`
