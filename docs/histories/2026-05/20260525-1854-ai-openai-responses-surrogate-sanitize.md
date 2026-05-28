## [2026-05-25 18:54] | Task: OpenAI-family surrogate sanitize

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 用户持续要求快速推进 pi-mono -> Tau 移植，降低大段文档和低收益单元测试成本，按下一轮继续迁移。

### 🛠 Changes Overview

**Scope:** `Tau.Ai`

**Key Actions:**

* **[Unicode sanitizer]**: 新增 `UnicodeTextSanitizer.RemoveUnpairedSurrogates(...)`，删除孤立 UTF-16 high/low surrogate，保留合法 surrogate pair（例如 emoji）。
* **[OpenAI Responses request parity]**: `OpenAiResponsesShared` 在 system/developer prompt、user `input_text`、assistant thinking/text fallback 和 tool-result text 进入 request body 前调用 sanitizer。
* **[OpenAI chat-completions parity]**: `OpenAiProvider`、`OpenAiCompatibleProvider` 和 `OpenAiMessageConverter` 在 system/user/assistant/tool-result 文本进入 chat-completions request body 前调用同一 sanitizer。
* **[Focused coverage]**: `OpenAiResponsesSharedTests` 和 `OpenAiProviderSerializationTests` 新增回归，覆盖 system/user/assistant/tool-result 文本中的孤立 surrogate 被删除，合法 emoji 保留。
* **[Minimal docs]**: 只同步 `next.md`、总 active plan 决策和本 history；其它 provider 的 sanitizer sweep 保留为后续切片。

### 🧠 Design Intent (Why)

上游 `sanitizeSurrogates` 是为避免 provider JSON body 因孤立 surrogate 报错。Tau 当前 OpenAI Responses shared converter 与 OpenAI chat-completions converter 都直接把自然语言文本写进 request body，可能把坏 UTF-16 传给 OpenAI Responses / Codex Responses / Azure Responses / OpenAI-compatible 共享路径。本切片先固定 OpenAI-family 两条高收益 converter，不一次性扫所有 provider，避免扩大到 Anthropic / Google / Mistral / Bedrock 多个转换器的冲突和验证面。

### 📁 Files Modified

* `src/Tau.Ai/Security/UnicodeTextSanitizer.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesShared.cs`
* `src/Tau.Ai/Providers/OpenAi/OpenAiMessageConverter.cs`
* `src/Tau.Ai/Providers/OpenAi/OpenAiProvider.cs`
* `src/Tau.Ai/Providers/OpenAiCompat/OpenAiCompatibleProvider.cs`
* `tests/Tau.Ai.Tests/OpenAiResponsesSharedTests.cs`
* `tests/Tau.Ai.Tests/OpenAiProviderSerializationTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

### ✅ Validation

* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --filter FullyQualifiedName~OpenAiResponsesSharedTests --verbosity minimal` -> 4/4 passed
* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --filter FullyQualifiedName~OpenAiProviderSerializationTests --verbosity minimal` -> 5/5 passed
* `dotnet build src\Tau.Ai\Tau.Ai.csproj --no-restore --verbosity minimal` -> passed, 0 warnings, 0 errors
