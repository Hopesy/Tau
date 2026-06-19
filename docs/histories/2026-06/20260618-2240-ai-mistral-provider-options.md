# AI Mistral provider-specific models.json options

## 用户诉求

继续推进 `Tau.Ai` / `Tau.Agent` foundation-first 迁移，按当前 `GOAL.md` 和 parity matrix 领取下一条可验证切片，不停止在只读分析。

## 主要变更

- 对照上游 `packages/ai/src/providers/mistral.ts` 的 `MistralOptions`，在 `ModelConfigurationStore` 的 provider-specific `models.json options` 解析中新增 Mistral 字符串字段：`toolChoice`、`promptMode`、`reasoningEffort`。
- 在 `StreamFunctions` 中把 `mistral-conversations` 接入 provider-specific typed dispatch：`StreamSimple` 会在配置存在时投影到 `MistralOptions` 并走 typed `Stream(...)`，显式 `MistralOptions` 仍优先于配置。
- 新增 Mistral request body、models.json typed dispatch 和显式 typed options precedence 测试。
- 扩展 `verify-agent-package-consumer.ps1` 的外部 `Tau.Ai` consumer，使用独立 `consumer-mistral-provider` 捕获 `MistralOptions`，并在 `verify-release-contracts.ps1` 中固定对应输出断言。
- 同步 `GOAL.md`、`next.md`、`README.md`、`docs/QUALITY_SCORE.md`、active plan 和 parity matrix。

## 设计意图

上一刀已经关闭 OpenAI Responses 家族 provider-specific options。本次选择 Mistral 作为第一条非 Responses provider-specific option map，是因为 Tau 已有专用 `MistralProvider` 和 `MistralOptions`，上游字段面较小，能用本地 request body + 外部 package consumer smoke 做完整闭环，不需要把 Bedrock/AWS credential 链或更大 provider 配置面混进同一刀。

本切片只支持字符串型 `toolChoice`，对应 Tau 当前 `MistralOptions.ToolChoice` 类型；上游对象型 `{ type: "function", function: { name } }` 仍保持 open，后续需要单独扩展 typed options 和序列化合同。

## 验证

- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "ModelConfigurationStoreTests|MistralProviderTests" --no-restore --verbosity minimal -m:1`：20/20 passed。
- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal -m:1`：432/432 passed。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-package-consumer.ps1 -SkipRestore -Json`：`succeeded=true`，57 assertions，包含 `configuredMistralOptionsType=MistralOptions`、`configuredMistralOptionsToolChoice=required`、`configuredMistralOptionsPromptMode=reasoning`、`configuredMistralOptionsReasoningEffort=high`、`configuredMistralOptionsMaxTokens=321`。

## 剩余边界

- 未关闭真实 Mistral 云端字段验收或任何真实 provider/OAuth e2e。
- 未实现上游对象型 function `toolChoice`。
- 未关闭 Bedrock、Anthropic、Google 等其它 provider-specific option map。
