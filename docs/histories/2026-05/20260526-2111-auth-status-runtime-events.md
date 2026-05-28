## [2026-05-26 21:11] | Task: Auth status runtime events

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 继续下一轮移植；多 Agent 并行加速，减少低收益单测，把核心 baseline 写进仓库。

### Changes Overview

**Scope:** `Tau.Ai` auth observability and `Tau.WebUi` auth status production path

**Key Actions:**

* **Auth status event contract**: Added targeted coverage for `ProviderAuthResolver.GetStatus(...)` writing `auth/status.checked` through an injected `ITauLogSink`.
* **Safe fields**: The event is intentionally limited to `provider`, `configured`, `source`, `usesOAuth`, and `canLogin`; it does not write status messages or secret values.
* **WebUi production sink**: Registered `ITauLogSink` in `Tau.WebUi` using `JsonlTauLogSink.FromEnvironment()` with a safe fallback to `NullTauLogSink.Instance`.
* **WebChatService injection**: `WebChatService` now builds its `ProviderAuthResolver` from the injected sink, and reuses that resolver for its `ModelCatalog` auth-aware model resolution.
* **Endpoint regression**: Added an HTTP-level WebUi test proving `GET /api/auth/openai` returns the expected status and emits `auth/status.checked` without leaking the test API key.
* **Roadmap sync**: Updated `next.md` and `docs/QUALITY_SCORE.md` so auth status is no longer listed as an observability gap; cross-module correlation remains open.

### Design Intent (Why)

`ProviderAuthResolver.GetStatus(...)` is the shared facts source for CodingAgent, WebUi, and auth-aware model filtering. The narrowest useful migration slice was to keep the event at that source, then ensure WebUi's production endpoint uses the same runtime log sink instead of constructing a resolver with no sink. This avoids duplicating auth-status logging in endpoint handlers or command routers and keeps the payload small enough for runtime JSONL logs.

### Files Modified

* `src/Tau.WebUi/Program.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `tests/Tau.Ai.Tests/ProviderAuthResolverTests.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`

### Validation

* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter FullyQualifiedName~ProviderAuthResolverTests --no-restore --verbosity minimal` passed: 10/10.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --filter FullyQualifiedName~WebUiEndpointTests.AuthEndpoint_ReturnsStatusAndLogsAuthStatus --no-restore --verbosity minimal` passed: 1/1.
* `dotnet build src\Tau.WebUi\Tau.WebUi.csproj --no-restore --verbosity minimal` passed: 0 warnings, 0 errors.
