# AI Mistral function toolChoice models.json options

## 用户诉求

继续推进 `Tau.Ai` / `Tau.Agent` foundation-first 迁移，按当前 `GOAL.md` 和 active parity plan 补齐可验证的 AI/Agent 基座缺口。

## 主要变更

- 对照上游 `packages/ai/src/providers/mistral.ts` 的 `MistralOptions.toolChoice` 联合类型，在 `Tau.Ai` 中新增 `MistralToolChoice`，保留字符串型 `toolChoice` 赋值，并支持 `MistralToolChoice.Function(name)` 表达对象型 function choice。
- `MistralProvider` 现在会把 function choice 序列化为 request body 中的 `{ "type": "function", "function": { "name": "..." } }`，字符串型 choice 仍按原字符串输出。
- `ModelConfigurationStore` 的 provider-specific `models.json options` 解析扩展为同时支持 `"toolChoice": "required"` 和 `"toolChoice": { "type": "function", "function": { "name": "read_file" } }`。
- `StreamFunctions` 会把配置层 `ModelToolChoiceConfiguration` 投影到 typed `MistralOptions.ToolChoice`；显式传入的 `MistralOptions` 继续优先于 `models.json` 配置。
- `Tau.Ai.Tests` 增加 Mistral request body function `tool_choice`、models.json function `toolChoice` typed dispatch，以及显式 typed options precedence 覆盖。
- `verify-agent-package-consumer.ps1` 的外部 `Tau.Ai` package consumer 改用 Mistral function object 配置，并输出 `configuredMistralOptionsToolChoice=function:consumer_tool`、`Kind=function`、`Function=consumer_tool`；`verify-release-contracts.ps1` 增加对应 release contract 断言。
- 同步 `README.md`、`GOAL.md`、`next.md`、`docs/QUALITY_SCORE.md` 和 active parity plan/matrix，移除“对象型 function toolChoice 未实现”的本地合同缺口，同时保留真实 Mistral 云端验收和其它 provider-specific map 缺口。

## 设计意图

上一刀只关闭了 Mistral 字符串型 provider-specific options；但上游 `toolChoice` 允许 function object，外部项目如果按上游配置写 `models.json` 仍会丢失 intent。本轮用一个小的值类型承载 string/function 两种形状，避免把 `MistralOptions.ToolChoice` 暴露成 untyped `object`，也避免破坏已有 `ToolChoice = "required"` 的 .NET 调用方式。

该切片只处理 Mistral 本地 request/config/package consumer contract。真实 Mistral 云端字段验收、其它 provider-specific option map、真实 provider/OAuth e2e 和最终 registry/signing/provenance 不在本轮关闭范围内。

## 验证

- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "ModelConfigurationStoreTests|MistralProviderTests" --no-restore --verbosity minimal -m:1`：22/22 passed。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-package-consumer.ps1 -SkipRestore -Json`：`succeeded=true`，59 assertions，包含 `configuredMistralOptionsToolChoice=function:consumer_tool`、`configuredMistralOptionsToolChoiceKind=function`、`configuredMistralOptionsToolChoiceFunction=consumer_tool`。
- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal -m:1`：434/434 passed。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1 -Json`：`succeeded=true`，`agentPackageConsumer.succeeded=true`，59 assertions，并命中 Mistral function `toolChoice` release contract 输出。
- `git diff --check`：通过；只有 CRLF/LF normalization warning，无 whitespace error。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过；Ai 434、Agent 127、Tui 251、CodingAgent 631、WebUi 72、Pods 216。

## 受影响文件

- `src/Tau.Ai/Providers/Mistral/MistralProvider.cs`
- `src/Tau.Ai/Registry/ModelConfigurationStore.cs`
- `src/Tau.Ai/Providers/StreamFunctions.cs`
- `tests/Tau.Ai.Tests/MistralProviderTests.cs`
- `tests/Tau.Ai.Tests/ModelConfigurationStoreTests.cs`
- `scripts/verify-agent-package-consumer.ps1`
- `scripts/verify-release-contracts.ps1`
- `README.md`
- `GOAL.md`
- `next.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
