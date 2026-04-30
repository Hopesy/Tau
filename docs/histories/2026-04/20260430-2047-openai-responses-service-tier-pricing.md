## [2026-04-30 20:47] | Task: OpenAI Responses service-tier pricing

### 🤖 Execution Context

* **Agent ID**: `codex`
* **Base Model**: `GPT-5.2`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续 pi-mono 到 Tau 的迁移，推进下一块 Tau.Ai provider/API fidelity：OpenAI Responses service-tier cost / pricing multiplier。

### 🛠 Changes Overview

**Scope:** `Tau.Ai` Responses / Codex Responses usage 归一、模型成本计算、provider 单元测试、迁移计划与仓库状态文档。

**Key Actions:**

* **[Usage tier]**: 在 `Usage` 增加可空 `ServiceTier`，让 stream done usage 能携带 effective service tier，不改变既有 token 字段语义。
* **[Responses parser]**: `OpenAiResponsesShared` 从 `response.service_tier` 与 requested option 解析 tier，并在 usage 提取时写入 `Usage.ServiceTier`。
* **[Codex parity]**: Codex Responses SSE/WebSocket parser 复用上游规则：响应 tier 为 `default` 且请求为 `flex` / `priority` 时，按请求 tier 计价。
* **[Cost multiplier]**: `ModelCatalog.CalculateCost` 统一应用 `flex=0.5`、`priority=2` 倍率，并保留显式 service-tier overload 供调用方使用。
* **[Regression tests]**: 补 OpenAI request/response service-tier、Codex default->requested fallback、ModelCatalog multiplier 单测。
* **[Docs sync]**: 更新 `next.md`、architecture、quality score、baseline plan，并归档本切片 execution plan。

### 🧠 Design Intent (Why)

上游 pi-mono 把 Responses service tier 作为 usage cost 的一部分，而 Tau 之前只把 `service_tier` 写进 request body，没有把有效 tier 带进成本计算。把 tier 记录在 `Usage`，成本继续由 `ModelCatalog.CalculateCost` 统一计算，可以保持 provider 只产出事实、catalog 负责价格规则的分层，不把成本计算散进 stream parser。

### 📁 Files Modified

* `src/Tau.Ai/Abstractions/Messages.cs`
* `src/Tau.Ai/Registry/ModelCatalog.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesShared.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesProvider.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/OpenAiCodexResponsesProvider.cs`
* `tests/Tau.Ai.Tests/ModelCatalogTests.cs`
* `tests/Tau.Ai.Tests/OpenAiResponsesProviderTests.cs`
* `tests/Tau.Ai.Tests/OpenAiCodexResponsesProviderTests.cs`
* `docs/exec-plans/completed/2026-04-30-openai-responses-service-tier-pricing.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
