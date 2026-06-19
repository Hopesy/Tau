# AI Anthropic provider-specific models.json options

## 用户诉求

继续推进 `Tau.Ai` / `Tau.Agent` foundation-first 迁移，优先把可被其它 .NET 项目引用的 AI/Agent 基座能力收口到可验证状态。

## 主要变更

- 对照上游 `packages/ai/src/providers/anthropic.ts` 的 `AnthropicOptions`，在 `Tau.Ai.Providers.Anthropic` 新增 public `AnthropicOptions` 与 `AnthropicToolChoice`。
- `AnthropicOptions` 覆盖本轮本地合同字段：`ThinkingEnabled`、`ThinkingBudgetTokens`、adaptive `Effort`、`ThinkingDisplay`、`InterleavedThinking` 和 `ToolChoice`。
- `AnthropicToolChoice` 支持字符串型 `"auto" / "any" / "none"`，也支持 `AnthropicToolChoice.Tool(name)` 输出上游 `{ type: "tool", name }` 形状；`ToString()` 对 tool choice 输出 `tool:<name>`，方便外部 consumer smoke 固定可读合同。
- `AnthropicProvider` 现在会把 typed options 映射到 request body：budget/adaptive/disabled `thinking`、adaptive `output_config.effort`、`tool_choice`、`metadata.user_id`，并在 non-adaptive 模型且 `InterleavedThinking=true` 时写 `anthropic-beta: interleaved-thinking-2025-05-14`。
- `ModelConfigurationStore` 的 provider-specific `models.json options` 解析扩展为 Anthropic 字段，并兼容 Anthropic `{ "type": "tool", "name": "..." }` 和 Mistral `{ "type": "function", "function": { "name": "..." } }` 两种 `toolChoice` 对象。
- `StreamFunctions` 现在按 `anthropic-messages` 把配置投影到 typed `AnthropicOptions`；显式 `AnthropicOptions` 继续优先于 `models.json`，且 `StreamSimple` 因 provider-specific config 改走 typed `Stream(...)` 时仍保留显式 `SimpleStreamOptions.Reasoning` 派生的 thinking intent。
- `Tau.Ai.Tests` 增加 Anthropic request body、adaptive thinking、disabled thinking、simple thinking budget、models.json typed dispatch、explicit precedence 和 public API compile sample 覆盖。
- `verify-agent-package-consumer.ps1` 的外部 `Tau.Ai` package consumer 新增 Anthropic provider-specific config 捕获，输出 `configuredAnthropicOptions*`；`verify-release-contracts.ps1` 增加对应 release contract 断言。
- 同步 `README.md`、`GOAL.md`、`next.md`、`docs/QUALITY_SCORE.md` 和 active parity plan/matrix，标明 Anthropic provider-specific options 本地合同已完成，同时保留真实 Anthropic 云端/OAuth e2e 缺口。

## 设计意图

上一批 provider-specific options 已关闭 Responses 家族和 Mistral 的本地 `models.json -> typed options -> provider request` 链路。Anthropic 仍只有 `SimpleStreamOptions.Reasoning` 的预算 thinking 路径，外部 consumer 无法按上游配置表达 `thinkingEnabled`、`thinkingDisplay`、adaptive effort、interleaved thinking 或 tool choice。

本轮选择 typed record，而不是把 Anthropic 字段塞进通用 `StreamOptions`。这样可以维持通用 options 的小边界，同时让外部 .NET consumer 可显式构造 `AnthropicOptions`，`models.json` 也能投影到同一类型，便于测试和评审。

该切片只关闭本地 request/config/package consumer contract。真实 Anthropic API 字段验收、Claude OAuth/browser refresh e2e、真实云端 callback/cancellation timing 和其它 provider-specific option map 仍是后续缺口。

## 验证

- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "AnthropicProviderTests|ModelConfigurationStoreTests|AiPublicApiCompileSampleTests" --no-restore --verbosity minimal -m:1`：24/24 passed。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-package-consumer.ps1 -SkipRestore -Json`：`succeeded=true`，69 assertions，包含 `configuredAnthropicOptionsType=AnthropicOptions`、`configuredAnthropicOptionsThinkingEnabled=True`、`configuredAnthropicOptionsThinkingBudgetTokens=2345`、`configuredAnthropicOptionsEffort=high`、`configuredAnthropicOptionsThinkingDisplay=omitted`、`configuredAnthropicOptionsInterleavedThinking=True`、`configuredAnthropicOptionsToolChoice=tool:read_file`、`configuredAnthropicOptionsMaxTokens=654`。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1 -Json`：`succeeded=true`，`agentPackageConsumer.assertionCount=69`。
- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal -m:1`：441/441 passed。

## 受影响文件

- `src/Tau.Ai/Providers/Anthropic/AnthropicProvider.cs`
- `src/Tau.Ai/Providers/StreamFunctions.cs`
- `src/Tau.Ai/Registry/ModelConfigurationStore.cs`
- `tests/Tau.Ai.Tests/AnthropicProviderTests.cs`
- `tests/Tau.Ai.Tests/ModelConfigurationStoreTests.cs`
- `tests/Tau.Ai.Tests/AiPublicApiCompileSampleTests.cs`
- `scripts/verify-agent-package-consumer.ps1`
- `scripts/verify-release-contracts.ps1`
- `README.md`
- `GOAL.md`
- `next.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
