## [2026-06-19 22:56] | Task: AI OpenAI provider options

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### User Query

> 继续 Tau.Ai / Tau.Agent foundation-first 迁移，优先完善可被其它项目引用的底座能力。

### Changes Overview

**Scope:** `Tau.Ai` provider runtime config and local package-consumer contract.

**Key Actions:**

* Added `OpenAiOptions` and `OpenAiToolChoice` for OpenAI chat-completions provider-specific request options.
* Projected `models.json options` for `openai-chat-completions` into typed `OpenAiOptions`, covering string/function-object `toolChoice` and `reasoningEffort`.
* Preserved explicit code `OpenAiOptions` over configured `models.json` values.
* Serialized OpenAI `tool_choice` and compatibility-mapped reasoning effort in the OpenAI request body path.
* Extended the external package consumer smoke and release contract assertions to capture OpenAI typed options from a temp app that only references Tau packages.
* Updated `README.md`, `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md`, and active parity plans/matrix with the local contract evidence and remaining external-e2e boundaries.

### Design Intent

This continues the foundation-first lane without claiming full provider parity. The goal is to make locally packaged `Tau.Ai` / `Tau.Agent` consumers able to use the same runtime config contract that in-repo callers use, while keeping real OpenAI/OpenAI-compatible cloud validation, OAuth/provider e2e, registry publishing, signing, and provenance as separate unfinished gates.

### Validation

* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "OpenAiProviderSerializationTests|ModelConfigurationStoreTests|AiPublicApiCompileSampleTests" --no-restore --verbosity minimal` passed 33/33.
* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal` passed 450/450.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-package-consumer.ps1 -SkipRestore -Json` passed 91 assertions.

### Files Modified

* `src/Tau.Ai/Providers/OpenAi/OpenAiProvider.cs`
* `src/Tau.Ai/Providers/StreamFunctions.cs`
* `tests/Tau.Ai.Tests/OpenAiProviderSerializationTests.cs`
* `tests/Tau.Ai.Tests/ModelConfigurationStoreTests.cs`
* `tests/Tau.Ai.Tests/AiPublicApiCompileSampleTests.cs`
* `scripts/verify-agent-package-consumer.ps1`
* `scripts/verify-release-contracts.ps1`
* `README.md`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
