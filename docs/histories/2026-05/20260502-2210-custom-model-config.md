## [2026-05-02 22:10] | Task: Custom model/provider config entry

### Execution Context

* **Agent ID**: `codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### User Query

> 继续 Tau 从 pi-mono 的迁移，推进自定义 provider / custom model 配置入口。

### Changes Overview

**Scope:** `Tau.Ai` model registry, custom model configuration, model catalog tests, migration plan, quality docs, and next backlog.

**Key Actions:**

* **[Configuration store]**: Added `ModelConfigurationStore` to load the first existing models config from `TAU_MODELS_FILE`, `./.tau/models.json`, or `~/.tau/models.json`.
* **[Model catalog merge]**: `ModelCatalog` now merges built-in/generated catalogs with models.json provider overrides, per-model overrides, and custom models.
* **[Upstream-shaped subset]**: Implemented the Tau-supported subset of upstream `models.json`: `providers`, provider `baseUrl/api/headers/compat/models/modelOverrides`, model `id/name/api/baseUrl/reasoning/input/cost/contextWindow/maxTokens/headers/compat`, and OpenAI-compatible API aliases.
* **[Tests]**: Added regressions for custom provider models, OpenAI-compatible API aliasing, provider-level compat/header/baseUrl inheritance, partial built-in model overrides, unknown override ignore behavior, invalid config fallback, and test isolation from ambient user models config.
* **[Docs sync]**: Updated `next.md`, architecture, quality score, and the active baseline execution plan.

### Design Intent

Tau already has `ProviderAuthResolver` and a registry of concrete API implementations, so this slice should not duplicate the upstream TypeScript registry wholesale. The minimal useful step is letting users register and override models without editing C# code while staying inside provider APIs Tau can actually run. Request auth resolution from models.json, dynamic API registration, and OAuth/login semantics remain separate follow-up work.

### Validation

* `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
* `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`
* `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/generate-tau-ai-models.ps1`
* Project-level sequential build/test validation passed for `Tau.Ai`, `Tau.CodingAgent`, `Tau.WebUi`, `Tau.Mom`, `Tau.Ai.Tests`, `Tau.CodingAgent.Tests`, `Tau.Agent.Tests`, `Tau.Tui.Tests`, and `Tau.Pods.Tests`.
* `git diff --check` returned no whitespace errors; only existing CRLF normalization warnings were reported.

### Files Modified

* `src/Tau.Ai/Registry/ModelConfigurationStore.cs`
* `src/Tau.Ai/Registry/ModelCatalog.cs`
* `tests/Tau.Ai.Tests/ModelCatalogTests.cs`
* `tests/Tau.Ai.Tests/OpenAiResponsesProviderTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
