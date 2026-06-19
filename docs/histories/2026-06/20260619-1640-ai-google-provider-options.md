# AI Google provider-specific models.json options

## 用户诉求

继续推进 `Tau.Ai` / `Tau.Agent` foundation-first 迁移，优先把可被其它 .NET 项目引用的 AI/Agent 基座能力收口到可验证状态。

## 主要变更

- 对照上游 `packages/ai/src/providers/google.ts`、`google-vertex.ts` 和 `google-gemini-cli.ts`，在 Google provider surface 新增/公开 `GoogleThinkingOptions`、`GoogleOptions`、`GoogleVertexOptions.ToolChoice/Thinking` 和 `GoogleGeminiCliOptions`。
- `GoogleOptions` 覆盖本轮本地合同字段：`ToolChoice`、`Thinking.Enabled`、`Thinking.BudgetTokens` 和通用 `MaxTokens`。
- `GoogleVertexOptions` 继续覆盖 Vertex `Project` / `Location` / credential 相关字段，并新增 `ToolChoice` / `Thinking` typed surface，使 `models.json` 可直接驱动 Vertex request body。
- `GoogleGeminiCliOptions` 新增 `ToolChoice`、`Thinking` 与 `ProjectId`，允许配置覆盖 OAuth credential 中的 project id。
- `ModelConfigurationStore` 的 provider-specific `models.json options` 解析扩展为 Google 系字段：`toolChoice`、`thinkingEnabled`、`thinkingBudgetTokens`、`thinkingLevel`、`project`、`location` 和 `projectId`。
- `StreamFunctions` 现在按 `google-generative-language`、`google-vertex` 和 `google-gemini-cli` 把配置投影到 typed provider options；显式 typed options 继续优先于 `models.json`。
- `GoogleProvider` / `GoogleVertexProvider` / `GoogleGeminiCliProvider` 会把 tool choice 映射到 `toolConfig.functionCallingConfig.mode`，把 explicit thinking 写入 `generationConfig.thinkingConfig`，并保持既有 shared thinking-budget fallback。
- `Tau.Ai.Tests` 增加 Google / Vertex / Gemini CLI request body、models.json typed dispatch 和 public API compile sample 覆盖。
- `verify-agent-package-consumer.ps1` 的外部 `Tau.Ai` package consumer 新增 Google provider-specific config 捕获，输出 `configuredGoogleOptions*`。
- 同步 `README.md`、`GOAL.md`、`next.md`、`docs/QUALITY_SCORE.md` 和 active parity plan/matrix，标明 Google 系 provider-specific options 本地合同已完成，同时保留真实 Google/Vertex/Gemini CLI/Antigravity 云端/OAuth e2e 缺口。

## 设计意图

前几轮已关闭通用 `models.json request options`、Responses 家族、Mistral 和 Anthropic 的本地 `models.json -> typed options -> provider request` 链路。Google 系 provider 仍缺 provider-specific typed config 合同，外部 consumer 无法通过 `models.json` 表达 Google function calling mode、explicit thinking、Vertex project/location 或 Gemini CLI project override。

本轮选择继续使用 provider-specific typed record，而不是把 Google 字段塞进通用 `StreamOptions`。这样保持通用 request options 边界较小，同时让外部 .NET consumer 可以显式构造 `GoogleOptions` / `GoogleVertexOptions` / `GoogleGeminiCliOptions`，`models.json` 也能投影到同一类型，便于测试和 package consumer smoke 固定。

该切片只关闭本地 request/config/package consumer contract。真实 Google API、Vertex、Gemini CLI、Antigravity 云端字段验收、OAuth/browser refresh e2e、真实 callback/cancellation timing 和其它 provider-specific option map 仍是后续缺口。

## 验证

- `dotnet build src\Tau.Ai\Tau.Ai.csproj --no-restore --verbosity minimal`：0 warning / 0 error。
- `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "FullyQualifiedName~ModelConfigurationStoreTests|FullyQualifiedName~GoogleGeminiCliProviderTests|FullyQualifiedName~GoogleVertexProviderTests|FullyQualifiedName~AiPublicApiCompileSampleTests" --no-restore --verbosity minimal`：39/39 passed。
- `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal`：444/444 passed。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-package-consumer.ps1 -SkipRestore -Json`：`succeeded=true`，74 assertions，包含 `configuredGoogleOptionsType=GoogleOptions`、`configuredGoogleOptionsToolChoice=any`、`configuredGoogleOptionsThinkingEnabled=True`、`configuredGoogleOptionsThinkingBudgetTokens=8765`、`configuredGoogleOptionsMaxTokens=765`。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1 -Json`：`succeeded=true`，包含 `agentPackageConsumer.assertionCount=74`、`aiProviderOauthMatrix.succeeded=true` 和 `aiAgentExportShape.succeeded=true`。

## 受影响文件

- `src/Tau.Ai/Providers/Google/GoogleProvider.cs`
- `src/Tau.Ai/Providers/Google/GoogleVertexProvider.cs`
- `src/Tau.Ai/Providers/Google/GoogleGeminiCliProvider.cs`
- `src/Tau.Ai/Providers/StreamFunctions.cs`
- `src/Tau.Ai/Registry/ModelConfigurationStore.cs`
- `tests/Tau.Ai.Tests/GoogleGeminiCliProviderTests.cs`
- `tests/Tau.Ai.Tests/GoogleVertexProviderTests.cs`
- `tests/Tau.Ai.Tests/ModelConfigurationStoreTests.cs`
- `tests/Tau.Ai.Tests/AiPublicApiCompileSampleTests.cs`
- `scripts/verify-agent-package-consumer.ps1`
- `README.md`
- `GOAL.md`
- `next.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
