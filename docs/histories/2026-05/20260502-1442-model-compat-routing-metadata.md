## [2026-05-02 14:42] | Task: Model compatibility and routing metadata

### Execution Context

* **Agent ID**: `codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### User Query

> 继续 Tau 从 pi-mono 的迁移，推进 generated model catalog 后的下一块模型元数据。

### Changes Overview

**Scope:** `Tau.Ai` model abstraction, OpenAI-compatible request body behavior, generated model generator, tests, migration plan, quality docs, and next backlog.

**Key Actions:**

* **[Model metadata]**: Added `Model.Compat`, `ModelCompatibility`, and `VercelGatewayRouting` so Tau can carry OpenAI-compatible behavior flags and routing preferences with a model.
* **[OpenAI-compatible behavior]**: `OpenAiProvider` now consumes explicit compat metadata for stream usage, `store`, system vs developer role, max token field, reasoning format/map, z.ai tool streaming, strict tool schema, OpenRouter routing, and Vercel AI Gateway routing.
* **[Message conversion]**: `OpenAiMessageConverter` now respects model image capability, can flatten thinking blocks to text when requested, and can include `strict: false` only when compat says the endpoint supports it.
* **[Generator]**: `generate-tau-ai-models.ps1` now preserves seed `compat` objects when generating `GeneratedBuiltInModels.g.cs`.
* **[Tests]**: Added request-body regressions for z.ai-style compatibility, OpenRouter routing, Vercel Gateway routing, and `CreateOpenAiCompatibleModel` metadata preservation.
* **[Docs sync]**: Updated `next.md`, architecture, quality score, and the active baseline execution plan.

### Design Intent

The upstream model registry has a broad compatibility schema, but Tau should not claim behavior that no provider consumes yet. This slice therefore added the subset Tau can actually honor now: OpenAI-compatible request shaping and provider routing. Remaining custom model configuration and unsupported provider-specific schema stay as follow-up work.

### Validation

* `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/generate-tau-ai-models.ps1`
* `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
* `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`
* Project-level sequential validation is run after this history entry.

### Files Modified

* `src/Tau.Ai/Abstractions/Models.cs`
* `src/Tau.Ai/Providers/OpenAi/OpenAiProvider.cs`
* `src/Tau.Ai/Providers/OpenAi/OpenAiMessageConverter.cs`
* `src/Tau.Ai/Registry/ModelCatalog.cs`
* `src/Tau.Ai/Serialization/TauAiJsonContext.cs`
* `scripts/generate-tau-ai-models.ps1`
* `tests/Tau.Ai.Tests/ModelCatalogTests.cs`
* `tests/Tau.Ai.Tests/OpenAiProviderSerializationTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
