## [2026-05-26 13:37] | Task: Non-OpenAI provider surrogate sanitize

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 用户要求继续快速推进 pi-mono -> Tau 移植，降低低收益文档和单测消耗，多 Agent 并行，并把时间优先投到真实 parity 缺口和核心 baseline。

### Changes Overview

**Scope:** `Tau.Ai`

**Key Actions:**

* **[Anthropic]**: `AnthropicProvider` / `AnthropicMessageConverter` 在 system、user text、assistant text、thinking 和 tool result 文本进入 request body 前复用 `UnicodeTextSanitizer.RemoveUnpairedSurrogates(...)`。
* **[Google]**: `GoogleProvider`、`GoogleVertexProvider`、`GoogleGeminiCliProvider` 和 `GoogleMessageConverter` 在 system/user/assistant/tool-result 文本进入 request body 前清理孤立 surrogate。
* **[Mistral]**: `MistralProvider` 在 system/user/assistant/thinking/tool-result 文本进入 conversations request body 前清理孤立 surrogate。
* **[Bedrock]**: `BedrockMessageConverter` 在 system/user/assistant/thinking/tool-result 文本进入 ConverseStream payload 前清理孤立 surrogate。
* **[Focused coverage]**: 新增 `ProviderRequestTextSanitizerTests`，用 stub HTTP handler 捕获各 provider request body，固定“孤立 UTF-16 surrogate 被删除、合法 emoji surrogate pair 保留”的协议边界。
* **[Minimal docs]**: 只同步 `next.md` 和本 history；不扩写架构文档或质量长表。

### Design Intent (Why)

上一轮已经把 OpenAI-family converter 的 request text sanitize 固定下来，但 `next.md` 仍明确标出 Anthropic、Google、Mistral、Bedrock 等 provider 需要按各自 converter 单独接入。该切片选择直接复用既有 sanitizer，不新增抽象、不改变 provider payload 结构，避免把高价值兼容性修复扩大成 provider 重构。

### Files Modified

* `src/Tau.Ai/Providers/Anthropic/AnthropicMessageConverter.cs`
* `src/Tau.Ai/Providers/Anthropic/AnthropicProvider.cs`
* `src/Tau.Ai/Providers/Google/GoogleMessageConverter.cs`
* `src/Tau.Ai/Providers/Google/GoogleProvider.cs`
* `src/Tau.Ai/Providers/Google/GoogleVertexProvider.cs`
* `src/Tau.Ai/Providers/Google/GoogleGeminiCliProvider.cs`
* `src/Tau.Ai/Providers/Mistral/MistralProvider.cs`
* `src/Tau.Ai/Providers/Bedrock/BedrockMessageConverter.cs`
* `tests/Tau.Ai.Tests/ProviderRequestTextSanitizerTests.cs`
* `next.md`

### Validation

* `dotnet build tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal` -> passed, 0 warnings, 0 errors
* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --no-build --verbosity minimal --filter FullyQualifiedName~ProviderRequestTextSanitizerTests` -> 6/6 passed
