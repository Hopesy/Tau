## [2026-06-18 09:41] | Task: close AI provider cancellation contract

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 继续完成 AI 和 Agent 迁移到 100%；按当前 `GOAL.md` foundation-first 主线继续推进。

### Changes Overview

**Scope:** `Tau.Ai` provider cancellation plumbing, provider tests, parity docs

**Key Actions:**

* **Provider cancellation helper**: `StreamOptionHelpers` now owns the shared aborted assistant contract for provider cancellation, using `Request was aborted` and `StopReason.Aborted`.
* **Provider I/O plumbing**: `StreamOptions.Signal` now flows through OpenAI, OpenAI-compatible, OpenAI Responses/Codex/Azure, Anthropic, Google/Gemini CLI/Vertex, Mistral, Bedrock, Vertex ADC token exchange, SSE/WebSocket/EventStream parsers, and retry-delay paths.
* **Provider terminal behavior**: pre-cancelled tokens short-circuit before payload/auth/http/websocket work, while user-triggered `OperationCanceledException` is reported as an aborted terminal assistant rather than an ordinary provider error.
* **Tests**: Added representative cancellation coverage for OpenAI, OpenAI Responses, Bedrock, and Gemini CLI retry-delay cancellation.
* **Docs**: Updated `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md`, and the active parity plan/matrix to record the local cancellation contract and keep real provider/OAuth e2e open.
* **Docs wording cleanup**: Kept the earlier shared-options `406/406` count as historical slice evidence while making the current cancellation `410/410` gate and the real cloud e2e boundary explicit.

### Design Intent (Why)

Cancellation is part of the public stream contract, not a provider-specific incidental error. Centralizing the aborted assistant shape keeps Faux provider, HTTP/SSE providers, Bedrock eventstream, Codex WebSocket, retry delays, and Vertex ADC token exchange consistent while still separating local cancellation plumbing from real cloud cancellation timing evidence.

### Files Modified

* `src/Tau.Ai/Providers/StreamOptionHelpers.cs`
* `src/Tau.Ai/Providers/OpenAi/OpenAiProvider.cs`
* `src/Tau.Ai/Providers/OpenAiCompat/OpenAiCompatibleProvider.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesProvider.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/OpenAiCodexResponsesProvider.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/AzureOpenAiResponsesProvider.cs`
* `src/Tau.Ai/Providers/Anthropic/AnthropicProvider.cs`
* `src/Tau.Ai/Providers/Google/GoogleProvider.cs`
* `src/Tau.Ai/Providers/Google/GoogleGeminiCliProvider.cs`
* `src/Tau.Ai/Providers/Google/GoogleVertexProvider.cs`
* `src/Tau.Ai/Providers/Google/GoogleVertexAccessTokenResolver.cs`
* `src/Tau.Ai/Providers/Mistral/MistralProvider.cs`
* `src/Tau.Ai/Providers/Bedrock/BedrockProvider.cs`
* `tests/Tau.Ai.Tests/OpenAiProviderSerializationTests.cs`
* `tests/Tau.Ai.Tests/OpenAiResponsesProviderTests.cs`
* `tests/Tau.Ai.Tests/BedrockProviderTests.cs`
* `tests/Tau.Ai.Tests/GoogleGeminiCliProviderTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`

### Validation

* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "OpenAiProviderSerializationTests|OpenAiResponsesProviderTests|BedrockProviderTests|GoogleGeminiCliProviderTests" --no-restore --verbosity minimal -m:1` passed: 44/44.
* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal -m:1` passed: 410/410.
* Stale wording grep for old provider-cancellation-open phrases returned no matches.
* `git diff --check` passed; output only contained existing CRLF normalization warnings.
