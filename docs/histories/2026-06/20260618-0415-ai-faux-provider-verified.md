## [2026-06-18 04:15] | Task: close AI Faux provider local row

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 继续完成 AI 和 Agent 迁移到 100%；按当前 `GOAL.md` foundation-first 主线继续推进。

### Changes Overview

**Scope:** `Tau.Ai` public stream options, Faux provider, AI parity docs

**Key Actions:**

* **Stream option surface**: Added `ProviderResponse`, `StreamOptions.Signal`, and `StreamOptions.OnResponse` as Tau-native equivalents for the upstream `signal` and `onResponse` local contract.
* **Faux provider behavior**: `Tau.Ai.Providers.Faux` now invokes the response callback with deterministic `200` metadata and emits an aborted terminal assistant when the signal is cancelled before streaming or between stream chunks.
* **Tests**: Added targeted Faux provider tests for response callback, pre-cancel, and mid-stream cancel behavior; expanded the public API compile sample to cover the new `StreamOptions` members.
* **Docs**: Raised `packages/ai/src/providers/faux.ts` from `ported` to `verified` in the parity matrix and updated `GOAL.md`, `next.md`, `QUALITY_SCORE.md`, README count, and the active plan with the new validation evidence.

### Design Intent

The upstream Faux provider is a deterministic local test provider, so it can be verified without cloud credentials. The closure deliberately keeps real provider/OAuth e2e, `onPayload`, thinking budgets, and provider-wide callback/signal adoption open instead of treating a local Faux contract as real provider parity.

### Files Modified

* `src/Tau.Ai/Abstractions/Options.cs`
* `src/Tau.Ai/Providers/Faux/FauxProvider.cs`
* `tests/Tau.Ai.Tests/FauxProviderTests.cs`
* `tests/Tau.Ai.Tests/AiPublicApiCompileSampleTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/QUALITY_SCORE.md`
* `GOAL.md`
* `next.md`
* `README.md`

### Validation

* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "FauxProviderTests|AiPublicApiCompileSampleTests" --no-restore --verbosity minimal -m:1` passed: 21/21.
* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal -m:1` passed: 402/402.
* `git diff --check` passed with CRLF normalization warnings only.
