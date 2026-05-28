## [2026-05-26 21:43] | Task: WebUi runner runtime log sink

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, dotnet`

### 📥 User Query

> 继续下一轮快速移植，减少低收益单测和文档同步，把 WebUi message runner runtime observability 缺口收进核心基线。

### 🛠 Changes Overview

**Scope:** `Tau.WebUi`, `Tau.WebUi.Tests`, migration status docs.

**Key Actions:**

* **WebUi runner sink 透传**: `WebUiRunnerFactory.Create(...)` 增加可选 `ITauLogSink`，并传给 `RuntimeCodingAgentRunner.Create(..., logSink: ...)`。
* **生产默认路径接线**: `WebChatService(WebChatStore, ITauLogSink?)` 默认 runner factory 闭包复用同一个 sink；生产 DI 已注册 `ITauLogSink`，因此 `/api/sessions/{id}/messages/stream` 默认 runner 路径可写 `agent/run.*` runtime events。
* **Endpoint 级证明**: `WebUiEndpointFixture` 保留 fake runner 模式，并新增默认 runner 启动模式；新增回归用本地 `TAU_MODELS_FILE` custom model 触发无网络的 unregistered API error，断言 stream 返回 error，同时 log sink 收到 `agent/run.start` 和 `agent/run.error`。
* **最小状态同步**: `next.md` 和 `docs/QUALITY_SCORE.md` 记录 WebUi message stream runtime sink 已进入基线，并把 `Tau.WebUi.Tests` discovery 数同步为 42。

### 🧠 Design Intent (Why)

上一轮只证明 WebUi auth status endpoint 会写 `auth/status.checked`。WebUi message streaming 的生产聊天路径仍可能绕过 `RuntimeCodingAgentRunner` 的 sink 参数，导致 `agent/run.*` 和共享 `tool/execution.*` trace 无法进入同一 runtime log。这个切片只打通 WebUi 默认 runner 的 sink 透传，并用不触网的 endpoint 测试固定行为，避免用真实 OpenAI 无 key 网络失败作为不稳定测试条件。

### 📁 Files Modified

* `src/Tau.WebUi/Services/WebUiRunnerFactory.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
