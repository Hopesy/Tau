## [2026-04-30 17:41] | Task: Google Gemini CLI Antigravity provider fidelity

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.4 Codex`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续 Tau 从 pi-mono-main 移植，持续推进 Google Gemini CLI / Antigravity provider fidelity 缺口。

### 🛠 Changes Overview

**Scope:** `Tau.Ai` Google Gemini CLI / Antigravity provider

**Key Actions:**

* **[Headers]**: Gemini CLI 请求补 `User-Agent`、`X-Goog-Api-Client`、`Client-Metadata`；Antigravity 请求只保留 Antigravity User-Agent，避免带 Gemini CLI fingerprint headers。
* **[Antigravity]**: baseUrl 为空时按上游顺序 fallback：daily sandbox -> autopush sandbox -> prod；403/404 自动切下一个 endpoint。
* **[Retry]**: 补 429/5xx/transient retry，解析 `Retry-After`、`x-ratelimit-reset`、`x-ratelimit-reset-after` 和 body 中的 retry delay，并受 `StreamOptions.MaxRetryDelay` 约束。
* **[Empty SSE]**: 空 SSE 响应自动重试，避免重复 start/done。
* **[Claude]**: Antigravity Claude thinking model 自动加 `anthropic-beta: interleaved-thinking-2025-05-14`。
* **[Payload]**: Antigravity payload 增加 `requestType: agent`、`userAgent: antigravity` 与 compact system instruction bridge；Gemini 3 reasoning 使用 `thinkingLevel`。
* **[Models]**: 内建 Antigravity model 的 `BaseUrl` 改为空，让 provider 默认 fallback 链真正生效。
* **[Tests]**: 新增 `GoogleGeminiCliProviderTests`，覆盖 Gemini CLI headers/body、Antigravity fallback/payload、Claude thinking beta header、empty SSE retry、server retry delay cap 和 retry delay parser。
* **[Docs]**: 同步 `next.md`、architecture、quality、baseline plan 与本切片 execution plan。

### 🧠 Design Intent (Why)

上游 pi-mono 的 Gemini CLI provider 已把 Cloud Code Assist 与 Antigravity 的差异放在同一个 provider 里：Antigravity 依赖 sandbox endpoint fallback 和特定 payload/header，Gemini CLI 依赖 Cloud Code Assist fingerprint headers，二者都需要 retry/empty-stream 兜底。Tau 原实现只有单 endpoint + 简化 payload，真实可用性不足。本轮只移植影响真实请求成功率的窄范围行为，不把 OAuth login 和 image/tool multimodal routing 混进来。

### 📁 Files Modified

* `src/Tau.Ai/Providers/Google/GoogleGeminiCliProvider.cs`
* `src/Tau.Ai/Registry/BuiltInModels.cs`
* `tests/Tau.Ai.Tests/GoogleGeminiCliProviderTests.cs`
* `next.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `docs/exec-plans/completed/2026-04-30-google-gemini-cli-antigravity-provider.md`
