## [2026-05-02 23:05] | Task: models.json request auth/header merge

### Execution Context

* **Agent ID**: `codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### User Query

> 继续 Tau 从 pi-mono 的迁移，补 models.json 的 request auth 与 header 合并。

### Changes Overview

**Scope:** `Tau.Ai` model configuration store, stream entrypoint auth/header resolution, tests, migration plan, quality docs, and next backlog.

**Key Actions:**

* **[Request config]**: Extended `ModelConfigurationStore` with request-time config lookup for provider `apiKey`, `authHeader`, provider headers, model override headers, and custom model headers.
* **[Value resolution]**: Added Tau-side models.json value resolution for literal values, environment variable names, and `!command` stdout values.
* **[Stream merge]**: `StreamFunctions` now merges models.json request auth/headers before dispatching to the concrete provider while preserving explicit runtime options as highest priority.
* **[Tests]**: Added regressions covering env-backed apiKey/header values, provider/model/explicit header precedence, authHeader insertion, and `SimpleStreamOptions` preservation.
* **[Docs sync]**: Updated `next.md`, architecture, quality score, and the active baseline execution plan.

### Design Intent

The previous slice made models discoverable from models.json, but real custom providers also need request-time credentials and headers. This keeps the implementation small: auth/header resolution happens at the shared stream entrypoint, so existing providers keep their current HTTP behavior and all hosts benefit without duplicating config parsing.

### Validation

* `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
* `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`
* Project-level sequential validation is run after this history entry.

### Files Modified

* `src/Tau.Ai/Registry/ModelConfigurationStore.cs`
* `src/Tau.Ai/Providers/StreamFunctions.cs`
* `tests/Tau.Ai.Tests/ModelConfigurationStoreTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`