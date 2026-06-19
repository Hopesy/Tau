## [2026-06-18 20:54] | Task: AI KnownApi alias compatibility

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 继续完成 `Tau.Ai` / `Tau.Agent` foundation-first 迁移。

### 🛠 Changes Overview

**Scope:** `Tau.Ai` provider registry / model configuration / package consumer smoke / docs.

**Key Actions:**

* **API alias adapter**: 新增 `ModelApiNames`，把上游公开 API 名 `openai-completions`、`openai-compatible`、`google-generative-ai` 规范化到 Tau 内部 canonical API 名。
* **Registry lookup closure**: `ProviderRegistry.Register/Get/TryGet/Unregister` 统一消费 alias normalize，避免 alias key 重复暴露在 `RegisteredApis`。
* **models.json compatibility**: `ModelConfigurationStore` 在 custom models、dynamic provider registration 和默认 API fallback 中复用同一个 normalize 入口。
* **External consumer proof**: `verify-agent-package-consumer.ps1` 的外部 `Tau.Ai` package consumer 增加 direct `Model.Api=google-generative-ai` 调度到 canonical `google-generative-language` provider 的断言。
* **Docs sync**: 同步 `GOAL.md`、`next.md`、`README.md`、`docs/QUALITY_SCORE.md` 和 active parity plans，明确该切片只关闭本地 API 名兼容层。

### 🧠 Design Intent (Why)

上游 `pi-ai` 对外暴露的 API 名与 Tau 内部 provider key 已经出现命名差异。如果外部 .NET consumer 或 `models.json` 按上游文档写 `google-generative-ai` / `openai-completions`，Tau 不应因为内部 canonical 名不同而无法解析 provider。这里选择增加窄 alias adapter，而不是重命名内部 provider 协议，目的是保留现有实现稳定性，同时补齐外部配置兼容层。

### 📁 Files Modified

* `src/Tau.Ai/Registry/ModelApiNames.cs`
* `src/Tau.Ai/Registry/ModelConfigurationStore.cs`
* `src/Tau.Ai/Providers/ProviderRegistry.cs`
* `tests/Tau.Ai.Tests/ModelConfigurationStoreTests.cs`
* `tests/Tau.Ai.Tests/ProviderRegistryTests.cs`
* `scripts/verify-agent-package-consumer.ps1`
* `README.md`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
