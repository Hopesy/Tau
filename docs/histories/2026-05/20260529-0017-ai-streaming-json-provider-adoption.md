# 变更历史：Tau.Ai streaming JSON provider adoption

## 用户诉求

持续执行 Tau 100% pi-mono parity 计划，接续 Phase 2 AI helper / provider contract closure，把上游 `parseStreamingJson(...)` 在 provider streaming tool-call argument path 的行为迁移进 Tau。

## 本次变更

- 对照上游 `packages/ai/src/utils/json-parse.ts` 以及 OpenAI completions、OpenAI Responses shared、Anthropic、Amazon Bedrock、Mistral provider 中的 streaming tool-call argument 调用点。
- 在 `StreamingJsonParser` 中补 internal object raw-text helper，让 provider 侧可以把 best-effort parsed object 以合法 JSON 字符串写回 `ToolCallContent.Arguments`。
- 更新 OpenAI-style / OpenAI Responses shared / Anthropic / Bedrock / Mistral streaming parsers：partial delta 后立即把 accumulated arguments 解析为合法 JSON object 字符串，done/stop 时再以原始 accumulator finalize 一次。
- 保留 `ToolCallDeltaEvent.Delta` 为 provider 原始增量，避免 UI 或下游增量展示丢失真实 provider chunk。
- 扩展 provider tests，覆盖 OpenAI、OpenAI Responses、Anthropic、Mistral 和 Bedrock 在 incomplete tool argument delta 后的 partial arguments，以及最终 done message arguments。
- 同步 `docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、100% active plan、parity matrix 和 `next.md`，把 provider-wide parser adoption 从待办改为 primary provider baseline 已完成。

## 设计意图

上游 TypeScript 的 `ToolCall.arguments` 可以直接存 object；Tau 当前 `ToolCallContent.Arguments` 是 string。为避免把 incomplete raw buffer 暴露成非法 JSON，本切片在 provider partial/done message 中统一写入合法 JSON object 字符串。这样保持 Tau 现有 public shape 不变，同时更接近上游在每个 streaming delta 后通过 `parseStreamingJson(...)` 暴露 best-effort object 的行为。

本切片不声明真实 provider e2e、standalone `pi-ai` bin、完整 TypeBox/AJV 或 exact TypeScript export/subpath shape 完成。

## 验证

- `dotnet build src\Tau.Ai\Tau.Ai.csproj --no-restore --verbosity minimal`，通过，0 warning / 0 error。
- `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "OpenAiProvider_ParsesPartialStreamingToolArguments|AnthropicProvider_ParsesPartialStreamingToolArguments|Stream_ParsesPartialStreamingToolArguments|Stream_TranslatesToolCallEvents|Stream_ConvertsToolsAndParsesToolCallEvents|AiUtilityHelpersTests|AiPublicApiCompileSampleTests" --no-restore --verbosity minimal`，通过 34/34。
- `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal`，通过 273/273。

## 受影响文件

- `src/Tau.Ai/Utilities/StreamingJsonParser.cs`
- `src/Tau.Ai/Providers/OpenAi/OpenAiStreamParser.cs`
- `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesShared.cs`
- `src/Tau.Ai/Providers/Anthropic/AnthropicStreamParser.cs`
- `src/Tau.Ai/Providers/Bedrock/BedrockStreamParser.cs`
- `tests/Tau.Ai.Tests/OpenAiResponsesProviderTests.cs`
- `tests/Tau.Ai.Tests/BedrockProviderTests.cs`
- `tests/Tau.Ai.Tests/ProviderRequestTextSanitizerTests.cs`
- `tests/Tau.Ai.Tests/MistralProviderTests.cs`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `next.md`
