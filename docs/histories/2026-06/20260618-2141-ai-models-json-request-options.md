## [2026-06-18 21:41] | Task: AI models.json request options

### Execution Context

* **Agent ID**: `Codex CLI`
* **Base Model**: `GPT-5`
* **Runtime**: `PowerShell / .NET 10`

### User Query

> 继续完成 `Tau.Ai` / `Tau.Agent` foundation-first 迁移，优先把 Agent 基座做到可被其它 .NET 项目可靠引用。

### Changes Overview

**Scope:** `Tau.Ai`、release/package consumer smoke、迁移文档

**Key Actions:**

* **models.json request options**: `ModelConfigurationStore` 现在解析 provider-level、`modelOverrides.<model>.options` 和 `models[].options`，并按 provider -> modelOverride -> model 合并通用 request options。
* **StreamFunctions merge**: `StreamFunctions` 会把配置 options 注入 `StreamOptions` / `SimpleStreamOptions`，同时保持调用方显式代码参数为最高优先级。
* **Explicit enum tracking**: `StreamOptions.Transport` / `CacheRetention` 增加内部显式设置跟踪，避免非 nullable enum 默认值吞掉配置，也允许显式 `CacheRetention.None` 覆盖配置里的 `long`。
* **Consumer smoke**: `scripts/verify-agent-package-consumer.ps1` 的外部 `Tau.Ai` package consumer 增加 `OptionsCapturingProvider`，断言配置注入后的 `temperature`、`maxTokens`、`topP`、`transport`、`cacheRetention`、`sessionId`、`maxRetryDelayMs`、`reasoning`、`thinkingBudgets` 和 `metadata`。
* **Release contract**: `scripts/verify-release-contracts.ps1` 增加 `configuredOptions*` 输出断言，确保新增配置合同进入 release gate。
* **Docs sync**: 同步 `GOAL.md`、`next.md`、`README.md`、`docs/QUALITY_SCORE.md` 和 active parity plan/matrix，记录通用 request options 已本地收口，同时保留 provider-specific option map 与真实 provider/OAuth e2e 缺口。

### Design Intent (Why)

此前外部 consumer 可以通过代码传 `StreamOptions`，但 `models.json` 只能覆盖动态 provider、auth/header 和少量 model metadata。上游 `StreamOptions` / `SimpleStreamOptions` 的通用字段属于 runtime config UX 的核心部分，外部应用需要能把这些默认值版本化在 provider/model 配置中，而不是每次调用都写代码。实现限定为可序列化且跨 provider 通用的 request defaults，避免把未验证的 provider-specific 云端语义伪装成完成。

### Files Modified

* `src/Tau.Ai/Abstractions/Options.cs`
* `src/Tau.Ai/Registry/ModelConfigurationStore.cs`
* `src/Tau.Ai/Providers/StreamFunctions.cs`
* `tests/Tau.Ai.Tests/ModelConfigurationStoreTests.cs`
* `scripts/verify-agent-package-consumer.ps1`
* `scripts/verify-release-contracts.ps1`
* `GOAL.md`
* `next.md`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`

### Validation

* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "ModelConfigurationStoreTests|AiPublicApiCompileSampleTests" --no-restore --verbosity minimal -m:1`：通过 11/11。
* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal -m:1`：通过 424/424。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-package-consumer.ps1 -SkipRestore -Json`：通过，47 assertions；外部 `Tau.Ai` consumer 输出 `configuredOptionsTemperature=0.3`、`configuredOptionsTransport=WebSocket`、`configuredOptionsCacheRetention=Long`、`configuredOptionsReasoning=High`、`configuredOptionsThinkingHigh=400`、`configuredOptionsMetadataShared=model` 等新增证据。

### Remaining Boundaries

本轮关闭的是通用 request options 的本地 `models.json` 配置合同。provider-specific option map、真实 provider/OAuth e2e、真实云端 callback/cancellation timing correlation、真实 registry/global install、signing/provenance 仍保持 open；不能把本地配置 smoke 解释成完整 runtime config UX 或全量 `Tau.Ai` / `Tau.Agent` 100% 终局完成。
