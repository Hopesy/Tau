## [2026-04-30 20:35] | Task: Codex WebSocket transport

### 🤖 Execution Context

* **Agent ID**: `codex`
* **Base Model**: `GPT-5.2`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续 pi-mono 到 Tau 的迁移，推进 Tau.Ai provider/API fidelity 的下一项。

### 🛠 Changes Overview

**Scope:** `Tau.Ai` Codex Responses provider、共享 Responses parser、provider 单元测试、迁移计划与仓库状态文档。

**Key Actions:**

* **[Transport option]**: 在 `StreamOptions` 增加 `StreamTransport`，默认保持 `Sse`，为 Codex WebSocket/auto transport 提供配置入口。
* **[WebSocket seam]**: 新增 `ICodexWebSocketTransport` / `ICodexWebSocketConnection` 和默认 `ClientWebSocket` 实现，避免引入第三方 WebSocket SDK。
* **[Codex WebSocket]**: `OpenAiCodexResponsesProvider` 支持 `WebSocket` / `Auto`，发送 `response.create` frame，使用 `wss://.../codex/responses`，并复用 Responses JSON event parser。
* **[Session cache]**: 对带 `SessionId` 的 Codex WebSocket 请求缓存空闲 socket；同 session 且 socket open 时复用，busy 时临时创建非缓存连接，错误路径关闭连接。
* **[Auto fallback]**: `Auto` transport 在 WebSocket 未开始前失败时回退原 SSE 路径；显式 `WebSocket` 失败时直接以 stream error 结束。
* **[Codex payload parity]**: Codex system prompt 走 `instructions`，`input` 不再混入 system prompt，默认 `text.verbosity=medium`，`prompt_cache_key` 直接跟随 `SessionId`。
* **[Regression tests]**: 使用 Fake WebSocket 覆盖 URL、headers、request frame、event parsing、auto fallback 与 session reuse。
* **[Docs sync]**: 更新 `next.md`、architecture、quality score、baseline plan，并归档 Codex WebSocket plan。

### 🧠 Design Intent (Why)

上游 pi-mono 的 Codex provider 已经不是 SSE-only：它支持 `transport=websocket` 和 `transport=auto`，并用 `sessionId` 复用 WebSocket 连接。Tau 之前只完成了 Codex SSE provider，无法覆盖这一条高价值运行路径。本轮先把 transport seam、frame 结构、headers 与 cache 生命周期做成可本地验证的最小闭环，同时保留 SSE 默认值，避免改变已有调用行为。

### 📁 Files Modified

* `src/Tau.Ai/Abstractions/Options.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/CodexWebSocketTransport.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/OpenAiCodexResponsesProvider.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesShared.cs`
* `tests/Tau.Ai.Tests/OpenAiCodexResponsesProviderTests.cs`
* `docs/exec-plans/completed/2026-04-30-codex-websocket-transport.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
