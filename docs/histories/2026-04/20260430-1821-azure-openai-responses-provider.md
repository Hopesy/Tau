## [2026-04-30 18:21] | Task: Azure OpenAI Responses provider

### 🤖 Execution Context

* **Agent ID**: `codex`
* **Base Model**: `GPT-5.2`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续 pi-mono 到 Tau 的迁移，推进当前 Tau.Ai provider/API fidelity 计划。

### 🛠 Changes Overview

**Scope:** `Tau.Ai` provider 层、provider 单元测试、迁移计划与仓库状态文档。

**Key Actions:**

* **Azure Responses provider**: 新增 `AzureOpenAiResponsesProvider`，把 `azure-openai-responses` 从 OpenAI-compatible chat-completions fallback 切到专用 Responses SSE 路径。
* **Azure config fidelity**: 支持 Azure base URL、resource name、api-version、deployment name map、显式 deployment option 与 `api-key` header。
* **Payload parity**: Azure 请求体使用 `input`、deployment name、`prompt_cache_key` 与 Responses reasoning 结构，不再发送 chat-completions `messages`。
* **Tool-call compatibility**: 将 `azure-openai-responses` 纳入 Responses tool-call id 保真 provider 集合。
* **Regression tests**: 新增 StubHandler 覆盖 provider 注册、URL/query、api-key header、request body、resourceName + deployment map、Simple reasoning 和 missing base URL error。
* **Docs sync**: 更新 `next.md`、architecture、quality score、baseline plan，并完成 Azure plan 归档。

### 🧠 Design Intent (Why)

Azure OpenAI Responses 与 chat completions 的请求语义不同：model 参数实际是 deployment name，消息入口是 `input`，stream 走 Responses SSE。继续复用 `OpenAiCompatibleProvider` 会把 Azure 请求错误地发送为 `messages`，也无法表达 Azure 的 resource/deployment/api-version 配置。本轮按上游 pi-mono 的 Azure provider 语义做窄范围移植，同时保持 Tau 当前 provider 层的约束：`HttpClient` + source-gen JSON + StubHandler 回归，不引入 SDK。

### 📁 Files Modified

* `src/Tau.Ai/Providers/OpenAiResponses/AzureOpenAiResponsesProvider.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesOptions.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesShared.cs`
* `src/Tau.Ai/Providers/BuiltInProviders.cs`
* `tests/Tau.Ai.Tests/AzureOpenAiResponsesProviderTests.cs`
* `docs/exec-plans/completed/2026-04-30-azure-openai-responses-provider.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
