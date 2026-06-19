## [2026-06-18 22:16] | Task: AI Responses provider-specific models.json options

### Execution Context

* **Agent ID**: `Codex CLI`
* **Base Model**: `GPT-5`
* **Runtime**: `PowerShell / .NET 10`

### User Query

> 继续推进 `Tau.Ai` / `Tau.Agent` foundation-first 迁移，把可本地验证的 AI/Agent 基座合同继续收口。

### Changes Overview

**Scope:** `Tau.Ai` runtime config、OpenAI Responses family providers、package consumer smoke、release contract、迁移文档

**Key Actions:**

* **Responses provider-specific config**: `ModelConfigurationStore` 现在从 `models.json options` 读取 Responses 家族第一批 provider-specific 字段：`serviceTier`、`reasoningEffort`、`reasoningSummary`、Codex `textVerbosity`、Azure `azureApiVersion` / `azureResourceName` / `azureBaseUrl` / `azureDeploymentName`。
* **Typed dispatch**: `StreamFunctions` 会在 provider dispatch 前把配置投影为 `OpenAiResponsesOptions`、`OpenAiCodexResponsesOptions` 或 `AzureOpenAiResponsesOptions`；显式代码传入的 typed options 仍优先。
* **StreamSimple preservation**: `StreamSimple` 在存在 Responses provider-specific config 时走 provider `Stream(...)`，避免 provider 内部重建 simple options 时丢失 provider-specific 字段。
* **Provider tests**: 新增/扩展 OpenAI Responses、OpenAI Codex Responses、Azure OpenAI Responses 的本地合同测试，覆盖配置注入、显式 typed options 优先和 provider request body 输出。
* **Consumer smoke**: `scripts/verify-agent-package-consumer.ps1` 的外部 `Tau.Ai` consumer 增加 `ResponsesOptionsCapturingProvider`，断言 `OpenAiResponsesOptions` typed dispatch 与配置字段输出；smoke assertion count 从 47 增至 52。
* **Release contract**: `scripts/verify-release-contracts.ps1` 增加 `configuredResponsesOptions*` 输出断言，确保本地 package consumer 合同进入 release automation gate。
* **Docs sync**: 同步 `GOAL.md`、`next.md`、`README.md`、`docs/QUALITY_SCORE.md` 和 active plans/matrix，记录 Responses 家族 provider-specific options 第一批已本地收口，同时保留真实 e2e 和非 Responses map 缺口。

### Design Intent (Why)

上一轮已把通用 `models.json request options` 收口，但 provider-specific 字段如果仍只能通过代码 typed options 传入，外部 consumer 无法把 OpenAI Responses / Codex / Azure Responses 的常用运行参数版本化在模型配置中。本轮选择 Responses 家族作为第一批，是因为这些字段已经有 Tau provider 本地实现和 stub tests，可用无凭证、无云调用的方式固定 typed dispatch 与 request body 合同。

该切片没有把所有 provider-specific option map 标成完成。Anthropic、Google、Mistral、Bedrock 等非 Responses family 的专用参数，以及真实 OpenAI/Codex/Azure cloud 行为，仍需要后续单独 e2e 或 provider-family 切片验证。

### Files Modified

* `src/Tau.Ai/Registry/ModelConfigurationStore.cs`
* `src/Tau.Ai/Providers/StreamFunctions.cs`
* `tests/Tau.Ai.Tests/ModelConfigurationStoreTests.cs`
* `tests/Tau.Ai.Tests/OpenAiCodexResponsesProviderTests.cs`
* `tests/Tau.Ai.Tests/AzureOpenAiResponsesProviderTests.cs`
* `scripts/verify-agent-package-consumer.ps1`
* `scripts/verify-release-contracts.ps1`
* `GOAL.md`
* `next.md`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`

### Validation

* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "ModelConfigurationStoreTests|OpenAiResponsesProviderTests|OpenAiCodexResponsesProviderTests|AzureOpenAiResponsesProviderTests" --no-restore --verbosity minimal -m:1`: passed 33/33.
* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal -m:1`: passed 429/429.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-package-consumer.ps1 -SkipRestore -Json`: passed, 52 assertions.

### Remaining Boundaries

本轮只关闭 OpenAI Responses family 第一批 provider-specific `models.json options` 的本地合同。非 Responses provider-specific option map、真实 provider/OAuth e2e、真实云端字段验收、真实 NuGet registry/global install、signing/provenance 仍保持 open；不能把该本地合同解释成 `Tau.Ai` / `Tau.Agent` 100% 完成。
