## [2026-06-18 05:02] | Task: close AI shared stream options contract

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 继续完成 AI 和 Agent 迁移到 100%；按当前 `GOAL.md` foundation-first 主线继续推进。

### Changes Overview

**Scope:** `Tau.Ai` shared stream options, provider request hooks, parity docs

**Key Actions:**

* **Public option surface**: `ProviderResponse`, `StreamOptions.OnPayload`, `StreamOptions.OnResponse`, and `SimpleStreamOptions.ThinkingBudgets` now cover the upstream `onPayload`, `onResponse`, and `thinkingBudgets` contract in Tau-native .NET form.
* **Shared helper**: `StreamOptionHelpers` centralizes payload replacement, response callback metadata, `ExtraHigh -> High` reasoning clamp, and custom thinking-budget lookup.
* **Provider adoption**: OpenAI, OpenAI-compatible, OpenAI Responses/Codex/Azure, Anthropic, Google/Gemini CLI/Vertex, Mistral, and Bedrock request paths now use the shared payload/response callback helpers; token-budget providers reuse the shared thinking-budget helper.
* **Docs**: Updated the parity matrix, active plan, `GOAL.md`, `next.md`, and `docs/QUALITY_SCORE.md` so future continuation treats shared stream options as locally closed rather than an open `onPayload` / `thinkingBudgets` gap.

### Design Intent

The upstream option contract is shared infrastructure, so keeping `onPayload`, `onResponse`, and `thinkingBudgets` as scattered provider-local behavior would make future provider parity fragile. The Tau implementation keeps callbacks non-serialized with `[JsonIgnore]`, accepts null-or-dictionary payload replacement for predictable request serialization, and deliberately limits the completion claim to local public/options/helper coverage. Real provider/OAuth e2e, real cloud callback correlation, and full provider cancellation timing remain separate external/runtime gates.

### Files Modified

* `src/Tau.Ai/Abstractions/Options.cs`
* `src/Tau.Ai/Providers/StreamOptionHelpers.cs`
* `src/Tau.Ai/Providers/StreamFunctions.cs`
* `src/Tau.Ai/Providers/OpenAi/OpenAiProvider.cs`
* `src/Tau.Ai/Providers/OpenAiCompat/OpenAiCompatibleProvider.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesProvider.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/OpenAiCodexResponsesProvider.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/AzureOpenAiResponsesProvider.cs`
* `src/Tau.Ai/Providers/Anthropic/AnthropicProvider.cs`
* `src/Tau.Ai/Providers/Google/GoogleProvider.cs`
* `src/Tau.Ai/Providers/Google/GoogleGeminiCliProvider.cs`
* `src/Tau.Ai/Providers/Google/GoogleVertexProvider.cs`
* `src/Tau.Ai/Providers/Mistral/MistralProvider.cs`
* `src/Tau.Ai/Providers/Bedrock/BedrockProvider.cs`
* `src/Tau.Ai/Providers/Bedrock/BedrockMessageConverter.cs`
* `src/Tau.Ai/Serialization/TauAiJsonContext.cs`
* `tests/Tau.Ai.Tests/OpenAiProviderSerializationTests.cs`
* `tests/Tau.Ai.Tests/GoogleGeminiCliProviderTests.cs`
* `tests/Tau.Ai.Tests/GoogleVertexProviderTests.cs`
* `tests/Tau.Ai.Tests/BedrockProviderTests.cs`
* `tests/Tau.Ai.Tests/AiPublicApiCompileSampleTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/QUALITY_SCORE.md`
* `GOAL.md`
* `next.md`

### Validation

* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "OpenAiProviderSerializationTests|GoogleGeminiCliProviderTests|GoogleVertexProviderTests|BedrockProviderTests|AiPublicApiCompileSampleTests" --no-restore --verbosity minimal` passed: 39/39.
* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal` passed: 406/406.
